using System.Globalization;
using System.Text;
using System.Text.Json;
using DirMover.Core.Diagnostics;
using DirMover.Core.IO;
using DirMover.Core.Registry;

namespace DirMover.Core.Engine;

/// <summary>
/// 一条未完成事务的记录（意图日志的单元）。
/// </summary>
/// <param name="Key">项稳定标识。</param>
/// <param name="Display">显示名。</param>
/// <param name="Source">源路径。</param>
/// <param name="Target">目标路径。</param>
/// <param name="Stage">阶段（决定下次启动要不要提示、回滚要不要收窄）。</param>
/// <param name="SnapshotPath">本轮注册表快照路径（可能为空：快照落盘失败）。</param>
/// <param name="RegistryTargets">该项涉及的 (子键, 值名)。</param>
/// <param name="RegistryKnown">
/// <c>false</c> = 旧日志没有 <c>registry</c> 字段 → <b>不允许</b>据此收窄回滚范围。
/// 宁可整份回滚，也不能凭残缺信息缩小范围（漏回滚的值会一直指向已消失的目录）。
/// </param>
public sealed record PendingIntent(
    string Key,
    string Display,
    string Source,
    string Target,
    TransferStage Stage,
    string SnapshotPath,
    IReadOnlyList<RegistryTarget> RegistryTargets,
    bool RegistryKnown);

/// <summary>
/// 意图日志的读取结果。
/// </summary>
/// <param name="Items">未完成项。</param>
/// <param name="ReadError">
/// 读失败 / JSON 损坏 / 结构异常 → 非空。<b>"没有文件" == 干净</b>，此时 <see cref="ReadError"/> 为空。
/// 两者必须可区分：把"读不到"当成"干净"会让半完成状态被静默放过。
/// </param>
public sealed record PendingIntents(IReadOnlyList<PendingIntent> Items, string? ReadError)
{
    /// <summary>是否干净（无未完成项且无读取错误）。</summary>
    public bool Clean => ReadError is null && Items.Count == 0;
}

/// <summary>
/// 意图日志：把"哪一项走到哪一步"原子地记在磁盘上，供下次启动判断是否需要提示/回滚。
/// </summary>
public interface IIntentJournal
{
    /// <summary>开始一轮：写入全部项（阶段 = <see cref="TransferStage.Pending"/>）。</summary>
    /// <param name="plan">本轮计划。</param>
    /// <param name="snapshotPath">本轮注册表快照路径（可空）。</param>
    void Begin(IReadOnlyList<PlanItem> plan, string? snapshotPath);

    /// <summary>把某项推进到指定阶段。</summary>
    /// <param name="item">规划项。</param>
    /// <param name="stage">阶段。</param>
    void Mark(PlanItem item, TransferStage stage);

    /// <summary>某项已安全收尾 → 注销。全部注销即删除 <c>intent.json</c>。</summary>
    /// <param name="item">规划项。</param>
    void Finish(PlanItem item);

    /// <summary>
    /// 一次运行收尾：<see cref="TransferStage.Pending"/> 与 <see cref="TransferStage.Cancelled"/> 一并注销
    /// （取消是正常收尾，不得在下次启动被误报为"未正常收尾"）。
    /// </summary>
    void CloseRun();

    /// <summary>丢弃整份记录（"忽略并删除这条记录"）。</summary>
    void Discard();
}

/// <summary>
/// 磁盘实现的意图日志：<c>backups\intent.json</c>，原子写（<c>.tmp</c> → <c>File.Replace</c>/<c>Move</c>）。
/// </summary>
/// <remarks>
/// 为什么每次变更都整份重写：文件只有几 KB（最多 7 项），而"部分成功"的写入会让日志与实际
/// 状态不一致——那正是这个文件存在的意义所在。整份重写 + 原子替换换来的是"要么是旧状态、
/// 要么是新状态"，不存在第三种。
/// </remarks>
public sealed class IntentJournal : IIntentJournal
{
    /// <summary>日志文件名。</summary>
    public const string FileName = "intent.json";

