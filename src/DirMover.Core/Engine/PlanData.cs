using DirMover.Core.IO;
using DirMover.Core.Registry;

namespace DirMover.Core.Engine;

/// <summary>
/// 单项事务的阶段。登记到意图日志（<c>backups\intent.json</c>），也是启动时回滚范围裁剪的依据。
/// </summary>
/// <remarks>
/// 11 个成员与 <see cref="UiText.StageLabel"/> 一一对应（PRD §4.3-3 固定 11 种阶段标签）。
/// 之所以要区分"复制校验完成，源目录未删除"和"注册表已更新，源目录未删除"：
/// 前者源目录是唯一副本，后者注册表已指向目标、源目录成了冗余副本——人工处置动作完全不同。
/// </remarks>
public enum TransferStage
{
    /// <summary>未开始。</summary>
    Pending,

    /// <summary>正在复制（目标可能只写入一部分）。</summary>
    Copying,

    /// <summary>复制校验完成，源目录未删除。</summary>
    Copied,

    /// <summary>注册表已更新，源目录未删除。</summary>
    RegistryWritten,

    /// <summary>源目录已删除（该项已完全落实，无需回滚）。</summary>
    SourceRemoved,

    /// <summary>已取消（正常收尾，不得在下次启动被误报为"未正常收尾"）。</summary>
    Cancelled,

    /// <summary>复制失败。</summary>
    CopyFailed,

    /// <summary>复制校验未通过。</summary>
    VerifyFailed,

    /// <summary>注册表写入失败（本项已写入的值已就地回滚）。</summary>
    RegistryFailed,

    /// <summary>源目录删除失败（数据已校验通过，重启后手动删除即可）。</summary>
    SourceRemovalFailed,

    /// <summary>发生未预期错误（异常被单项边界捕获）。</summary>
    UnexpectedError,
}

/// <summary>单项的最终结论。</summary>
public enum ItemStatus
{
    /// <summary>尚未处理。</summary>
    Pending,

    /// <summary>完成。</summary>
    Ok,

    /// <summary>跳过（路径相同无需转移，或"仅更新注册表"）。</summary>
    Skipped,

    /// <summary>失败（原因必须逐项上报，不得只报"失败"）。</summary>
    Failed,

    /// <summary>已取消（源未删、注册表未写）。</summary>
    Cancelled,
}

/// <summary>
/// 一项转移/恢复的完整规划结果。只读规划与执行之间靠它传递全部判定。
/// </summary>
/// <remarks>
/// <see cref="BlockingReason"/> 的判定顺序是写死的（PRD §6.1 与红线 P0-8）：
/// 顺序错了会让"空间不足"掩盖"路径相同"，或者让"同物理位置"被空间闸门提前拦下，
/// 于是用户看到的下一步动作与实际危险完全不同。
/// </remarks>
public sealed class PlanItem
{
    /// <summary>静态规格。</summary>
    public required FolderSpec Spec { get; init; }

    /// <summary>当前位置（注册表解析后<b>已展开</b>）。</summary>
    public required string Current { get; init; }

    /// <summary>目标绝对路径（已合成）。</summary>
    public required string Target { get; init; }

    /// <summary>写入注册表的值（转移 = 目标绝对路径；恢复 = 保留 <c>%USERPROFILE%</c> 的原始形态）。</summary>
    public string RegistryPayload { get; init; } = string.Empty;

    /// <summary>需要写入的 (子键, 值名) 集合（权威名 + 已存在的别名 + Shell Folders 中已存在的同名值）。</summary>
    public IReadOnlyList<RegistryTarget> RegistryTargets { get; init; } = Array.Empty<RegistryTarget>();

    /// <summary>当前位置与目标是否同一路径（→「路径相同，无需转移」）。</summary>
    public bool Same { get; set; }

    /// <summary>目标路径当前是否已存在（→ 将合并）。</summary>
    public bool TargetExists { get; set; }

    /// <summary>是否合并（目标已存在且非空）。</summary>
    public bool Merge { get; set; }

