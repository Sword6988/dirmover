using System.Security;
using DirMover.Core.Diagnostics;
using DirMover.Core.IO;
using DirMover.Core.Registry;

namespace DirMover.Core.Engine;

/// <summary>
/// 编排者：只读规划（<see cref="BuildPlan"/> / <see cref="BuildRestorePlan"/>）与带事务的执行
/// （<see cref="ExecuteAsync"/> / <see cref="RestoreDefaultsAsync"/>）。
/// </summary>
/// <remarks>
/// 单项事务的顺序是<b>写死的</b>，不可配置（架构 §3.7 契约 4）：
/// 执行期重解析点检查（建目录<b>之前</b>）→ 建父目录 → 原子改名（A 路径）→ 否则复制合并 →
/// 全量校验 → 写注册表（本项快照 + 就地回滚）→ 删源前两道闸门 + 一次链接复检 → 删源 → 注销意图。
/// <para>
/// 为什么顺序不能变：任何一步提前，都会出现"注册表已指向目标但数据没校验过"或"源目录已被删
/// 但目标只剩半份"的窗口。事务的意义就在于这个窗口不存在。
/// </para>
/// </remarks>
public sealed class TransferEngine
{
    private readonly RegistryStore _store;
    private readonly string? _backupDir;
    private readonly bool _dryRun;
    private readonly IAppLog _log;

    private string? _lastResolveError;

