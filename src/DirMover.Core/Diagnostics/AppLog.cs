using System.Text;

namespace DirMover.Core.Diagnostics;

/// <summary>
/// 日志出口。过程详情只写日志文件，界面<b>没有</b>日志查看区（红线）：界面上唯一的
/// 相关出口是状态栏的「打开日志目录」。
/// </summary>
public interface IAppLog
{
    /// <summary>记录一条常规信息。</summary>
    /// <param name="message">消息。</param>
    void Info(string message);

    /// <summary>记录一条告警（"静默失败"必须以告警形式可见）。</summary>
    /// <param name="message">消息。</param>
    void Warn(string message);

    /// <summary>记录一条错误。</summary>
    /// <param name="message">消息。</param>
    /// <param name="ex">异常（可为 null）。</param>
    void Error(string message, Exception? ex = null);
}

/// <summary>
/// 运行期产物（logs / backups / PID）的落点解析。
/// </summary>
/// <remarks>
/// 为什么是接口而不是静态类：Core 里所有需要落点的类型（<c>FileLogger</c>、
/// <c>RegistryStore</c> 的快照目录、<c>IntentJournal</c>、<c>SingleInstanceGuard</c>）
/// 都必须能被注入——否则单测会写到构建机**真实**的用户配置目录里，测试从"验证安全逻辑"
/// 变成"污染本机环境"。任何 Core 类型都<b>不得</b>直接读 <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>。
///
/// 为什么便携判定是"整体"的：要么 logs 与 backups 都在 exe 旁，要么全部回落到
/// <c>%LOCALAPPDATA%\DirMover</c>。一半一半（日志在 exe 旁、快照在 LocalAppData）会让
/// 用户在两处找文件、让「打开日志目录」与"一键回滚"指向不同根，排障时极易误判。
/// </remarks>
public interface IAppPaths
{
    /// <summary>是否为"绿色便携"形态（产物落在 exe 旁）。</summary>
    bool IsPortable { get; }

    /// <summary>logs / backups 的共同根目录。</summary>
    string BaseDirectory { get; }

    /// <summary>日志目录（<c>&lt;base&gt;\logs</c>）。</summary>
    string LogDirectory { get; }

    /// <summary>备份目录（<c>&lt;base&gt;\backups</c>）：注册表快照 + 意图日志。</summary>
    string BackupDirectory { get; }

    /// <summary>启动崩溃兜底文件（<c>&lt;logs&gt;\startup_error.txt</c>）。</summary>
    string StartupErrorFile { get; }

    /// <summary>给定时间对应的日志文件（<c>&lt;logs&gt;\DirMover-yyyyMMdd.log</c>）。</summary>
    /// <param name="now">时间（显式传入以便单测固定文件名）。</param>
    string LogFileFor(DateTimeOffset now);

    /// <summary>给定时间对应的只读扫描结果文件（<c>&lt;logs&gt;\scan-yyyyMMdd-HHmmss.txt</c>）。</summary>
    /// <param name="now">时间（显式传入以便单测固定文件名）。</param>
    string ScanFileFor(DateTimeOffset now);

    /// <summary>
    /// 单实例 PID 文件。<b>恒为</b> <c>%LOCALAPPDATA%\DirMover\single_instance.pid</c>，
    /// 不随便携落点变化。
    /// </summary>
    /// <remarks>
    /// 固定位置的理由：它是"本用户会话内的进程互斥"辅助件，不是需要随程序走的产物。
    /// 若跟着便携落点走，用户把 exe 换到别的目录后就会丢失"上一个实例"的线索，
    /// 从而允许两个实例同时迁移——两个实例同时删源是数据事故。
    /// </remarks>
    string SingleInstancePidFile { get; }
}

/// <summary>
/// 默认落点解析：exe 旁可写 → 便携；否则整体回落 <c>%LOCALAPPDATA%\DirMover</c>。
/// </summary>
public sealed class DefaultAppPaths : IAppPaths
{
    private const string AppFolderName = "DirMover";
    private const string LogFolderName = "logs";
    private const string BackupFolderName = "backups";
    private const string PidFileName = "single_instance.pid";
    private const string StartupErrorFileName = "startup_error.txt";

    private readonly string _baseDirectory;

    private DefaultAppPaths(string baseDirectory, bool portable)
    {
        _baseDirectory = baseDirectory;
        IsPortable = portable;
    }

    /// <inheritdoc />
    public bool IsPortable { get; }

    /// <inheritdoc />
    public string BaseDirectory => _baseDirectory;

    /// <inheritdoc />
    public string LogDirectory => Path.Combine(_baseDirectory, LogFolderName);

    /// <inheritdoc />
    public string BackupDirectory => Path.Combine(_baseDirectory, BackupFolderName);

    /// <inheritdoc />
    public string StartupErrorFile => Path.Combine(LogDirectory, StartupErrorFileName);

    /// <inheritdoc />
    public string LogFileFor(DateTimeOffset now) =>
        Path.Combine(LogDirectory, $"DirMover-{now:yyyyMMdd}.log");

    /// <inheritdoc />
    public string ScanFileFor(DateTimeOffset now) =>
        Path.Combine(LogDirectory, $"scan-{now:yyyyMMdd-HHmmss}.txt");

    /// <inheritdoc />
    public string SingleInstancePidFile => Path.Combine(UserScopeDirectory(), PidFileName);

    /// <summary>
    /// 解析落点。<paramref name="exeDirectory"/> 可写则用 exe 旁（便携），否则整体回落。
    /// </summary>
    /// <param name="exeDirectory">exe 所在目录（<see cref="AppContext.BaseDirectory"/>）。</param>
    public static DefaultAppPaths Resolve(string exeDirectory)
    {
        string candidate = (exeDirectory ?? string.Empty).Trim().TrimEnd('\\', '/');
        if (candidate.Length > 0 && CanWriteBaseDirectory(candidate))
        {
            return new DefaultAppPaths(candidate, portable: true);
        }

        return new DefaultAppPaths(UserScopeDirectory(), portable: false);
    }