    /// <summary>是否已扫描过规模（界面常态为未扫描）。</summary>
    public bool Scanned { get; set; }

    /// <summary>恢复专用：源已不存在，只修注册表，不动任何文件。</summary>
    public bool RegistryOnly { get; set; }

    /// <summary>规模统计。</summary>
    public TreeStat Stat { get; set; } = new();

    /// <summary>规划期就已判定的不可执行原因（禁区 / 注册表读失败）；空串表示规划期无异议。</summary>
    public string InvalidReason { get; set; } = string.Empty;

    /// <summary>稳定标识。</summary>
    public string Key => Spec.Key;

    /// <summary>界面显示名。</summary>
    public string Display => Spec.Display;

    /// <summary>是否真的需要搬动（非相同、且源与目标都有值）。</summary>
    public bool NeedsMove =>
        !Same && !string.IsNullOrWhiteSpace(Current) && !string.IsNullOrWhiteSpace(Target);

    /// <summary>状态列文案（委托 <see cref="UiText.DescribePlan(PlanItem)"/>）。</summary>
    public string Describe() => UiText.DescribePlan(this);

    /// <summary>
    /// 是否被阻断。<b>空串 = 可执行</b>。
    /// </summary>
    /// <remarks>
    /// 顺序（不可调整）：
    /// ① <see cref="Same"/> → 不算阻断（由调用方按"跳过"处理）；
    /// ② 当前位置为空 → 阻断（"位置未知"不能当成"无需转移"）；
    /// ③ 当前位置不存在 → 阻断（恢复路径的 <see cref="RegistryOnly"/> 例外，源本就不存在）；
    /// ④ 规划期原因（禁区 / 注册表读失败）；⑤ 同物理位置 → <b>放行</b>（删源前还会再拦一次）；
    /// ⑥ 有不可读项 → 阻断（规模被低估）；⑦ 空间闸门（同卷 rename 完全豁免）。
    /// </remarks>
    public string BlockingReason()
    {
        if (Same)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(Current))
        {
            return UiText.CurrentUnknown();
        }

        if (!RegistryOnly && !PathTools.Exists(Current))
        {
            return UiText.CurrentMissing(Current);
        }

        if (!string.IsNullOrWhiteSpace(InvalidReason))
        {
            return InvalidReason;
        }

        if (!string.IsNullOrWhiteSpace(Target) && PathTools.ResolvesToSame(Current, Target))
        {
            // 放行：真正的拦截点在引擎"删源前第一道闸门"，那时才需要保留源目录并写注册表。
            return string.Empty;
        }

        if (Stat.Unreadable > 0)
        {
            return UiText.UnreadableItems(Stat.Unreadable);
        }

        if (RegistryOnly || string.IsNullOrWhiteSpace(Target))
        {
            return string.Empty;
        }

        // 同卷 + 目标不存在 ⇒ 走 rename，完全不产生新数据，空间校验必须整体跳过。
        bool renamePath = !TargetExists &&
            string.Equals(
                PathTools.VolumeKey(Current),
                PathTools.VolumeKey(Target),
                StringComparison.Ordinal) &&
            PathTools.VolumeKey(Target).Length > 0;

        if (renamePath)
        {
            return string.Empty;
        }

        long? available = PathTools.FreeSpace(Target);
        if (available is null)
        {
            // 绝不用 0 代替"未知"：那会让"读不到"变成"无需检查"，一路复制到磁盘写满。
            return UiText.SpaceUnknown;
        }

        long needed = Math.Max(0, Stat.Bytes);
        double raw = (needed * Catalog.SpaceMarginRatio) + Catalog.SpaceMarginBytes;
        long recommended = raw >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(raw);

        if (available.Value < recommended)
        {
            return UiText.SpaceInsufficient(
                PathTools.HumanSize(needed),
                PathTools.HumanSize(recommended),
                PathTools.HumanSize(available.Value));
        }

        return string.Empty;
    }
}