    /// <summary>构造。</summary>
    /// <param name="store">注册表封装。</param>
    /// <param name="backupDir">备份目录（快照 + 意图日志）；试运行可传 <c>null</c>。</param>
    /// <param name="dryRun">试运行：不写快照、不写意图日志、不写注册表、不动文件。</param>
    /// <param name="log">日志出口。</param>
    public TransferEngine(RegistryStore store, string? backupDir, bool dryRun, IAppLog log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _backupDir = backupDir;
        _dryRun = dryRun;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>注册表封装（<c>--scan</c> 报告与回滚弹窗复用）。</summary>
    public RegistryStore Store => _store;

    /// <summary>是否试运行。</summary>
    public bool DryRun => _dryRun;

    // ================================================================ 只读规划

    /// <summary>
    /// 解析当前位置：权威值名 → 别名 → 系统默认。
    /// </summary>
    /// <param name="spec">文件夹规格。</param>
    /// <remarks>
    /// 读失败<b>不阻断</b>（回退到系统默认，让界面仍能显示一个位置），但会记 warning 并置
    /// <see cref="TransferEngine"/> 内部的"最近解析错误"，随后 <see cref="BuildPlan"/> 把它
    /// 变成该项的 <see cref="PlanItem.InvalidReason"/>（fail-closed）。
    /// </remarks>
    public string ResolveCurrent(FolderSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _lastResolveError = null;

        string raw = _store.GetText(Catalog.UserShellFoldersSubKey, spec.ValueName, expand: false);
        string? readError = _store.ConsumeReadError();

        if (string.IsNullOrWhiteSpace(raw) && spec.Aliases is { Count: > 0 })
        {
            foreach (string alias in spec.Aliases)
            {
                raw = _store.GetText(Catalog.UserShellFoldersSubKey, alias, expand: false);
                readError ??= _store.ConsumeReadError();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(readError))
        {
            _lastResolveError = readError;
            _log.Warn($"{spec.Display}：当前位置读取失败，暂按系统默认位置处理（结果可能不准）：{readError}");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return PathTools.Expand(spec.DefaultPath);
        }

        return PathTools.Expand(raw);
    }

    /// <summary>
    /// 计算需要写入的 (子键, 值名)。
    /// </summary>
    /// <param name="spec">文件夹规格。</param>
    /// <remarks>
    /// 三档口径：①权威值名<b>总写</b>；②别名只在<b>已存在</b>时同步（绝不凭空创建，否则会在
    /// Explorer 里多出一个"看起来有效"的值）；③Shell Folders 是缓存副本，只写其中已存在的同名值。
    /// 三档合起来保证"同一项的所有值同进同退"，不会出现权威值与 GUID 别名各指一处的错乱。
    /// </remarks>
    public IReadOnlyList<RegistryTarget> RegistryTargets(FolderSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var targets = new List<RegistryTarget>(4)
        {
            new(Catalog.UserShellFoldersSubKey, spec.ValueName),
        };

        if (spec.Aliases is { Count: > 0 })
        {
            foreach (string alias in spec.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                if (_store.Exists(Catalog.UserShellFoldersSubKey, alias))
                {
                    AddUnique(targets, new RegistryTarget(Catalog.UserShellFoldersSubKey, alias));
                }

                if (_store.Exists(Catalog.ShellFoldersSubKey, alias))
                {
                    AddUnique(targets, new RegistryTarget(Catalog.ShellFoldersSubKey, alias));
                }
            }
        }

        if (_store.Exists(Catalog.ShellFoldersSubKey, spec.ValueName))
        {
            AddUnique(targets, new RegistryTarget(Catalog.ShellFoldersSubKey, spec.ValueName));
        }

        string? readError = _store.ConsumeReadError();
        if (!string.IsNullOrWhiteSpace(readError))
        {
            _log.Warn($"{spec.Display}：判断别名是否存在时读取失败，可能漏同步别名：{readError}");
        }

        return targets;
    }

    /// <summary>
    /// 只读地构造转移计划。
    /// </summary>
    /// <param name="selectedKeys">勾选的项（按规格表顺序归一）。</param>
    /// <param name="destinationRoot">目标根目录。</param>
    /// <param name="createUserDirectory">是否在目标根下再套一层用户名。</param>
    /// <param name="userName">当前用户名。</param>
    /// <param name="scan">是否扫描目录规模（界面常态为 <c>false</c>）。</param>
    /// <param name="statCache">规模缓存（避免同一次界面刷新重复扫盘）。</param>
    /// <remarks>任何注册表读失败都会变成该项的 <see cref="PlanItem.InvalidReason"/>（fail-closed）。</remarks>
    public IReadOnlyList<PlanItem> BuildPlan(
        IEnumerable<string> selectedKeys,
        string destinationRoot,
        bool createUserDirectory,
        string userName,
        bool scan,
        IReadOnlyDictionary<string, TreeStat>? statCache = null)
    {
        var config = new RunConfig
        {
            DestinationRoot = destinationRoot ?? string.Empty,
            CreateUserDirectory = createUserDirectory,
        };
        config.Normalize();

        List<string> keys = NormalizeKeys(selectedKeys);
        var resolved = new List<(FolderSpec Spec, string Current)>(keys.Count);
        foreach (string key in keys)
        {
            FolderSpec? spec = Catalog.ByKey(key);
            if (spec is null)
            {
                // key 可能来自上一次运行留下的旧数据：忽略，不抛。
                _log.Warn($"忽略未知的文件夹标识：{key}");
                continue;
            }

            resolved.Add((spec, ResolveCurrent(spec)));
        }

        var sources = new List<string>(resolved.Count);
        foreach ((_, string current) in resolved)
        {
            if (!string.IsNullOrWhiteSpace(current))
            {
                sources.Add(current);
            }
        }

        var items = new List<PlanItem>(resolved.Count);
        foreach ((FolderSpec spec, string current) in resolved)
        {
            string target = config.ComposeTarget(spec, current, userName);
            var item = new PlanItem
            {
                Spec = spec,
                Current = current,
                Target = target,
                RegistryPayload = target,
                RegistryTargets = RegistryTargets(spec),
            };

            item.Same = PathTools.SamePath(current, target);
            item.TargetExists = PathTools.Exists(target);
            item.Merge = item.TargetExists && !item.Same;

            // 拷到局部 string：字段不做流收窄，net48 引用程序集又缺 NotNullWhen 注解，
            // 直接传 _lastResolveError 会报 CS8604（net8 侧无感，行为不变）。
            string resolveError = _lastResolveError ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(resolveError))
            {
                item.InvalidReason = UiText.RegistryReadFailed(resolveError);
            }

            if (item.InvalidReason.Length == 0 && !item.Same && item.NeedsMove)
            {
                GuardResult destinationGuard = DestinationGuard.ValidateDestination(target, sources);
                if (!destinationGuard.Ok)
                {
                    item.InvalidReason = destinationGuard.Reason;
                }
                else
                {
                    SourceRiskResult risk = DestinationGuard.CheckSourceTree(current);
                    if (risk.Risk != SourceRisk.None)
                    {
                        item.InvalidReason = risk.Reason;
                    }
                }
            }

            if (scan && !item.Same)
            {
                item.Stat = LookupStat(statCache, spec.Key, current);
                item.Scanned = true;
            }

            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// 只读地构造恢复计划。
    /// </summary>
    /// <param name="selectedKeys">勾选的项。</param>
    /// <param name="scan">是否扫描规模。</param>
    /// <param name="statCache">规模缓存。</param>
    /// <remarks>
    /// 目标 = <see cref="PathTools.Expand"/> 后的系统默认路径（文件操作用），
    /// <see cref="PlanItem.RegistryPayload"/> = <b>未展开</b>的 <c>%USERPROFILE%</c> 形态
    /// （写回注册表时必须与系统原生写法一致）。源目录已不存在 → <see cref="PlanItem.RegistryOnly"/>。
    /// </remarks>
    public IReadOnlyList<PlanItem> BuildRestorePlan(
        IEnumerable<string> selectedKeys,
        bool scan,
        IReadOnlyDictionary<string, TreeStat>? statCache = null)
    {
        List<string> keys = NormalizeKeys(selectedKeys);
        var items = new List<PlanItem>(keys.Count);

        foreach (string key in keys)
        {
            FolderSpec? spec = Catalog.ByKey(key);
            if (spec is null)
            {
                _log.Warn($"忽略未知的文件夹标识：{key}");
                continue;
            }

            string current = ResolveCurrent(spec);
            string target = PathTools.Expand(spec.DefaultPath);

            var item = new PlanItem
            {
                Spec = spec,
                Current = current,
                Target = target,
                RegistryPayload = spec.DefaultPath,
                RegistryTargets = RegistryTargets(spec),
            };

            item.Same = PathTools.SamePath(current, target);
            item.TargetExists = PathTools.Exists(target);
            item.Merge = item.TargetExists && !item.Same;
            item.RegistryOnly = !string.IsNullOrWhiteSpace(current) && !PathTools.Exists(current);

            // 拷到局部 string：字段不做流收窄，net48 引用程序集又缺 NotNullWhen 注解，
            // 直接传 _lastResolveError 会报 CS8604（net8 侧无感，行为不变）。
            string resolveError = _lastResolveError ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(resolveError))
            {
                item.InvalidReason = UiText.RegistryReadFailed(resolveError);
            }
            else if (!item.RegistryOnly && !item.Same && item.NeedsMove)
            {
                GuardResult gate = DestinationGuard.CheckRestoreSource(current, target);
                if (!gate.Ok)
                {
                    item.InvalidReason = gate.Reason;
                }
            }

            if (scan && !item.Same && !item.RegistryOnly)
            {
                item.Stat = LookupStat(statCache, spec.Key, current);
                item.Scanned = true;
            }

            items.Add(item);
        }

        return items;
    }

    // ================================================================ 执行

    /// <summary>
    /// 顺序执行一份计划（单项事务 + 单项边界异常隔离）。
    /// </summary>
    /// <param name="plan">计划（来自 <see cref="BuildPlan"/> / <see cref="BuildRestorePlan"/>）。</param>
    /// <param name="progress">进度出口（可空）。</param>
    /// <param name="observer">项级事件（可空，工作线程回调）。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<TransferReport> ExecuteAsync(
        IReadOnlyList<PlanItem> plan,
        ITransferProgress? progress = null,
        IItemObserver? observer = null,
        CancellationToken ct = default)
    {
        var items = new List<PlanItem>();
        if (plan is not null)
        {
            foreach (PlanItem item in plan)
            {
                if (item is not null)
                {
                    items.Add(item);
                }
            }
        }

        // 取消由引擎自己表达（ItemStatus.Cancelled），不让 OperationCanceledException 穿过边界，
        // 因此这里不把 ct 交给 Task.Run。
        return Task.Run(() => ExecuteCore(items, progress, observer, ct), CancellationToken.None);
    }

    /// <summary>
    /// 恢复系统默认位置：先建恢复计划（扫描规模以启用空间闸门），再走与转移完全相同的执行体。
    /// </summary>
    /// <param name="selectedKeys">勾选的项。</param>
    /// <param name="progress">进度出口。</param>
    /// <param name="observer">项级事件。</param>
    /// <param name="ct">取消令牌。</param>
    public Task<TransferReport> RestoreDefaultsAsync(
        IEnumerable<string> selectedKeys,
        ITransferProgress? progress = null,
        IItemObserver? observer = null,
        CancellationToken ct = default)
    {
        var keys = new List<string>();
        if (selectedKeys is not null)
        {
            foreach (string key in selectedKeys)
            {
                keys.Add(key);
            }
        }

        return Task.Run(
            () =>
            {
                IReadOnlyList<PlanItem> plan = BuildRestorePlan(keys, scan: true);
                var items = new List<PlanItem>(plan);
                return ExecuteCore(items, progress, observer, ct);
            },
            CancellationToken.None);
    }

    // ================================================================ 执行内核

    private TransferReport ExecuteCore(
        IReadOnlyList<PlanItem> plan,
        ITransferProgress? progress,
        IItemObserver? observer,
        CancellationToken ct)
    {
        var report = new TransferReport { StartedAt = DateTimeOffset.Now };
        IIntentJournal journal = IntentJournal.Open(_backupDir, enabled: !_dryRun, _log);

        try
        {
            if (!_dryRun)
            {
                var targets = new List<RegistryTarget>();
                foreach (PlanItem item in plan)
                {
                    if (item.RegistryTargets is { Count: > 0 })
                    {
                        targets.AddRange(item.RegistryTargets);
                    }
                }

                RegistrySnapshot snapshot = _store.Snapshot(targets);
                string? snapshotPath = _store.SaveSnapshot(_backupDir ?? string.Empty, snapshot);
                report.SnapshotPath = snapshotPath;
                if (snapshotPath is null)
                {
                    // 不阻断，但必须可见：没有快照就没有"一键回滚"，用户得知道。
                    report.Notes.Add(UiText.SnapshotNotSaved);
                    _log.Warn(UiText.SnapshotNotSaved);
                }

                journal.Begin(plan, snapshotPath);
            }

            int total = plan.Count;
            for (int index = 0; index < total; index++)
            {
                PlanItem item = plan[index];

                if (report.Cancelled || ct.IsCancellationRequested)
                {
                    report.Cancelled = true;
                    observer?.OnItemStarted(index + 1, total, item);
                    ItemOutcome cancelled = CancelledOutcome(item);
                    report.Outcomes.Add(cancelled);
                    observer?.OnItemFinished(index + 1, total, item, cancelled);
                    continue;
                }

                observer?.OnItemStarted(index + 1, total, item);

                ItemOutcome outcome;
                try
                {
                    outcome = RunItem(item, journal, progress, ct);
                }
                catch (Exception ex)
                {
                    // 单项边界：任何未预期异常都只让这一项失败，不中断整轮。
                    _log.Error($"{item.Display}：处理过程中发生未预期错误", ex);
                    outcome = Make(
                        item,
                        ItemStatus.Failed,
                        $"{item.Display}：发生未预期错误（{ex.Message}），源目录已保留；详情见日志文件",
                        TransferStage.UnexpectedError);
                    journal.Mark(item, TransferStage.UnexpectedError);
                    journal.Finish(item);
                }

                report.Outcomes.Add(outcome);
                observer?.OnItemFinished(index + 1, total, item, outcome);

                if (outcome.Status == ItemStatus.Cancelled)
                {
                    report.Cancelled = true;
                }
            }
        }
        finally
        {
            // 收尾：Pending / Cancelled 一律注销，避免下次启动把"正常取消"误报成"未正常收尾"。
            journal.CloseRun();
            report.FinishedAt = DateTimeOffset.Now;
        }

        return report;
    }

    private ItemOutcome RunItem(
        PlanItem item,
        IIntentJournal journal,
        ITransferProgress? progress,
        CancellationToken ct)
    {
        if (_dryRun)
        {
            return DryRunItem(item);
        }

        if (item.Same)
        {
            journal.Finish(item);
            return Make(item, ItemStatus.Skipped, UiText.StatusSame, TransferStage.Pending);
        }

        // 界面常态不扫描规模；真正动手前必须补一次，否则空间闸门拿不到内容体积（等于没闸门）。
        if (!item.Scanned && !item.RegistryOnly && item.NeedsMove)
        {
            item.Stat = FsOps.ScanTree(item.Current);
            item.Scanned = true;
        }

        string blocked = item.BlockingReason();
        if (blocked.Length > 0)
        {
            journal.Finish(item);
            return Make(item, ItemStatus.Failed, blocked, TransferStage.Pending);
        }

        if (item.RegistryOnly)
        {
            (bool Ok, string Message) registryOnlyWrite = WriteRegistry(item);
            if (!registryOnlyWrite.Ok)
            {
                journal.Finish(item);
                return Make(item, ItemStatus.Failed, registryOnlyWrite.Message, TransferStage.RegistryFailed);
            }

            journal.Mark(item, TransferStage.RegistryWritten);
            journal.Finish(item);
            return Make(item, ItemStatus.Skipped, UiText.StatusRegistryOnly, TransferStage.RegistryWritten);
        }

        if (ct.IsCancellationRequested)
        {
            journal.Mark(item, TransferStage.Cancelled);
            return CancelledOutcome(item);
        }

        journal.Mark(item, TransferStage.Pending);

        // 执行期重解析点闸：必须在建目录**之前**（规划到执行之间可达分钟级时间窗）。
        string linkAncestor = DestinationGuard.ReparseAncestor(item.Target);
        if (linkAncestor.Length > 0)
        {
            journal.Finish(item);
            return Make(item, ItemStatus.Failed, UiText.TargetIsLink(linkAncestor), TransferStage.CopyFailed);
        }

        string? parentDirectory = Path.GetDirectoryName(item.Target);
        OpResult parentEnsured = FsOps.EnsureDirectory(parentDirectory ?? string.Empty);
        if (!parentEnsured.Ok)
        {
            journal.Finish(item);
            return Make(
                item,
                ItemStatus.Failed,
                UiText.CopyFailed(parentEnsured.Message, item.Target),
                TransferStage.CopyFailed);
        }

        var scope = new ItemProgressScope(progress, item.Key, item.Display);
        CopyResult? copy = null;
        bool renamed = false;

        // 路径 A：同卷 + 目标不存在 → 原子改名（不产生第二份数据，因此无需复制校验）。
        MoveResult move = FsOps.TryAtomicMove(item.Current, item.Target);
        if (move.Moved)
        {
            renamed = true;
        }
        else
        {
            journal.Mark(item, TransferStage.Copying);
            copy = FsOps.CopyMerge(item.Current, item.Target, scope, ct);
            if (copy.Cancelled)
            {
                journal.Mark(item, TransferStage.Cancelled);
                return CancelledOutcome(item);
            }

            if (!copy.Ok)
            {
                journal.Finish(item);
                return Make(
                    item,
                    ItemStatus.Failed,
                    UiText.CopyFailed(FirstError(copy), item.Target),
                    TransferStage.CopyFailed);
            }

            journal.Mark(item, TransferStage.Copied);

            VerifyResult verify = FsOps.VerifyCopy(item.Current, item.Target, copy, scope, ct);
            if (verify.Cancelled)
            {
                journal.Mark(item, TransferStage.Cancelled);
                return CancelledOutcome(item);
            }

            if (!verify.Verified)
            {
                journal.Finish(item);
                return Make(
                    item,
                    ItemStatus.Failed,
                    UiText.VerifyFailed(verify.Detail),
                    TransferStage.VerifyFailed);
            }
        }

        // 写注册表：本项快照 + 就地回滚（多值同进同退）。
        (bool Ok, string Message) written = WriteRegistry(item);
        if (!written.Ok)
        {
            string message = written.Message;
            if (renamed)
            {
                MoveResult back = FsOps.TryAtomicMove(item.Target, item.Current);
                message += "；" + (back.Moved
                    ? UiText.MovedBackToCurrent(item.Target, item.Current)
                    : UiText.DataAtTargetNoRegistryChange(item.Target));
            }
            else
            {
                message += "；" + UiText.SourceKeptAtTarget(item.Target);
            }

            journal.Finish(item);
            return Make(item, ItemStatus.Failed, message, TransferStage.RegistryFailed);
        }

        journal.Mark(item, TransferStage.RegistryWritten);

        if (renamed)
        {
            // rename 路径：源就是目标，不存在"删源"这一步。
            journal.Mark(item, TransferStage.SourceRemoved);
            journal.Finish(item);
            return Make(item, ItemStatus.Ok, string.Empty, TransferStage.SourceRemoved);
        }

        // 删源前第一道闸门：物理同位置（junction / subst 绕过的最后拦截点）。
        if (PathTools.ResolvesToSame(item.Current, item.Target))
        {
            journal.Finish(item);
            return Make(
                item,
                ItemStatus.Ok,
                UiText.SamePhysicalLocation(item.Current, item.Target),
                TransferStage.RegistryWritten,
                copy?.CopiedFiles ?? 0,
                copy?.CopiedBytes ?? 0);
        }

        if (ct.IsCancellationRequested)
        {
            // 注册表已写、源未删：这是半完成状态，必须留在意图日志里（下次启动仍要提示）。
            return Make(
                item,
                ItemStatus.Cancelled,
                UiText.CancelledAfterRegistry(item.Current),
                TransferStage.RegistryWritten,
                copy?.CopiedFiles ?? 0,
                copy?.CopiedBytes ?? 0);
        }

        // 删源前第二道闸门：重新清点源，指纹必须与清单一致。
        string? changed = DescribeSourceChange(item, copy);
        if (changed is not null)
        {
            journal.Finish(item);
            return Make(
                item,
                ItemStatus.Ok,
                changed,
                TransferStage.RegistryWritten,
                copy?.CopiedFiles ?? 0,
                copy?.CopiedBytes ?? 0);
        }

        // 删源前第三道闸门（审计 A-11 的防御性加固）：确认目标路径与其上级此刻仍未变成
        // 链接/联接点。规划期与执行期开头各查过一次，但那之后还有"复制 + 校验 + 写注册表"
        // 一整段耗时操作，`mklink /J` 不需要管理员权限，足以在这个窗口里把目标的某一级上级
        // 换成指向别处的联接点 —— 那时"数据已写到链接指向的位置"，再删源就是真丢数据。
        //
        // 这**缩小**但不**关闭**窗口：本检查与 SafeRemoveDirectory 之间仍有毫秒级间隙，
        // 真正关闭需要 FILE_FLAG_OPEN_REPARSE_POINT 的句柄级实现（打开即锁定、按句柄操作），
        // 留档见 docs/04-redline-coverage.md 的"已知未覆盖分支 ⑥"（该分支本身已有确定性
        // 用例：SafetyRedLineEngineTests.RedLine11_...；未覆盖的只是本检查与删除之间的间隙）。
        string lateLink = DestinationGuard.ReparseAncestor(item.Target);
        if (lateLink.Length > 0)
        {
            // 与"删源失败"同一条记账路径：注册表已写、源保留 = 半完成状态，
            // 必须留在意图日志里让下次启动继续提示（故只 Mark，不 Finish）。
            journal.Mark(item, TransferStage.SourceRemovalFailed);
            return Make(
                item,
                ItemStatus.Failed,
                UiText.TargetBecameLinkBeforeRemove(lateLink, item.Current),
                TransferStage.SourceRemovalFailed,
                copy?.CopiedFiles ?? 0,
                copy?.CopiedBytes ?? 0);
        }

        OpResult removed = FsOps.SafeRemoveDirectory(item.Current);
        if (!removed.Ok)
        {
            journal.Mark(item, TransferStage.SourceRemovalFailed);
            return Make(
                item,
                ItemStatus.Failed,
                UiText.SourceRemoveFailed(removed.Message, item.Current),
                TransferStage.SourceRemovalFailed,
                copy?.CopiedFiles ?? 0,
                copy?.CopiedBytes ?? 0);
        }

        journal.Mark(item, TransferStage.SourceRemoved);
        journal.Finish(item);
        return Make(
            item,
            ItemStatus.Ok,
            string.Empty,
            TransferStage.SourceRemoved,
            copy?.CopiedFiles ?? 0,
            copy?.CopiedBytes ?? 0);
    }

    private ItemOutcome DryRunItem(PlanItem item)
    {
        if (item.Same)
        {
            return Make(item, ItemStatus.Skipped, UiText.DryRunOutcome(UiText.StatusSame), TransferStage.Pending);
        }

        string blocked = item.BlockingReason();
        if (blocked.Length > 0)
        {
            return Make(item, ItemStatus.Failed, UiText.DryRunOutcome(blocked), TransferStage.Pending);
        }

        if (item.RegistryOnly)
        {
            return Make(
                item,
                ItemStatus.Skipped,
                UiText.DryRunOutcome(UiText.StatusRegistryOnly),
                TransferStage.RegistryWritten);
        }

        return Make(
            item,
            ItemStatus.Ok,
            UiText.DryRunOutcome(item.Describe()),
            TransferStage.SourceRemoved,
            item.Stat.Files,
            item.Stat.Bytes);
    }

    /// <summary>
    /// 写注册表：本项任一值失败 → 用<b>本项</b>快照就地回滚，并报出跳过条数。
    /// </summary>
    /// <remarks>
    /// "本项快照"而不是"整轮快照"：整轮回滚会把此前已成功的项一起滚回去，让它们指向不存在
    /// 的目录。范围必须精确到当前项的这几个值。
    /// </remarks>
    private (bool Ok, string Message) WriteRegistry(PlanItem item)
    {
        if (item.RegistryTargets is not { Count: > 0 })
        {
            return (true, string.Empty);
        }

        RegistrySnapshot itemSnapshot = _store.Snapshot(item.RegistryTargets);
        foreach (RegistryTarget target in item.RegistryTargets)
        {
            try
            {
                _store.SetText(target.SubKey, target.Name, item.RegistryPayload ?? string.Empty);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or SecurityException
                or InvalidOperationException
                or NotSupportedException)
            {
                (int Restored, int Removed) rolledBack = _store.Rollback(itemSnapshot);
                int skipped = _store.LastRollbackSkipped;
                string message = UiText.RegistryWriteFailed(
                    target.SubKey,
                    target.Name,
                    ex.Message,
                    rolledBack.Restored + rolledBack.Removed);

                if (skipped > 0)
                {
                    message += $"（另有 {skipped} 处因快照期读取失败、原值未知未能回滚，需人工核对）";
                }

                _log.Warn(message);
                return (false, message);
            }
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// 删源前第二道闸门：重新清点源并与清单逐项比 (size, mtime)。
    /// </summary>
    /// <returns>一致返回 <c>null</c>；不一致返回可直接展示的说明。</returns>
    /// <remarks>
    /// <b>这里的"重复枚举"是刻意冗余，不是漏优化（审计 A-07 已评估，结论：保持不动）。</b>
    /// 一次完整的"复制 → 校验 → 删源"里，源树确实被完整枚举了两次：
    /// ① <see cref="FsOps.VerifyCopy"/> 内部（校验维度）以清单为基准比对；
    /// ② 本方法（删源前临时再读一次）。
    /// <para>
    /// 为什么不能用①的结论替代②：①的回答是"目标副本与清单一致吗"，②的回答是"源<b>现在</b>还是
    /// 复制时那份吗"。把①的快照拿来复用，等于把闸门降级成"复述上一次的结论"——中间那段
    /// （校验结束 → 写注册表 → 删源）恰好是用户程序仍在往源目录写文件的时间窗，
    /// 一旦源在校验后新增/改写了文件，复用结论就会把"新写进去的东西"连同源目录一起删掉。
    /// 这是安全语义的实质性弱化，因此<b>不接受合并</b>。
    /// </para>
    /// <para>
    /// 成本量级（本机实测，2026-11-13，10 000 个文件 / 100 个目录、%TEMP% 下 NTFS）：
    /// 本方法一次 <see cref="FsOps.EnumerateSource"/> 中位数约 <b>2.6 s</b>；其中纯目录遍历
    /// 约 0.27 s（= <see cref="FsOps.ScanTree"/> 同规模的实测值），其余约 2.3 s 花在
    /// <b>每个文件一次</b>的 <c>CreateFileW + GetFileInformationByHandle</c> 上
    /// （<see cref="FileFingerprint"/> 要拿 VolumeSerial/FileIndex；本机开着实时防护，单文件约
    /// 250 µs）。同一棵树的文件内容读一遍约 2.1 s，所以第二次清点的量级与"再读一遍文件"
    /// 同级，远小于一次真实的"复制 + 校验 + 删源"（真实用户目录是 GB 级，秒到分钟级）。
    /// </para>
    /// <para>
    /// 另外：本方法<b>不读任何文件内容</b>（<c>FingerprintOf(..., withDigest: false)</c>），
    /// 只做元数据遍历；<c>withDigest: true</c> 的 SHA256 只在复制阶段边读边算一次。
    /// </para>
    /// <para>
    /// <b>边界（别把它读成"永远可以忽略"）</b>："远小于一次真实复制"这个结论只在
    /// <b>千级～万级文件、平均体积不小</b>的场景成立。代价是按<b>文件个数</b>线性增长的
    /// （≈246 µs/文件，本机实测）：<b>海量小文件</b>时它可能反而超过复制本身——
    /// 10 万个小文件约 25 s，而同样 10 万个 1 KB 文件的复制（含校验）也在同一量级。
    /// 那种场景要降本，正确方向是"只取 size/mtime、不取 inode 的轻量清点"（实测 10k 文件
    /// 0.27 s），而<b>不是</b>删掉这次重读——删掉等于把闸门降级，见本节第一段。
    /// </para>
    /// </remarks>
    private string? DescribeSourceChange(PlanItem item, CopyResult? copy)
    {
        if (copy is null)
        {
            return null;
        }

        SourceTree fresh;
        try
        {
            fresh = FsOps.EnumerateSource(item.Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"删源前重新清点源目录失败（{ex.Message}）；为安全起见保留源目录：{item.Current}";
        }

        if (fresh.Errors.Count > 0)
        {
            return $"删源前重新清点源目录时有 {fresh.Errors.Count} 项读不到；为安全起见保留源目录：{item.Current}";
        }

        var manifest = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (CopyItem copyItem in copy.Items)
        {
            manifest[copyItem.Rel] = copyItem.Fingerprint;
        }

        var manifestDirs = new HashSet<string>(copy.Dirs, StringComparer.OrdinalIgnoreCase);
        var manifestLinks = new HashSet<string>(copy.Links, StringComparer.OrdinalIgnoreCase);

        int added = 0;
        int removed = 0;
        int modified = 0;

        foreach (KeyValuePair<string, FileFingerprint> entry in fresh.Files)
        {
            if (!manifest.TryGetValue(entry.Key, out FileFingerprint? known))
            {
                added++;
            }
            else if (known.Size != entry.Value.Size || known.ModifiedTicksUtc != entry.Value.ModifiedTicksUtc)
            {
                modified++;
            }
        }

        foreach (string rel in manifest.Keys)
        {
            if (!fresh.Files.ContainsKey(rel))
            {
                removed++;
            }
        }

        foreach (string rel in fresh.Dirs)
        {
            if (!manifestDirs.Contains(rel))
            {
                added++;
            }
        }

        foreach (string rel in fresh.Links)
        {
            if (!manifestLinks.Contains(rel))
            {
                added++;
            }
        }

        if (added == 0 && removed == 0 && modified == 0)
        {
            return null;
        }

        return UiText.SourceChangedDuringCopy(added, removed, modified, item.Current, item.Target);
    }

    // ================================================================ 小工具

    private static ItemOutcome Make(
        PlanItem item,
        ItemStatus status,
        string message,
        TransferStage stage,
        int files = 0,
        long bytes = 0) => new()
        {
            Key = item.Key,
            Display = item.Display,
            Status = status,
            Message = message ?? string.Empty,
            Source = item.Current ?? string.Empty,
            Target = item.Target ?? string.Empty,
            Files = files,
            CopiedBytes = bytes,
            Stage = stage,
        };

    private static ItemOutcome CancelledOutcome(PlanItem item) => Make(
        item,
        ItemStatus.Cancelled,
        UiText.CancelledItem,
        TransferStage.Cancelled);

    private static string FirstError(CopyResult copy)
    {
        if (copy.Errors.Count == 0)
        {
            // 文案唯一出口原则：这一句同样必须来自 UiText（审计 A-14）。
            return UiText.UnknownReason;
        }

        string first = copy.Errors[0];
        return copy.Errors.Count == 1
            ? first
            : $"{first}（另有 {copy.Errors.Count - 1} 项失败，详见日志）";
    }

    private TreeStat LookupStat(
        IReadOnlyDictionary<string, TreeStat>? statCache,
        string key,
        string current)
    {
        if (statCache is not null && statCache.TryGetValue(key, out TreeStat? cached) && cached is not null)
        {
            return cached;
        }

        return FsOps.ScanTree(current);
    }

    private static void AddUnique(List<RegistryTarget> targets, RegistryTarget candidate)
    {
        foreach (RegistryTarget existing in targets)
        {
            if (RegistryTargetComparer.Instance.Equals(existing, candidate))
            {
                return;
            }
        }

        targets.Add(candidate);
    }

    /// <summary>按 <see cref="Catalog.AllKeys"/> 的顺序归一勾选集合（去重、去空、忽略大小写）。</summary>
    private static List<string> NormalizeKeys(IEnumerable<string> selectedKeys)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        if (selectedKeys is null)
        {
            return ordered;
        }

        foreach (string key in selectedKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                continue;
            }

            ordered.Add(key);
        }

        ordered.Sort(static (left, right) => IndexOfKey(left).CompareTo(IndexOfKey(right)));
        return ordered;
    }

    private static int IndexOfKey(string key)
    {
        IReadOnlyList<string> all = Catalog.AllKeys;
        for (int index = 0; index < all.Count; index++)
        {
            if (string.Equals(all[index], key, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}
