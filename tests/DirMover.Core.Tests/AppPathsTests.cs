using DirMover.Core.Diagnostics;

namespace DirMover.Core.Tests;

/// <summary>
/// 落点解析（T2 新增验收项）：可写则便携、不可写则<b>整体</b>回落，
/// 且单实例 PID 文件不随落点变化。
/// </summary>
/// <remarks>
/// 为什么"整体回落"必须被断言：如果一半一半（日志在 exe 旁、快照在 LocalAppData），
/// 「打开日志目录」与"一键回滚"会指向不同根，用户在排障时按提示找快照会找不到——
/// 而"一键回滚"恰恰是出问题时的最后手段。
/// </remarks>
public sealed class AppPathsTests
{
    private static readonly DateTimeOffset FixedTime = new(2025, 11, 12, 9, 8, 7, TimeSpan.Zero);

    [Fact]
    public void Resolve_UsesPortableLayoutWhenExeDirectoryIsWritable()
    {
        using var sandbox = new TempDirectory("paths-portable");

        DefaultAppPaths paths = DefaultAppPaths.Resolve(sandbox.Root);

        Assert.True(paths.IsPortable);
        Assert.Equal(sandbox.Root, paths.BaseDirectory);
        Assert.Equal(Path.Combine(sandbox.Root, "logs"), paths.LogDirectory);
        Assert.Equal(Path.Combine(sandbox.Root, "backups"), paths.BackupDirectory);
        Assert.Equal(Path.Combine(sandbox.Root, "logs", "startup_error.txt"), paths.StartupErrorFile);
    }

    [Fact]
    public void Resolve_FallsBackEntirelyWhenExeDirectoryIsUnusable()
    {
        using var sandbox = new TempDirectory("paths-fallback");
        string fileAsDirectory = sandbox.CreateFile("not-a-directory.txt");

        DefaultAppPaths portable = DefaultAppPaths.Resolve(sandbox.Root);
        DefaultAppPaths fallback = DefaultAppPaths.Resolve(fileAsDirectory);

        Assert.False(fallback.IsPortable);

        // "整体"回落：logs 与 backups 必须同在一个根下，且都不在 exe 旁。
        string expectedBase = fallback.BaseDirectory;
        Assert.Equal(Path.Combine(expectedBase, "logs"), fallback.LogDirectory);
        Assert.Equal(Path.Combine(expectedBase, "backups"), fallback.BackupDirectory);
        Assert.False(fallback.LogDirectory.StartsWith(sandbox.Root, StringComparison.OrdinalIgnoreCase));
        Assert.False(fallback.BackupDirectory.StartsWith(sandbox.Root, StringComparison.OrdinalIgnoreCase));

        // PID 文件不随落点变化：换目录后仍能认出"上一个实例"（两个实例同时删源是数据事故）。
        Assert.Equal(portable.SingleInstancePidFile, fallback.SingleInstancePidFile);
        Assert.EndsWith(@"DirMover\single_instance.pid", fallback.SingleInstancePidFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileNamesFollowContract()
    {
        using var sandbox = new TempDirectory("paths-names");
        DefaultAppPaths paths = DefaultAppPaths.Resolve(sandbox.Root);

        Assert.EndsWith(@"logs\DirMover-20251112.log", paths.LogFileFor(FixedTime), StringComparison.Ordinal);
        Assert.EndsWith(@"logs\scan-20251112-090807.txt", paths.ScanFileFor(FixedTime), StringComparison.Ordinal);
    }

    [Fact]
    public void FileLogger_WritesActualPlacementAsFirstLine()
    {
        using var sandbox = new TempDirectory("paths-logger");
        var paths = new TempAppPaths(sandbox.Root);

        using (FileLogger logger = FileLogger.Create(paths, FixedTime))
        {
            Assert.False(logger.IsDisabled);
            logger.Info("第二条");
        }

        string logFile = paths.LogFileFor(FixedTime);
        Assert.True(File.Exists(logFile));

        string[] lines = File.ReadAllLines(logFile);
        Assert.True(lines.Length >= 2);
        Assert.Contains("[INFO]", lines[0], StringComparison.Ordinal);
        Assert.Contains("落点：", lines[0], StringComparison.Ordinal);
        Assert.Contains(paths.BaseDirectory, lines[0], StringComparison.Ordinal);
        Assert.Contains("便携模式", lines[0], StringComparison.Ordinal);
        Assert.Contains("第二条", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void FileLogger_DegradesInsteadOfThrowingWhenPathIsUnusable()
    {
        using var sandbox = new TempDirectory("paths-logger-bad");
        string fileAsDirectory = sandbox.CreateFile("blocking.txt");
        string unusable = Path.Combine(fileAsDirectory, "DirMover.log");

        using FileLogger logger = FileLogger.Create(unusable);

        Assert.True(logger.IsDisabled);
        logger.Warn("这条只会进 stderr");

        // 降级时不得创建出任何文件（否则说明"打开成功"的判断有问题）。
        Assert.False(File.Exists(unusable));
    }
}
