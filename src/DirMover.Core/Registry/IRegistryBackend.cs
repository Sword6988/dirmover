using System.Security;
using Microsoft.Win32;

// 本文件所在命名空间 DirMover.Core.Registry 会遮蔽 Microsoft.Win32.Registry 类型名，
// 故用别名显式指向 Win32 注册表根键类型。
using Win32Registry = Microsoft.Win32.Registry;

namespace DirMover.Core.Registry;

/// <summary>
/// 一个注册表值（数据 + 类型）。存在性用 <c>null</c> 表达，不用"空值"。
/// </summary>
/// <param name="Data">数据。文本类为 <see cref="string"/>，DWord/QWord 为整型，Binary 为 <see cref="byte"/>[]。</param>
/// <param name="Kind">注册表类型。</param>
public readonly record struct RegistryValue(object? Data, RegistryValueKind Kind);

/// <summary>
/// 注册表<b>读取失败</b>（权限 / 句柄 / 损坏）。
/// </summary>
/// <remarks>
/// 必须与"值不存在"区分开：原实现把两者都返回 <c>None</c>，于是"读不到"会被静默降级成
/// "没有这个值"，进而导致 ①当前位置悄悄回落到系统默认路径；②别名被判为"不存在"而漏同步
/// （注册表出现"权威值已改、GUID 别名仍旧"的错乱）。这里让读失败成为可感知的事件。
/// </remarks>
public sealed class RegistryReadException : IOException
{
    /// <summary>构造读失败异常。</summary>
    /// <param name="message">中文原因（含子键与值名，便于排障）。</param>
    /// <param name="inner">内层异常。</param>
    public RegistryReadException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>内存后端的种子项（子键相对 HKCU；HKLM 种子另列）。</summary>
/// <param name="SubKey">子键。</param>
/// <param name="Name">值名。</param>
/// <param name="Value">值。</param>
public readonly record struct RegistrySeedEntry(string SubKey, string Name, RegistryValue Value);

/// <summary>
/// 注册表访问抽象。<c>ReadValue</c>/<c>WriteValue</c>/<c>DeleteValue</c>/<c>EnumerateValues</c>
/// 的子键路径一律<b>相对 HKCU</b>；<c>ReadHklmValue</c> 是唯一的 HKLM 通道且<b>只读</b>。
/// </summary>
/// <remarks>
/// 为什么要把读写收在接口后面：迁移的每一步都依赖"注册表长什么样"，而这些判断必须能在
/// <b>完全不触碰真实注册表</b>的前提下被测（否则 CI 会改写构建机的用户配置）。
/// 另外"只读 HKLM"是硬约束：本工具永不写机器级配置，故接口里根本不存在对应的写方法。
/// </remarks>
public interface IRegistryBackend
{
    /// <summary>
    /// 读取一个 HKCU 值。三态语义：
    /// <c>null</c> = 键或值确实不存在（合法）；有值 = 读到了；抛 <see cref="RegistryReadException"/> = 读失败。
    /// </summary>
    /// <param name="subKey">子键（相对 HKCU）。</param>
    /// <param name="name">值名。</param>
    RegistryValue? ReadValue(string subKey, string name);

    /// <summary>读取一个 HKLM 值（只用于读系统开关，例如 <c>Winlogon\AutoRestartShell</c>）。</summary>
    /// <param name="subKey">子键（相对 HKLM）。</param>
    /// <param name="name">值名。</param>
    RegistryValue? ReadHklmValue(string subKey, string name);

    /// <summary>写入一个 HKCU 值。</summary>
    /// <param name="subKey">子键（相对 HKCU）。</param>
    /// <param name="name">值名。</param>
    /// <param name="data">数据。</param>
    /// <param name="kind">类型。</param>
    void WriteValue(string subKey, string name, object data, RegistryValueKind kind);

    /// <summary>删除一个 HKCU 值；值（或子键）不存在时静默成功。</summary>
    /// <param name="subKey">子键（相对 HKCU）。</param>
    /// <param name="name">值名。</param>
    void DeleteValue(string subKey, string name);

    /// <summary>枚举子键下的全部值（<b>大小写不敏感</b>）。子键不存在 → 空字典；读失败 → 抛异常。</summary>
    /// <param name="subKey">子键（相对 HKCU）。</param>
    IReadOnlyDictionary<string, RegistryValue> EnumerateValues(string subKey);
}

/// <summary>
/// 真实注册表后端：HKCU 可读写，HKLM 只读。
/// </summary>
public sealed class Win32RegistryBackend : IRegistryBackend
{
    /// <inheritdoc />
    public RegistryValue? ReadValue(string subKey, string name) =>
        ReadFrom(Win32Registry.CurrentUser, subKey, name, hiveLabel: @"HKEY_CURRENT_USER\");

    /// <inheritdoc />
    public RegistryValue? ReadHklmValue(string subKey, string name) =>
        ReadFrom(Win32Registry.LocalMachine, subKey, name, hiveLabel: @"HKEY_LOCAL_MACHINE\");

    /// <inheritdoc />
    public void WriteValue(string subKey, string name, object data, RegistryValueKind kind)
    {
        using RegistryKey key = Win32Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new IOException($"无法创建或打开注册表子键：HKEY_CURRENT_USER\\{subKey}");
        key.SetValue(name, data, kind);
    }

    /// <inheritdoc />
    public void DeleteValue(string subKey, string name)
    {
        using RegistryKey? key = Win32Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        if (key is null)
        {
            // 子键不存在 ⇒ 值必然不存在 ⇒ 按"静默成功"处理（回滚时删一个从未存在的值不该报错）。
            return;
        }

        key.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, RegistryValue> EnumerateValues(string subKey)
    {
        var values = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? key = Win32Registry.CurrentUser.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                // 子键不存在：这是"真的没有"，空结果合法（与 ReadValue 返回 null 同义）。
                return values;
            }

            foreach (string name in key.GetValueNames())
            {
                object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                RegistryValueKind kind;
                try
                {
                    kind = key.GetValueKind(name);
                }
                catch (IOException)
                {
                    // 值在枚举过程中被别的进程删掉了：跳过这一项（不是整键读取失败）。
                    continue;
                }

                values[name] = new RegistryValue(raw, kind);
            }

            return values;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // 审计口径：权限 / 句柄 / 损坏必须抛出去，绝不静默返回一个"看起来正常"的空字典。
            throw new RegistryReadException($"枚举注册表 HKEY_CURRENT_USER\\{subKey} 失败：{ex.Message}", ex);
        }
    }

    private static RegistryValue? ReadFrom(RegistryKey root, string subKey, string name, string hiveLabel)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return null;
            }