    /// <summary>本用户作用域目录（<c>%LOCALAPPDATA%\DirMover</c>）。</summary>
    internal static string UserScopeDirectory()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            // 极端情况（无用户配置目录的宿主）：退回临时目录，保证程序仍可运行。
            local = Path.GetTempPath();
        }

        return Path.Combine(local, AppFolderName);
    }

    /// <summary>
    /// 探测根目录可写：真的建 logs 与 backups 并写一个探针文件。
    /// </summary>
    /// <param name="baseDirectory">候选根目录。</param>
    private static bool CanWriteBaseDirectory(string baseDirectory)
    {
        try
        {
            string logs = Path.Combine(baseDirectory, LogFolderName);
            string backups = Path.Combine(baseDirectory, BackupFolderName);
            Directory.CreateDirectory(logs);
            Directory.CreateDirectory(backups);

            string probe = Path.Combine(logs, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            // 唯一允许"吞掉并继续"的一类失败之一（架构 §6.2 ①）：装在只读目录不是启动失败的理由。
            return false;
        }
    }
}

/// <summary>
/// 文件日志：UTF-8 with BOM（记事本友好）、每行 <c>yyyy-MM-dd HH:mm:ss [LEVEL] message</c>、逐行刷新。
/// </summary>
/// <remarks>
/// 写入失败时<b>降级而不是抛</b>：日志是排障手段，不是业务前提。若日志写不进去就抛异常，
/// 用户会在"日志目录没有权限"这种小问题上直接启动失败。降级后信息仍写 stderr，
/// 且 <see cref="IsDisabled"/> 让调用方能感知"日志其实没落地"。
/// </remarks>
public sealed class FileLogger : IAppLog, IDisposable
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private bool _disposed;

    private FileLogger(string path, StreamWriter? writer)
    {
        LogPath = path;
        _writer = writer;
    }

    /// <summary>日志文件路径（即便写入被禁用也保留路径，供界面提示实际落点）。</summary>
    public string LogPath { get; }

    /// <summary>写入是否已被禁用（打开失败 → 只写 stderr）。</summary>
    public bool IsDisabled => _writer is null;

    /// <summary>
    /// 按落点解析创建当日日志，并把"实际落点"作为<b>首行</b>写入。
    /// </summary>
    /// <param name="paths">落点。</param>
    /// <param name="now">时间（可注入，便于单测）。</param>
    /// <remarks>
    /// 首行固定记录落点是刻意的：unpackaged 应用可能被装进 <c>Program Files</c>，
    /// 此时产物整体回落到 <c>%LOCALAPPDATA%</c>。用户按"exe 旁边应该有 logs"去找会找不到，
    /// 而首行直接告诉他真实位置（界面上的「打开日志目录」用的也是这个落点）。
    /// </remarks>
    public static FileLogger Create(IAppPaths paths, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        FileLogger logger = Create(paths.LogFileFor(now ?? DateTimeOffset.Now));
        string mode = paths.IsPortable ? "便携模式" : "已回落到 %LOCALAPPDATA%";
        logger.Info($"落点：{paths.BaseDirectory}（{mode}）");
        return logger;
    }

    /// <summary>
    /// 打开（或创建）日志文件。打开失败不抛异常，返回降级实例。
    /// </summary>
    /// <param name="path">日志文件完整路径。</param>
    public static FileLogger Create(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream, Utf8Bom) { AutoFlush = true };
            return new FileLogger(path, writer);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            TryWriteStderr($"日志文件打开失败（已降级为仅 stderr）：{path}；{ex.GetType().Name}: {ex.Message}");
            return new FileLogger(path, writer: null);
        }
    }

    /// <inheritdoc />
    public void Info(string message) => Write("INFO", message, ex: null);

    /// <inheritdoc />
    public void Warn(string message) => Write("WARN", message, ex: null);

    /// <inheritdoc />
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>关闭底层文件句柄。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer?.Dispose();
            }
            catch (IOException)
            {
                // 关闭失败无需上报（进程即将退出）。
            }
        }
    }

    private void Write(string level, string message, Exception? ex)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_writer is null)
            {
                TryWriteStderr(line);
                return;
            }

            try
            {
                _writer.WriteLine(line);
                if (ex is not null)
                {
                    _writer.WriteLine(
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] 异常：{ex.GetType().FullName}: {ex.Message}");
                    _writer.WriteLine(ex.StackTrace ?? "(无堆栈)");
                }
            }
            catch (Exception writeError) when (writeError is IOException or ObjectDisposedException)
            {
                // 唯一允许"吞掉并继续"的一类失败之一（架构 §6.2 ①）：磁盘满/句柄失效时
                // 不能让业务逻辑因为写日志而失败。
                TryWriteStderr($"{line}（写入日志失败：{writeError.Message}）");
            }
        }
    }

    private static void TryWriteStderr(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
        }
        catch (IOException)
        {
            // stderr 也不可用（无控制台宿主的极端情况）：静默丢弃，不阻断任何流程。
        }
    }
}

/// <summary>
/// 什么都不做的日志实现。单测与试运行使用。
/// </summary>
public sealed class NullLog : IAppLog
{
    /// <summary>共享实例（无状态）。</summary>
    public static readonly NullLog Instance = new();

    /// <inheritdoc />
    public void Info(string message)
    {
    }

    /// <inheritdoc />
    public void Warn(string message)
    {
    }

    /// <inheritdoc />
    public void Error(string message, Exception? ex = null)
    {
    }
}
