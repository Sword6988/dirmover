using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DirMover.Core.Diagnostics;
using Microsoft.Win32;

namespace DirMover.Core.Registry;

/// <summary>
/// 一个注册表值的快照条目。
/// </summary>
/// <param name="SubKey">子键（相对 HKCU）。</param>
/// <param name="Name">值名。</param>
/// <param name="Data">原值（<see cref="Existed"/> 为 false 或 <see cref="Unknown"/> 为 true 时无意义）。</param>
/// <param name="Kind">原类型（同上）。</param>
/// <param name="Existed">快照时该值<b>确实不存在</b> → 回滚时应删除。</param>
/// <param name="Unknown">
/// 快照期<b>读取失败</b> → 回滚时<b>必须跳过</b>（既不能恢复也不能删除）。
/// 若把读失败当成"不存在"，回滚就会对用户真实存在的值执行删除，属不可自愈的注册表损坏。
/// </param>
public sealed record RegistryEntry(
    string SubKey,
    string Name,
    object? Data,
    RegistryValueKind? Kind,
    bool Existed,
    bool Unknown);

/// <summary>一组注册表原值的快照，用于"一键回滚"。</summary>
/// <param name="Entries">条目（已按 (子键, 值名) 去重）。</param>
/// <param name="CreatedAt">创建时间（ISO 8601 UTC）。</param>
public sealed record RegistrySnapshot(IReadOnlyList<RegistryEntry> Entries, string CreatedAt);

/// <summary>需要写入/快照的一个注册表位置（子键 + 值名）。</summary>
/// <param name="SubKey">子键（相对 HKCU）。</param>
/// <param name="Name">值名。</param>
public readonly record struct RegistryTarget(string SubKey, string Name);

/// <summary>
/// 注册表的高层封装：读失败与"值不存在"分离、可展开类型判定、快照 / 回滚、快照落盘。
/// </summary>
public sealed class RegistryStore
{
    /// <summary>
    /// 可展开判定（逐字移植，不要"改进"）。
    /// </summary>
    /// <remarks>
    /// 只有<b>以</b> <c>%VAR%</c> 开头、且其后紧跟分隔符或结束的值才写 <c>REG_EXPAND_SZ</c>。
    /// 旧实现只要值里任意位置出现 <c>%</c> 就写可展开类型，于是 <c>D:\a%USERPROFILE%b\UserData</c>
    /// （目录名里恰好含百分号）会被系统再次展开成错误路径——路径静默失真。
    /// </remarks>
    private static readonly Regex ExpandablePrefix =
        new(@"^%[A-Za-z_][A-Za-z0-9_()]*%(\\[^\0]*)?$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 中文路径 / 显示名按原样落盘，不转成 \uXXXX：快照是给用户看、供人工核对的凭据。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IRegistryBackend _backend;
    private readonly IAppLog _log;

    /// <summary>构造。</summary>
    /// <param name="backend">注册表后端（真实 / 内存）。</param>
    /// <param name="log">日志出口。</param>
    public RegistryStore(IRegistryBackend backend, IAppLog log)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _log = log ?? NullLog.Instance;
    }

    /// <summary>最近一次"读取失败（不是值不存在）"的描述；无失败时为 <c>null</c>。</summary>
    public string? LastReadError { get; private set; }

    /// <summary>最近一次 <see cref="Rollback"/> 因 <see cref="RegistryEntry.Unknown"/> 而跳过的条数。</summary>
    public int LastRollbackSkipped { get; private set; }

    /// <summary>取走最近一次读取错误（取走即清空）。</summary>
    public string? ConsumeReadError()
    {
        string? error = LastReadError;
        LastReadError = null;
        return error;
    }

    /// <summary>读取原始值（含类型）。</summary>
    /// <param name="subKey">子键（相对 HKCU）。</param>
    /// <param name="name">值名。</param>
    public RegistryValue? GetRaw(string subKey, string name) => Read(subKey, name);