    /// <summary>临时文件名（原子写用；正常路径下不应残留）。</summary>
    public const string TempFileName = "intent.json.tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 中文路径 / 显示名按原样落盘（intent.json 是排障与人工核对的凭据，不是压缩日志）。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly string _tempPath;
    private readonly IAppLog _log;
    private readonly List<PendingIntent> _entries = new();
    private bool _writeDisabled;

    private IntentJournal(string backupDir, IAppLog log)
    {
        _path = Path.Combine(backupDir, FileName);
        _tempPath = Path.Combine(backupDir, TempFileName);
        _log = log;
    }

    /// <summary>
    /// 打开一个意图日志。
    /// </summary>
    /// <param name="backupDir">备份目录（空 / <paramref name="enabled"/> = false → 返回空实现）。</param>
    /// <param name="enabled">是否启用（试运行时关闭）。</param>
    /// <param name="log">日志出口。</param>
    public static IIntentJournal Open(string? backupDir, bool enabled, IAppLog log)
    {
        IAppLog safeLog = log ?? NullLog.Instance;
        if (!enabled || string.IsNullOrWhiteSpace(backupDir))
        {
            return new NoopIntentJournal();
        }

        return new IntentJournal(backupDir, safeLog);
    }

    /// <inheritdoc />
    public void Begin(IReadOnlyList<PlanItem> plan, string? snapshotPath)
    {
        _entries.Clear();
        if (plan is not null)
        {
            foreach (PlanItem item in plan)
            {
                _entries.Add(new PendingIntent(
                    item.Key,
                    item.Display,
                    item.Current,
                    item.Target,
                    TransferStage.Pending,
                    snapshotPath ?? string.Empty,
                    item.RegistryTargets ?? Array.Empty<RegistryTarget>(),
                    RegistryKnown: true));
            }
        }

        Flush();
    }

    /// <inheritdoc />
    public void Mark(PlanItem item, TransferStage stage)
    {
        if (item is null)
        {
            return;
        }

        int index = IndexOf(item.Key);
        if (index < 0)
        {
            _entries.Add(new PendingIntent(
                item.Key,
                item.Display,
                item.Current,
                item.Target,
                stage,
                string.Empty,
                item.RegistryTargets ?? Array.Empty<RegistryTarget>(),
                RegistryKnown: true));
        }
        else
        {
            _entries[index] = _entries[index] with { Stage = stage };
        }

        Flush();
    }

    /// <inheritdoc />
    public void Finish(PlanItem item)
    {
        if (item is null)
        {
            return;
        }

        int index = IndexOf(item.Key);
        if (index >= 0)
        {
            _entries.RemoveAt(index);
            Flush();
        }
    }

    /// <inheritdoc />
    public void CloseRun()
    {
        int removed = _entries.RemoveAll(static entry =>
            entry.Stage is TransferStage.Pending or TransferStage.Cancelled);
        if (removed > 0 || _entries.Count == 0)
        {
            Flush();
        }
    }

    /// <inheritdoc />
    public void Discard()
    {
        _entries.Clear();
        TryDelete(_path);
        TryDelete(_tempPath);
    }

    /// <summary>
    /// 读取未完成事务记录。
    /// </summary>
    /// <param name="backupDir">备份目录。</param>
    /// <remarks>
    /// 三种结果必须可区分：①没有文件 → <c>Items</c> 空且 <c>ReadError</c> 空（干净）；
    /// ②文件损坏/结构异常 → <c>ReadError</c> 非空；③有内容 → 逐项返回。
    /// </remarks>
    public static PendingIntents ReadPending(string? backupDir)
    {
        if (string.IsNullOrWhiteSpace(backupDir))
        {
            return new PendingIntents(Array.Empty<PendingIntent>(), null);
        }

        // 走 PathTools.Exists：>MAX_PATH 的合法路径用裸 File.Exists 会被判成"不存在"，
        // 于是"上次事务记录读不到"会静默退化成"上次很干净"。
        string path = Path.Combine(backupDir, FileName);
        if (!PathTools.Exists(path))
        {
            return new PendingIntents(Array.Empty<PendingIntent>(), null);
        }

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return new PendingIntents(Array.Empty<PendingIntent>(), $"意图日志无法读取：{ex.Message}");
        }

        JournalDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<JournalDto>(json);
        }
        catch (JsonException ex)
        {
            return new PendingIntents(Array.Empty<PendingIntent>(), $"意图日志格式损坏：{ex.Message}");
        }

        if (dto?.Items is null)
        {
            return new PendingIntents(Array.Empty<PendingIntent>(), "意图日志结构异常（缺少 items 字段）");
        }

        var items = new List<PendingIntent>(dto.Items.Count);
        foreach (IntentDto item in dto.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Key) || item.Target is null)
            {
                return new PendingIntents(Array.Empty<PendingIntent>(), "意图日志结构异常（条目缺少 key 或 target）");
            }

            if (!Enum.TryParse(item.Stage, ignoreCase: true, out TransferStage stage))
            {
                return new PendingIntents(Array.Empty<PendingIntent>(), $"意图日志结构异常（未知阶段 {item.Stage}）");
            }

            var targets = new List<RegistryTarget>();
            if (item.Registry is not null)
            {
                foreach (TargetDto target in item.Registry)
                {
                    if (target is null || string.IsNullOrWhiteSpace(target.SubKey) || target.Name is null)
                    {
                        return new PendingIntents(Array.Empty<PendingIntent>(), "意图日志结构异常（注册表目标缺少子键或值名）");
                    }

                    targets.Add(new RegistryTarget(target.SubKey, target.Name));
                }
            }

            items.Add(new PendingIntent(
                item.Key!,
                item.Display ?? item.Key!,
                item.Source ?? string.Empty,
                item.Target!,
                stage,
                item.Snapshot ?? string.Empty,
                targets,
                RegistryKnown: item.Registry is not null));
        }

        return new PendingIntents(items, null);
    }

    /// <summary>
    /// 未完成项涉及的注册表位置（回滚范围的<b>上限</b>）。
    /// </summary>
    /// <param name="pending">已读取的意图日志。</param>
    /// <remarks>
    /// 排除 <see cref="TransferStage.SourceRemoved"/>（已完成，不该被滚回指向不存在目录的路径），
    /// 并跳过 <see cref="PendingIntent.RegistryKnown"/> = false 的旧记录（信息不完整 → 不裁剪）。
    /// </remarks>
    public static ISet<RegistryTarget> UnfinishedRegistryTargets(PendingIntents pending)
    {
        var set = new HashSet<RegistryTarget>(RegistryTargetComparer.Instance);
        if (pending?.Items is null)
        {
            return set;
        }

        foreach (PendingIntent item in pending.Items)
        {
            if (!item.RegistryKnown || item.Stage == TransferStage.SourceRemoved)
            {
                continue;
            }

            foreach (RegistryTarget target in item.RegistryTargets)
            {
                set.Add(target);
            }
        }

        return set;
    }

    // ================================================================ 内部

    private int IndexOf(string key)
    {
        for (int index = 0; index < _entries.Count; index++)
        {
            if (string.Equals(_entries[index].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private void Flush()
    {
        if (_writeDisabled)
        {
            return;
        }

        try
        {
            if (_entries.Count == 0)
            {
                TryDelete(_path);
                TryDelete(_tempPath);
                return;
            }

            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(ToDto(), JsonOptions);
            File.WriteAllText(_tempPath, json, Utf8NoBom);

            if (PathTools.Exists(_path))
            {
                try
                {
                    // File.Replace 是原子的：崩溃时要么是旧文件、要么是新文件。
                    File.Replace(_tempPath, _path, destinationBackupFileName: null);
                }
                catch (IOException)
                {
                    // 少数文件系统（部分网络盘）不支持 Replace：退回覆盖式 Move。
                    Compat.MoveOrReplace(_tempPath, _path);
                }
            }
            else
            {
                File.Move(_tempPath, _path);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            // 意图日志写不进去不阻断搬运，但必须"停用并可见"：不能让用户以为下次启动还有提示。
            _writeDisabled = true;
            TryDelete(_tempPath);
            _log.Error("意图日志写入失败，本次运行将不再记录事务进度（下次启动不会提示未完成项目）", ex);
        }
    }

    private JournalDto ToDto()
    {
        var dto = new JournalDto
        {
            CreatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Items = new List<IntentDto>(_entries.Count),
        };

        foreach (PendingIntent entry in _entries)
        {
            var item = new IntentDto
            {
                Key = entry.Key,
                Display = entry.Display,
                Source = entry.Source,
                Target = entry.Target,
                Stage = entry.Stage.ToString(),
                Snapshot = entry.SnapshotPath,
                Registry = new List<TargetDto>(),
            };

            if (entry.RegistryKnown)
            {
                foreach (RegistryTarget target in entry.RegistryTargets)
                {
                    item.Registry.Add(new TargetDto { SubKey = target.SubKey, Name = target.Name });
                }
            }
            else
            {
                // 旧记录形态：保持 registry 缺失，让读取方知道"不可裁剪"。
                item.Registry = null;
            }

            dto.Items.Add(item);
        }

        return dto;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (PathTools.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败不影响结论（下次要么覆盖、要么被识别为损坏记录）。
        }
    }

    private sealed class NoopIntentJournal : IIntentJournal
    {
        public void Begin(IReadOnlyList<PlanItem> plan, string? snapshotPath)
        {
        }

        public void Mark(PlanItem item, TransferStage stage)
        {
        }

        public void Finish(PlanItem item)
        {
        }

        public void CloseRun()
        {
        }

        public void Discard()
        {
        }
    }

    private sealed class JournalDto
    {
        public string? CreatedAt { get; set; }

        public List<IntentDto>? Items { get; set; }
    }

    private sealed class IntentDto
    {
        public string? Key { get; set; }

        public string? Display { get; set; }

        public string? Source { get; set; }

        public string? Target { get; set; }

        public string? Stage { get; set; }

        public string? Snapshot { get; set; }

        public List<TargetDto>? Registry { get; set; }
    }

    private sealed class TargetDto
    {
        public string? SubKey { get; set; }

        public string? Name { get; set; }
    }
}

/// <summary>
/// 按 (子键, 值名) 做<b>大小写不敏感</b>比较：注册表键名本身不区分大小写，
/// 用区分大小写的集合比较会让"已经记过的目标"被当成新目标，回滚范围算错。
/// </summary>
internal sealed class RegistryTargetComparer : IEqualityComparer<RegistryTarget>
{
    /// <summary>共享实例。</summary>
    public static readonly RegistryTargetComparer Instance = new();

    /// <inheritdoc />
    public bool Equals(RegistryTarget x, RegistryTarget y) =>
        string.Equals(x.SubKey, y.SubKey, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public int GetHashCode(RegistryTarget obj) => HashCode.Combine(
        (obj.SubKey ?? string.Empty).ToLowerInvariant(),
        (obj.Name ?? string.Empty).ToLowerInvariant());
}

/// <summary>
/// 把注册表快照裁剪到"只含这些位置"。
/// </summary>
public static class SnapshotTrimmer
{
    /// <summary>
    /// 裁剪快照（子键 / 值名均大小写不敏感）。
    /// </summary>
    /// <param name="snapshot">完整快照。</param>
    /// <param name="targets">要保留的位置（通常是"未完成项"涉及的注册表值）。</param>
    /// <returns>裁剪后的快照与被排除的条数。</returns>
    public static (RegistrySnapshot Trimmed, int Excluded) Restrict(
        RegistrySnapshot snapshot,
        IEnumerable<RegistryTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var keep = new HashSet<RegistryTarget>(
            targets ?? Array.Empty<RegistryTarget>(),
            RegistryTargetComparer.Instance);

        var entries = new List<RegistryEntry>(snapshot.Entries.Count);
        int excluded = 0;
        foreach (RegistryEntry entry in snapshot.Entries)
        {
            if (keep.Contains(new RegistryTarget(entry.SubKey, entry.Name)))
            {
                entries.Add(entry);
            }
            else
            {
                excluded++;
            }
        }

        return (new RegistrySnapshot(entries, snapshot.CreatedAt), excluded);
    }
}