            object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null)
            {
                return null;
            }

            return new RegistryValue(raw, key.GetValueKind(name));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            throw new RegistryReadException(
                $"{hiveLabel}{subKey}\\{name} 读取失败：{ex.Message}",
                ex);
        }
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException or NotSupportedException;
}

/// <summary>
/// 内存注册表后端：单测与试运行演练用。不触碰真实注册表。
/// </summary>
public sealed class InMemoryRegistryBackend : IRegistryBackend
{
    private readonly Dictionary<string, Dictionary<string, RegistryValue>> _hkcu;
    private readonly Dictionary<string, Dictionary<string, RegistryValue>> _hklm;
    private readonly List<(string SubKey, string Name, object? Data, RegistryValueKind Kind)> _writes = new();
    private int _writeAttempts;

    /// <summary>构造内存后端。</summary>
    /// <param name="hkcu">HKCU 种子（可空）。</param>
    /// <param name="hklm">HKLM 只读种子（可空）。两个命名空间相互独立。</param>
    public InMemoryRegistryBackend(
        IEnumerable<RegistrySeedEntry>? hkcu = null,
        IEnumerable<RegistrySeedEntry>? hklm = null)
    {
        _hkcu = Build(hkcu);
        _hklm = Build(hklm);
    }

    /// <summary>写入记录（供断言"到底写了什么、写了几次"）。</summary>
    public IReadOnlyList<(string SubKey, string Name, object? Data, RegistryValueKind Kind)> Writes => _writes;

    /// <summary>
    /// 写失败注入：参数为 (子键, 值名, 第几次尝试，从 1 开始)，返回 <c>true</c> 则本次写入抛异常。
    /// </summary>
    /// <remarks>
    /// 存在的原因：红线 5「写注册表失败 → 本项已写入的值回滚、源目录保留」必须被验证，
    /// 而真实注册表无法可靠地"让第二个值写失败"。
    /// </remarks>
    public Func<string, string, int, bool>? WriteFailure { get; set; }

    /// <summary>删除记录（供断言"回滚有没有误删用户真实存在的值"）。</summary>
    public IReadOnlyList<(string SubKey, string Name)> Deletes { get; private set; } = Array.Empty<(string, string)>();

    /// <inheritdoc />
    public RegistryValue? ReadValue(string subKey, string name) => Lookup(_hkcu, subKey, name);

    /// <inheritdoc />
    public RegistryValue? ReadHklmValue(string subKey, string name) => Lookup(_hklm, subKey, name);

    /// <inheritdoc />
    public void WriteValue(string subKey, string name, object data, RegistryValueKind kind)
    {
        _writeAttempts++;
        if (WriteFailure is not null && WriteFailure(subKey, name, _writeAttempts))
        {
            throw new IOException($"注入的写入失败：HKEY_CURRENT_USER\\{subKey}\\{name}");
        }

        if (!_hkcu.TryGetValue(subKey, out Dictionary<string, RegistryValue>? values))
        {
            values = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
            _hkcu[subKey] = values;
        }

        values[name] = new RegistryValue(data, kind);
        _writes.Add((subKey, name, data, kind));
    }

    /// <inheritdoc />
    public void DeleteValue(string subKey, string name)
    {
        if (_hkcu.TryGetValue(subKey, out Dictionary<string, RegistryValue>? values))
        {
            values.Remove(name);
        }

        var deletes = new List<(string, string)>(Deletes) { (subKey, name) };
        Deletes = deletes;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, RegistryValue> EnumerateValues(string subKey)
    {
        if (!_hkcu.TryGetValue(subKey, out Dictionary<string, RegistryValue>? values))
        {
            return new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, RegistryValue>(values, StringComparer.OrdinalIgnoreCase);
    }

    private static RegistryValue? Lookup(
        Dictionary<string, Dictionary<string, RegistryValue>> hive,
        string subKey,
        string name)
    {
        if (!hive.TryGetValue(subKey, out Dictionary<string, RegistryValue>? values))
        {
            return null;
        }

        return values.TryGetValue(name, out RegistryValue value) ? value : null;
    }

    private static Dictionary<string, Dictionary<string, RegistryValue>> Build(
        IEnumerable<RegistrySeedEntry>? seed)
    {
        var result = new Dictionary<string, Dictionary<string, RegistryValue>>(StringComparer.OrdinalIgnoreCase);
        if (seed is null)
        {
            return result;
        }

        foreach (RegistrySeedEntry entry in seed)
        {
            if (!result.TryGetValue(entry.SubKey, out Dictionary<string, RegistryValue>? values))
            {
                values = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
                result[entry.SubKey] = values;
            }

            values[entry.Name] = entry.Value;
        }

        return result;
    }
}