    /// <summary>
    /// 读取文本值；读不到返回空串。默认展开环境变量（<paramref name="expand"/> = true）。
    /// </summary>
    /// <param name="subKey">子键。</param>
    /// <param name="name">值名。</param>
    /// <param name="expand">是否展开 <c>%VAR%</c>。</param>
    public string GetText(string subKey, string name, bool expand = true)
    {
        RegistryValue? found = Read(subKey, name);
        if (found is null)
        {
            return string.Empty;
        }

        string text = found.Value.Data switch
        {
            string s => s,
            null => string.Empty,
            byte[] bytes => Convert.ToHexString(bytes),
            string[] parts => string.Join(";", parts),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => found.Value.Data.ToString() ?? string.Empty,
        };

        return expand ? Environment.ExpandEnvironmentVariables(text) : text;
    }

    /// <summary>
    /// 判断值是否存在。
    /// </summary>
    /// <remarks>
    /// 读取失败<b>不会</b>被伪装成一个干净的 <c>false</c>：失败会记入 <see cref="LastReadError"/>。
    /// 对一致性敏感的调用点（例如"别名是否已存在"）必须 <see cref="ConsumeReadError"/>，
    /// 否则会漏同步别名而破坏"多值同进同退"。
    /// </remarks>
    /// <param name="subKey">子键。</param>
    /// <param name="name">值名。</param>
    public bool Exists(string subKey, string name) => Read(subKey, name) is not null;

    /// <summary>
    /// 按 PRD §2.2 判定类型后写入文本值，返回实际写入的类型。
    /// </summary>
    /// <param name="subKey">子键。</param>
    /// <param name="name">值名。</param>
    /// <param name="value">值。</param>
    public RegistryValueKind SetText(string subKey, string name, string value)
    {
        RegistryValueKind kind = IsExpandable(value)
            ? RegistryValueKind.ExpandString
            : RegistryValueKind.String;
        _backend.WriteValue(subKey, name, value ?? string.Empty, kind);
        return kind;
    }