/// <summary>一项的最终结论（结果弹窗逐行展示）。</summary>
public sealed class ItemOutcome
{
    /// <summary>稳定标识。</summary>
    public required string Key { get; init; }

    /// <summary>显示名。</summary>
    public required string Display { get; init; }

    /// <summary>结论。</summary>
    public required ItemStatus Status { get; init; }

    /// <summary>说明（失败时必须是"原因 + 下一步"，不得只写"失败"）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>源路径。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>目标路径。</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>涉及文件数。</summary>
    public int Files { get; init; }

    /// <summary>实际写入字节数。</summary>
    public long CopiedBytes { get; init; }

    /// <summary>
    /// 结束时所在阶段。架构文档未在类图里列出，但 T11 验收项②要求能断言
    /// <c>Stage=RegistryFailed</c>，且界面行着色也需要它，故补充为只读属性。
    /// </summary>
    public TransferStage Stage { get; init; } = TransferStage.Pending;

    /// <summary>状态短标签（完成 / 失败 / 跳过 / 已取消）。</summary>
    public string Label => UiText.StatusLabel(Status);
}

/// <summary>一轮执行的汇总报告。</summary>
public sealed class TransferReport
{
    /// <summary>逐项结论（顺序与计划一致）。</summary>
    public List<ItemOutcome> Outcomes { get; } = new();

    /// <summary>注册表快照路径；落盘失败或试运行时为 <c>null</c>。</summary>
    public string? SnapshotPath { get; set; }

    /// <summary>开始时间。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>结束时间。</summary>
    public DateTimeOffset FinishedAt { get; set; }

    /// <summary>是否被用户取消。</summary>
    public bool Cancelled { get; set; }

    /// <summary>附加提示（如"注册表备份未能落盘，本次无法一键回滚"）。</summary>
    public List<string> Notes { get; } = new();

    /// <summary>失败项。</summary>
    public IReadOnlyList<ItemOutcome> Failed =>
        Outcomes.Where(static o => o.Status == ItemStatus.Failed).ToList();

    /// <summary>成功项。</summary>
    public IReadOnlyList<ItemOutcome> Succeeded =>
        Outcomes.Where(static o => o.Status == ItemStatus.Ok).ToList();

    /// <summary>跳过项（路径相同 / 仅更新注册表）。单独计数，<b>不</b>混进完成数。</summary>
    public int SkippedCount => Outcomes.Count(static o => o.Status == ItemStatus.Skipped);

    /// <summary>整体是否成功（无失败项且未被取消）。跳过不算失败。</summary>
    public bool Ok =>
        !Cancelled && Outcomes.All(static o => o.Status is ItemStatus.Ok or ItemStatus.Skipped);

    /// <summary>结果摘要（委托 <see cref="UiText.Summarize(TransferReport)"/>）。</summary>
    public string Summary() => UiText.Summarize(this);
}

/// <summary>一条进度快照（文件级 + 字节级 + 当前相对路径）。</summary>
/// <param name="ItemKey">项稳定标识。</param>
/// <param name="Display">项显示名。</param>
/// <param name="DoneInItem">本项已完成文件数。</param>
/// <param name="TotalInItem">本项文件总数。</param>
/// <param name="BytesCopied">本项已处理字节数。</param>
/// <param name="CurrentRelativePath">当前处理中的相对路径。</param>
public readonly record struct TransferProgressInfo(
    string ItemKey,
    string Display,
    int DoneInItem,
    int TotalInItem,
    long BytesCopied,
    string CurrentRelativePath);

/// <summary>
/// 进度出口。
/// </summary>
/// <remarks>
/// 用 <c>in</c> 传值：进度回调在 1 MiB 分块粒度上被调用，按值传结构体会在热路径上多一次拷贝。
/// </remarks>
public interface ITransferProgress
{
    /// <summary>上报一条进度。</summary>
    /// <param name="info">进度快照。</param>
    void Report(in TransferProgressInfo info);
}

