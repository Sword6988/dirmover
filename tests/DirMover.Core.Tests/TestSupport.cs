using System.Diagnostics;
using DirMover.Core.Diagnostics;
using DirMover.Core.Registry;

// 关掉并行化：Catalog.SystemDrive 是"可注入的系统盘"，而"禁区名单随系统盘现算"这条性质
// 只能通过改写它来验证。并行执行时，一个用例改值会让另一个用例的断言随机飘红——
// 这种 flaky 比没有用例更糟（团队会学会忽略红色）。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DirMover.Core.Tests;

/// <summary>
/// 一次性临时目录沙箱：构造时建目录，<see cref="Dispose"/> 时递归删除。
/// </summary>
/// <remarks>
/// 为什么必须落在系统临时目录而不是相对路径：这些用例会真实创建/删除目录树，
/// 相对路径会把测试产物留在仓库里，CI 上第二次运行就会因为"已存在"而失败。
/// 删除失败一律吞掉（沙箱清理失败不应把绿色用例变红）。
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "dirmover")
    {
        Root = Path.Combine(Path.GetTempPath(), "dirmover-tests", prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>沙箱根目录（绝对路径、无尾分隔符）。</summary>
    public string Root { get; }

    /// <summary>在沙箱内拼一个绝对路径（不创建）。</summary>
    public string Combine(params string[] relativeParts)
    {
        string result = Root;
        foreach (string part in relativeParts)
        {
            result = Path.Combine(result, part);
        }

        return result;
    }

    /// <summary>建立子目录并返回其绝对路径。</summary>
    public string CreateDirectoryPath(string relative)
    {
        string full = Path.Combine(Root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>写一个文件（自动建父目录）并返回完整路径。</summary>
    public string CreateFile(string relative, string content = "x")
    {
        string full = Path.Combine(Root, relative);
        string? parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 沙箱清理失败不影响任何断言结论。
        }
    }
}

/// <summary>
/// 记录型日志，供断言"某个静默失败是否被上报"。
/// </summary>
internal sealed class FakeLog : IAppLog
{
    public List<string> Infos { get; } = new();

    public List<string> Warns { get; } = new();

    public List<string> Errors { get; } = new();

    public void Info(string message) => Infos.Add(message);

    public void Warn(string message) => Warns.Add(message);

    public void Error(string message, Exception? ex = null) => Errors.Add(message);
}

/// <summary>
/// 指向临时沙箱的落点实现：让所有"写日志/写快照/写意图日志"的用例彻底离开真实用户目录。
/// </summary>
/// <remarks>
/// 这是 Core 把落点抽成 <see cref="IAppPaths"/> 的直接收益：若 Core 里直接读
/// <c>%LOCALAPPDATA%</c>，跑一次单测就会往构建机的真实配置目录里塞一堆
/// <c>logs\DirMover-*.log</c> 与 <c>backups\registry-*.json</c>。
/// </remarks>
internal sealed class TempAppPaths : IAppPaths
{
    public TempAppPaths(string root)
    {
        BaseDirectory = root;
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(BackupDirectory);
    }

    public bool IsPortable => true;

    public string BaseDirectory { get; }

    public string LogDirectory => Path.Combine(BaseDirectory, "logs");

    public string BackupDirectory => Path.Combine(BaseDirectory, "backups");

    public string StartupErrorFile => Path.Combine(LogDirectory, "startup_error.txt");

    public string SingleInstancePidFile => Path.Combine(BaseDirectory, "single_instance.pid");

    public string LogFileFor(DateTimeOffset now) =>
        Path.Combine(LogDirectory, $"DirMover-{now:yyyyMMdd}.log");

    public string ScanFileFor(DateTimeOffset now) =>
        Path.Combine(LogDirectory, $"scan-{now:yyyyMMdd-HHmmss}.txt");
}

/// <summary>
/// 注册表种子构造器：链式攒出 HKCU / HKLM 两套数据，再包成内存后端。
/// </summary>
/// <remarks>
/// 之所以用 (子键, 值名, 值) 三元组列表而不是嵌套字典：<c>Dictionary&lt;string, Dictionary&lt;…&gt;&gt;</c>
/// 与后端内部的"大小写不敏感"字典之间没有协变关系，直接传会退化成一堆类型转换噪音。
/// 三元组列表既好写也好读，且能显式表达"同一个值名在 HKCU 与 HKLM 各有一份"这种要点。
/// </remarks>
internal sealed class RegistrySeed
{
    private readonly List<RegistrySeedEntry> _hkcu = new();
    private readonly List<RegistrySeedEntry> _hklm = new();

    /// <summary>放一个 HKCU 值（默认写成可展开字符串，与系统原生形态一致）。</summary>
    public RegistrySeed Add(string subKey, string name, string value, bool expandable = true)
    {
        _hkcu.Add(new RegistrySeedEntry(subKey, name, new RegistryValue(
            value,
            expandable ? Microsoft.Win32.RegistryValueKind.ExpandString : Microsoft.Win32.RegistryValueKind.String)));
        return this;
    }

    /// <summary>放一个 HKLM 值（只读通道用，例如 <c>Winlogon\AutoRestartShell</c>）。</summary>
    public RegistrySeed AddHklm(string subKey, string name, int value)
    {
        _hklm.Add(new RegistrySeedEntry(subKey, name, new RegistryValue(
            value, Microsoft.Win32.RegistryValueKind.DWord)));
        return this;
    }

    /// <summary>包成内存后端（记录 Writes 供断言）。</summary>
    public InMemoryRegistryBackend ToBackend() => new(_hkcu, _hklm);
}

/// <summary>
/// 依赖宿主环境能力的小工具（junction / 8.3 短名）。本机不满足时用例做软跳过——
/// 把"环境不支持"误判成"功能坏了"会让红线用例长期飘红，最终被忽略。
/// </summary>
internal static class TestEnv
{
    private static readonly Lazy<bool> JunctionProbe = new(TryCreateJunctionProbe, isThreadSafe: true);

    /// <summary>当前用户能否创建目录联接（junction 不需要管理员权限，但策略可能禁用）。</summary>
    public static bool CanCreateJunction => JunctionProbe.Value;

    /// <summary>
    /// 在 <paramref name="linkPath"/> 上创建一个指向 <paramref name="targetPath"/> 的目录联接。
    /// </summary>
    public static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(targetPath);

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 cmd.exe");
            process.WaitForExit(15000);
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TryCreateJunctionProbe()
    {
        using var sandbox = new TempDirectory("junction-probe");
        string target = sandbox.CreateDirectoryPath("target");
        string link = sandbox.Combine("link");
        return TryCreateJunction(link, target);
    }
}