    /// <summary>
    /// 快照一组注册表位置。
    /// </summary>
    /// <param name="targets">需要快照的 (子键, 值名)；同一位置重复出现只快照一次。</param>
    /// <remarks>
    /// 这里<b>直接</b>调用后端并显式分流两种"取不到"：读失败记 <see cref="RegistryEntry.Unknown"/>，
    /// 返回 null 记 <see cref="RegistryEntry.Existed"/>=false。走 <see cref="Read"/> 会把读失败
    /// 降级成 null，回滚时就会误删用户真实存在的值。
    /// </remarks>
    public RegistrySnapshot Snapshot(IEnumerable<RegistryTarget> targets)
    {
        var entries = new List<RegistryEntry>();
        var seen = new HashSet<(string SubKey, string Name)>();

        foreach (RegistryTarget target in targets ?? Array.Empty<RegistryTarget>())
        {
            var marker = (target.SubKey.ToLowerInvariant(), target.Name.ToLowerInvariant());
            if (!seen.Add(marker))
            {
                continue;
            }

            try
            {
                RegistryValue? found = _backend.ReadValue(target.SubKey, target.Name);
                if (found is null)
                {
                    entries.Add(new RegistryEntry(target.SubKey, target.Name, null, null, Existed: false, Unknown: false));
                }
                else
                {
                    entries.Add(new RegistryEntry(
                        target.SubKey,
                        target.Name,
                        found.Value.Data,
                        found.Value.Kind,
                        Existed: true,
                        Unknown: false));
                }
            }
            catch (RegistryReadException ex)
            {
                LastReadError = ex.Message;
                _log.Warn($"快照读取失败，该项将不参与回滚：{ex.Message}");
                entries.Add(new RegistryEntry(target.SubKey, target.Name, null, null, Existed: false, Unknown: true));
            }
        }

        return new RegistrySnapshot(entries, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 按快照还原注册表。返回 (恢复数, 删除数)。
    /// </summary>
    /// <param name="snapshot">快照。</param>
    /// <remarks>
    /// <see cref="RegistryEntry.Unknown"/> 的条目<b>跳过</b>：它既不能恢复（不知道原值）
    /// 也不能删除（不知道原本有没有）。跳过的事实通过 <see cref="LastRollbackSkipped"/>
    /// 与一条 warning 暴露，绝不静默。
    /// </remarks>
    public (int Restored, int Removed) Rollback(RegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        int restored = 0;
        int removed = 0;
        int skipped = 0;

        foreach (RegistryEntry entry in snapshot.Entries)
        {
            if (entry.Unknown)
            {
                skipped++;
                continue;
            }

            if (entry.Existed)
            {
                _backend.WriteValue(
                    entry.SubKey,
                    entry.Name,
                    entry.Data ?? string.Empty,
                    entry.Kind ?? RegistryValueKind.String);
                restored++;
            }
            else
            {
                _backend.DeleteValue(entry.SubKey, entry.Name);
                removed++;
            }
        }

        LastRollbackSkipped = skipped;
        if (skipped > 0)
        {
            _log.Warn($"回滚时有 {skipped} 处注册表值因「快照读取失败、原值未知」被跳过，未做改动");
        }

        return (restored, removed);
    }

    /// <summary>
    /// 读取 HKLM 的整数值（只读通道，用于系统开关，例如 <c>Winlogon\AutoRestartShell</c>）。
    /// </summary>
    /// <param name="subKey">子键（相对 HKLM）。</param>
    /// <param name="name">值名。</param>
    /// <returns>值；<b>值不存在返回 <c>null</c></b>（与"读失败"区分：读失败抛异常）。</returns>
    /// <exception cref="RegistryReadException">读取失败（权限 / 句柄）。调用方必须 fail-closed。</exception>
    public int? GetHklmDword(string subKey, string name)
    {
        RegistryValue? found = _backend.ReadHklmValue(subKey, name);
        if (found is null)
        {
            return null;
        }

        return found.Value.Data switch
        {
            int number => number,
            long wide => unchecked((int)wide),
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>枚举子键下的全部值（大小写不敏感）。读失败 → 记 <see cref="LastReadError"/> 并返回空字典。</summary>
    /// <param name="subKey">子键。</param>
    public IReadOnlyDictionary<string, RegistryValue> EnumerateValues(string subKey)
    {
        try
        {
            return _backend.EnumerateValues(subKey);
        }
        catch (RegistryReadException ex)
        {
            LastReadError = ex.Message;
            _log.Warn($"枚举注册表失败，暂按「无值」继续（请留意结果可能不准）：{ex.Message}");
            return new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 把快照落盘到 <paramref name="backupDir"/> 下的 <c>registry-yyyyMMdd-HHmmss.json</c>。
    /// </summary>
    /// <param name="backupDir">备份目录。</param>
    /// <param name="snapshot">快照。</param>
    /// <returns>落盘路径；失败返回 <c>null</c>（<b>不抛</b>——落盘失败不阻断搬运，由引擎写进结果附注）。</returns>
    public string? SaveSnapshot(string backupDir, RegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string path = string.Empty;
        try
        {
            Directory.CreateDirectory(backupDir);

            // 同一秒内可能保存多份快照（rename 通道的单项事务可达毫秒级）：文件名只精确到
            // 秒 + 下面的 overwrite:true 会让同秒的第二份"静默覆盖"前一份——被覆盖后，
            // 意图日志里指向同一文件的多个项只剩最后一份的数据，跨启动回滚时更早那项的
            // 原注册表值就还原不出来了。因此已存在时递增序号（-1、-2…），保证"每份快照
            // 一个文件"。序号方案不依赖时钟精度，也无需伪造时间即可被单测钉住。
            string baseName = $"registry-{DateTime.Now:yyyyMMdd-HHmmss}";
            path = Path.Combine(backupDir, baseName + ".json");
            for (int sequence = 1; File.Exists(path); sequence++)
            {
                path = Path.Combine(backupDir, $"{baseName}-{sequence}.json");
            }

            string temp = path + ".tmp";

            string json = JsonSerializer.Serialize(ToDto(snapshot), JsonOptions);
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // .tmp → 原子替换：崩溃时绝不会留下一个"看起来完整、其实半截"的快照文件。
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or JsonException)
        {
            _log.Error($"注册表快照未能落盘（本次将无法一键回滚）：{path}", ex);
            return null;
        }
    }

    /// <summary>
    /// 读取快照文件。
    /// </summary>
    /// <param name="path">快照文件路径。</param>
    /// <exception cref="InvalidDataException">文件损坏或结构异常（必须是可感知的错误，不能当作"没有快照"）。</exception>
    public static RegistrySnapshot LoadSnapshot(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"快照文件无法读取：{path}", ex);
        }

        SnapshotDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SnapshotDto>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"快照文件格式损坏：{path}", ex);
        }

        if (dto?.Entries is null)
        {
            throw new InvalidDataException($"快照文件结构异常（缺少 entries）：{path}");
        }

        var entries = new List<RegistryEntry>(dto.Entries.Count);
        foreach (EntryDto entry in dto.Entries)
        {
            if (string.IsNullOrEmpty(entry.SubKey) || entry.Name is null)
            {
                throw new InvalidDataException($"快照文件结构异常（条目缺少子键或值名）：{path}");
            }

            entries.Add(new RegistryEntry(
                entry.SubKey,
                entry.Name,
                DecodeData(entry),
                ParseKind(entry.Kind),
                entry.Existed,
                entry.Unknown));
        }

        return new RegistrySnapshot(entries, dto.CreatedAt ?? string.Empty);
    }

    // ================================================================ 内部

    private RegistryValue? Read(string subKey, string name)
    {
        try
        {
            return _backend.ReadValue(subKey, name);
        }
        catch (RegistryReadException ex)
        {
            // 兼容"取不到即 null"的旧契约，但不再静默：错误记下来供上层显式告警。
            LastReadError = ex.Message;
            _log.Warn($"注册表读取失败，暂按「值不存在」继续（位置可能不准）：{ex.Message}");
            return null;
        }
    }

    private static bool IsExpandable(string value) => !string.IsNullOrEmpty(value) && ExpandablePrefix.IsMatch(value);

    private static SnapshotDto ToDto(RegistrySnapshot snapshot)
    {
        var dto = new SnapshotDto
        {
            CreatedAt = snapshot.CreatedAt,
            Entries = new List<EntryDto>(snapshot.Entries.Count),
        };

        foreach (RegistryEntry entry in snapshot.Entries)
        {
            var item = new EntryDto
            {
                SubKey = entry.SubKey,
                Name = entry.Name,
                Existed = entry.Existed,
                Unknown = entry.Unknown,
                Kind = entry.Kind?.ToString(),
            };

            if (!entry.Unknown && entry.Existed)
            {
                EncodeData(item, entry.Data, entry.Kind ?? RegistryValueKind.String);
            }

            dto.Entries.Add(item);
        }

        return dto;
    }

    private static void EncodeData(EntryDto dto, object? data, RegistryValueKind kind)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord:
            case RegistryValueKind.QWord:
                dto.Number = Convert.ToInt64(data ?? 0L, CultureInfo.InvariantCulture);
                break;

            case RegistryValueKind.Binary:
                dto.Base64 = Convert.ToBase64String(data as byte[] ?? Array.Empty<byte>());
                break;

            case RegistryValueKind.MultiString:
                dto.Multi = data as string[] ?? Array.Empty<string>();
                break;

            default:
                dto.Text = data as string ?? Convert.ToString(data, CultureInfo.InvariantCulture);
                break;
        }
    }