/// <summary>
/// 把 <see cref="ITransferProgress"/> 桥接到 <see cref="IProgress{T}"/>。
/// </summary>
/// <remarks>
/// UI 侧应当用 <c>new Progress&lt;TransferProgressInfo&gt;(...)</c>（在 UI 线程构造），
/// 这样回调天然回到 UI 线程，Core 里不需要任何 DispatcherQueue 依赖。
/// </remarks>
public sealed class TransferProgressBridge : ITransferProgress
{
    private readonly IProgress<TransferProgressInfo> _inner;

    /// <summary>构造。</summary>
    /// <param name="inner">目标 <see cref="IProgress{T}"/>。</param>
    public TransferProgressBridge(IProgress<TransferProgressInfo> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public void Report(in TransferProgressInfo info) => _inner.Report(info);
}

/// <summary>
/// 节流包装：同一 100 ms 窗口内的中间进度合并，但<b>最后一项永远放行</b>。
/// </summary>
/// <remarks>
/// 为什么"最后一项"要豁免：否则界面可能永远停在 <c>n-1/N</c>，被用户当成卡死。
/// 节流逻辑放在 Core 而不是 UI，是为了它能被单测钉住（UI 里的节流只能靠手点）。
/// </remarks>
public sealed class ThrottledTransferProgress : ITransferProgress
{
    private readonly ITransferProgress _inner;
    private readonly long _minIntervalTicks;
    private long _lastTicks;

    /// <summary>构造。</summary>
    /// <param name="inner">下游出口。</param>
    /// <param name="minInterval">最小间隔（默认 <see cref="Catalog.ProgressThrottle"/>）。</param>
    public ThrottledTransferProgress(ITransferProgress inner, TimeSpan? minInterval = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _minIntervalTicks = (minInterval ?? Catalog.ProgressThrottle).Ticks;
    }

    /// <inheritdoc />
    public void Report(in TransferProgressInfo info)
    {
        bool isFinal = info.TotalInItem > 0 && info.DoneInItem >= info.TotalInItem;
        long now = DateTime.UtcNow.Ticks;

        if (!isFinal && _lastTicks != 0 && now - _lastTicks < _minIntervalTicks)
        {
            return;
        }

        _lastTicks = now;
        _inner.Report(info);
    }
}

/// <summary>
/// 项级事件（UI 用于行着色与状态栏）。<b>在调用线程上触发</b>，实现方自行切回 UI 线程。
/// </summary>
public interface IItemObserver
{
    /// <summary>一项开始。</summary>
    /// <param name="index">序号（从 1 开始）。</param>
    /// <param name="total">总数。</param>
    /// <param name="item">规划项。</param>
    void OnItemStarted(int index, int total, PlanItem item);

    /// <summary>一项结束。</summary>
    /// <param name="index">序号（从 1 开始）。</param>
    /// <param name="total">总数。</param>
    /// <param name="item">规划项。</param>
    /// <param name="outcome">结论。</param>
    void OnItemFinished(int index, int total, PlanItem item, ItemOutcome outcome);
}

/// <summary>
/// 把"项标识/显示名"注入到 <see cref="FsOps"/> 上报的进度里的内部包装。
/// </summary>
/// <remarks>
/// <c>FsOps.CopyMerge</c> 只知道相对路径，不知道"这是哪一项"，而它也不想依赖 <see cref="PlanItem"/>
/// （IO 层不该反向依赖引擎的数据结构）。所以由引擎在外面套一层，负责补两个字段。
/// </remarks>
internal sealed class ItemProgressScope : ITransferProgress
{
    private readonly ITransferProgress? _inner;
    private readonly string _key;
    private readonly string _display;

    internal ItemProgressScope(ITransferProgress? inner, string key, string display)
    {
        _inner = inner;
        _key = key ?? string.Empty;
        _display = display ?? string.Empty;
    }

    public void Report(in TransferProgressInfo info)
    {
        if (_inner is null)
        {
            return;
        }

        _inner.Report(info with { ItemKey = _key, Display = _display });
    }
}