    private static object? DecodeData(EntryDto dto)
    {
        RegistryValueKind kind = ParseKind(dto.Kind) ?? RegistryValueKind.String;
        return kind switch
        {
            RegistryValueKind.DWord => dto.Number is null ? 0 : unchecked((int)dto.Number.Value),
            RegistryValueKind.QWord => dto.Number ?? 0L,
            RegistryValueKind.Binary => dto.Base64 is null ? Array.Empty<byte>() : Convert.FromBase64String(dto.Base64),
            RegistryValueKind.MultiString => dto.Multi ?? Array.Empty<string>(),
            _ => dto.Text ?? string.Empty,
        };
    }

    private static RegistryValueKind? ParseKind(string? kind)
    {
        if (string.IsNullOrEmpty(kind))
        {
            return null;
        }

        return Enum.TryParse(kind, ignoreCase: true, out RegistryValueKind parsed) ? parsed : null;
    }

    private sealed class SnapshotDto
    {
        public string? CreatedAt { get; set; }

        public List<EntryDto>? Entries { get; set; }
    }

    private sealed class EntryDto
    {
        public string SubKey { get; set; } = string.Empty;

        public string? Name { get; set; }

        public bool Existed { get; set; }

        public bool Unknown { get; set; }

        public string? Kind { get; set; }

        public string? Text { get; set; }

        public long? Number { get; set; }

        public string? Base64 { get; set; }

        public string[]? Multi { get; set; }
    }
}
