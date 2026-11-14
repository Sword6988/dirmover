using DirMover.Core.Engine;
using DirMover.Core.Interop;
using DirMover.Core.IO;

namespace DirMover.Core.Tests;

/// <summary>T6：清点与规模统计。</summary>
public sealed class FsOpsScanTests
{
    [Fact]
    public void EnumerateSource_EmptyDirectory_IsRecorded()
    {
        using var sandbox = new TempDirectory("scan-empty");
        string src = sandbox.CreateDirectoryPath("src");
        Directory.CreateDirectory(Path.Combine(src, "empty"));
        sandbox.CreateFile(Path.Combine("src", "file.txt"), "hello");

        SourceTree tree = FsOps.EnumerateSource(src);

        Assert.Empty(tree.Errors);
        Assert.Contains("empty", tree.Dirs);
        Assert.True(tree.Files.ContainsKey("file.txt"));
        Assert.Equal("hello".Length, tree.Files["file.txt"].Size);
    }

    [Fact]
    public void EnumerateSource_Junction_IsLinkedAndNotRecursed()
    {
        using var sandbox = new TempDirectory("scan-link");
        string src = sandbox.CreateDirectoryPath("src");
        string outside = sandbox.CreateDirectoryPath("outside");
        sandbox.CreateFile(Path.Combine("outside", "deep.txt"), "should-not-be-scanned");

        string link = Path.Combine(src, "link");
        if (!TestEnv.CanCreateJunction || !TestEnv.TryCreateJunction(link, outside))
        {
            return;
        }

        SourceTree tree = FsOps.EnumerateSource(src);

        Assert.Empty(tree.Errors);
        Assert.Contains("link", tree.Links);
        // 关键：绝不递归进链接 —— 否则 "deep.txt" 会出现在清单里。
        Assert.DoesNotContain(tree.Files.Keys, key => key.Contains("deep.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnumerateSource_SkipNames_GoesToSkipped()
    {
        using var sandbox = new TempDirectory("scan-skip");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "keep.txt"), "a");
        sandbox.CreateFile(Path.Combine("src", "UserData", "skip.txt"), "b");

        SourceTree tree = FsOps.EnumerateSource(src, new[] { "UserData" });

        Assert.Empty(tree.Errors);
        Assert.Contains("UserData", tree.Skipped);
        Assert.False(tree.Files.ContainsKey(@"UserData\skip.txt"));
        Assert.True(tree.Files.ContainsKey("keep.txt"));
    }

    [Fact]
    public void EnumerateSource_RootIsFile_ReportsErrorInsteadOfSilentlySkipping()
    {
        using var sandbox = new TempDirectory("scan-file-root");
        string file = sandbox.CreateFile("plain.txt", "x");

        SourceTree tree = FsOps.EnumerateSource(file);

        Assert.NotEmpty(tree.Errors);
    }

    [Fact]
    public void EnumerateSource_UnreadableSubdirectory_GoesToErrors()
    {
        using var sandbox = new TempDirectory("scan-denied");
        string src = sandbox.CreateDirectoryPath("src");
        string locked = Path.Combine(src, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "secret.txt"), "x");

        // 用 FILE_FLAG_BACKUP_SEMANTICS + 共享模式 0 独占打开该子目录：
        // 之后任何枚举都会得到"共享冲突"，等价于"这一项读不到"。
        const uint backupSemantics = 0x02000000;
        IntPtr handle = NativeMethods.CreateFileW(
            PathTools.ToExtended(locked),
            NativeMethods.GenericRead,
            shareMode: 0,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            backupSemantics,
            IntPtr.Zero);

        // 自己刚建的临时目录，独占打开必然成功；失败说明环境异常，应当红而不是静默跳过。
        Assert.True(handle != IntPtr.Zero && handle != new IntPtr(-1), "无法独占打开临时子目录");

        try
        {
            SourceTree tree = FsOps.EnumerateSource(src);

            // 关键：读不到必须进 Errors，不得静默跳过（静默跳过 + 删源 = 丢数据）。
            Assert.NotEmpty(tree.Errors);
            Assert.True(FsOps.ScanTree(src).Unreadable >= 1);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    [Fact]
    public void ScanTree_CountsFilesDirsAndBytes()
    {
        using var sandbox = new TempDirectory("scan-stat");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "12345");
        sandbox.CreateFile(Path.Combine("src", "sub", "b.txt"), "123");
        Directory.CreateDirectory(Path.Combine(src, "empty"));

        TreeStat stat = FsOps.ScanTree(src);

        Assert.Equal(2, stat.Files);
        Assert.Equal(2, stat.Dirs);
        Assert.Equal(8, stat.Bytes);
        Assert.Equal(0, stat.Unreadable);
        Assert.Equal("2 个文件 / 8 B", stat.Describe());
    }

    [Fact]
    public void ScanTree_LimitFiles_StopsEarly()
    {
        using var sandbox = new TempDirectory("scan-limit");
        string src = sandbox.CreateDirectoryPath("src");
        for (int index = 0; index < 20; index++)
        {
            sandbox.CreateFile(Path.Combine("src", $"f{index}.txt"), "x");
        }

        TreeStat stat = FsOps.ScanTree(src, limitFiles: 3);

        Assert.Equal(3, stat.Files);
    }

    [Fact]
    public void ScanTree_RootIsFile_MarksUnreadable()
    {
        using var sandbox = new TempDirectory("scan-unreadable");
        string file = sandbox.CreateFile("plain.txt", "x");

        TreeStat stat = FsOps.ScanTree(file);

        Assert.True(stat.Unreadable > 0);
    }

    [Fact]
    public void Fingerprint_ContainsIdentityFields()
    {
        using var sandbox = new TempDirectory("scan-fingerprint");
        string file = sandbox.CreateFile("a.txt", "hello world");

        FileFingerprint fingerprint = FsOps.FingerprintOf(file, withDigest: true);

        Assert.Equal("hello world".Length, fingerprint.Size);
        Assert.True(fingerprint.ModifiedTicksUtc > 0);
        Assert.Equal(64, fingerprint.Digest.Length);
        Assert.NotEqual(0u, fingerprint.Device);
        Assert.NotEqual(0ul, fingerprint.Inode);
        Assert.True(fingerprint.LinkCount >= 1);
        Assert.NotEqual(0, (int)fingerprint.Attributes);
    }




}

/// <summary>T7：安全复制（分块 + 摘要、稀疏、硬链接、<c>.part</c> 原子落盘、同名冲突、进度、取消）。</summary>
public sealed class FsOpsCopyTests
{
    [Fact]
    public void CopyMerge_SimpleTree_CopiesFilesDirsAndKeepsSource()
    {
        using var sandbox = new TempDirectory("copy-simple");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        sandbox.CreateFile(Path.Combine("src", "sub", "b.txt"), "world");
        Directory.CreateDirectory(Path.Combine(src, "empty"));
        string dst = sandbox.Combine("dst");

        CopyResult result = FsOps.CopyMerge(src, dst);

        Assert.True(result.Ok);
        Assert.Equal(2, result.CopiedFiles);
        Assert.Equal(10, result.CopiedBytes);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(dst, "sub", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(dst, "empty")));
        // 复制绝不删除源
        Assert.True(File.Exists(Path.Combine(src, "a.txt")));
    }

    [Fact]
    public void CopyMerge_SameNameDifferentContent_SavesBothAndRecordsConflict()
    {
        using var sandbox = new TempDirectory("copy-conflict");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "source-content");
        string dst = sandbox.CreateDirectoryPath("dst");
        File.WriteAllText(Path.Combine(dst, "a.txt"), "target-content");

        CopyResult result = FsOps.CopyMerge(src, dst);

        Assert.True(result.Ok);
        Assert.Single(result.Conflicts);
        Assert.Contains("a (1).txt", result.Conflicts[0]);
        // 红线：绝不静默覆盖
        Assert.Equal("target-content", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.Equal("source-content", File.ReadAllText(Path.Combine(dst, "a (1).txt")));
    }

    [Fact]
    public void CopyMerge_SameSizeAndMtimeButDifferentContent_IsNotReused()
    {
        using var sandbox = new TempDirectory("copy-reuse-negative");
        string src = sandbox.CreateDirectoryPath("src");
        string sourceFile = sandbox.CreateFile(Path.Combine("src", "a.bin"), "AAAA");
        string dst = sandbox.CreateDirectoryPath("dst");
        string targetFile = Path.Combine(dst, "a.bin");
        File.WriteAllText(targetFile, "BBBB");

        DateTime stamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sourceFile, stamp);
        File.SetLastWriteTimeUtc(targetFile, stamp);

        CopyResult result = FsOps.CopyMerge(src, dst);

        Assert.True(result.Ok);
        Assert.False(result.Items[0].Reused);
        Assert.Single(result.Conflicts);
        Assert.Equal("BBBB", File.ReadAllText(targetFile));
        Assert.Equal("AAAA", File.ReadAllText(Path.Combine(dst, "a (1).bin")));
    }

    [Fact]
    public void CopyMerge_IdenticalExistingFile_IsReused()
    {
        using var sandbox = new TempDirectory("copy-reuse");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "same-bytes");
        string dst = sandbox.CreateDirectoryPath("dst");
        File.WriteAllText(Path.Combine(dst, "a.txt"), "same-bytes");

        CopyResult result = FsOps.CopyMerge(src, dst);

        Assert.True(result.Ok);
        Assert.True(result.Items[0].Reused);
        Assert.Empty(result.Conflicts);
        Assert.Equal(0, result.CopiedFiles);
        Assert.NotEmpty(result.Items[0].Fingerprint.Digest);
    }

    [Fact]
    public void CopyMerge_RebuildsSymlink()
    {
        using var sandbox = new TempDirectory("copy-link");
        string src = sandbox.CreateDirectoryPath("src");
        string real = sandbox.Combine("real-target");
        Directory.CreateDirectory(real);
        string link = Path.Combine(src, "link");
        if (!TryCreateSymlink(link, real))
        {
            return;
        }

        string dst = sandbox.Combine("dst");
        CopyResult result = FsOps.CopyMerge(src, dst);

        Assert.True(result.Ok);
        Assert.Contains("link", result.Links);
        Assert.NotNull(new DirectoryInfo(Path.Combine(dst, "link")).LinkTarget);
    }

    [Fact]
    public void CopyMerge_ReportsProgress_AndFinishesAtTotal()
    {
        using var sandbox = new TempDirectory("copy-progress");
        string src = sandbox.CreateDirectoryPath("src");
        for (int index = 0; index < 3; index++)
        {
            sandbox.CreateFile(Path.Combine("src", $"f{index}.txt"), "x");
        }

        var recorder = new RecordingProgress();
        CopyResult result = FsOps.CopyMerge(src, sandbox.Combine("dst"), recorder);

        Assert.True(result.Ok);
        Assert.NotEmpty(recorder.Reports);
        TransferProgressInfo last = recorder.Reports[^1];
        Assert.Equal(3, last.TotalInItem);
        Assert.Equal(3, last.DoneInItem);
    }

    [Fact]
    public void CopyMerge_Cancelled_LeavesNoPartFile()
    {
        using var sandbox = new TempDirectory("copy-cancel");
        string src = sandbox.CreateDirectoryPath("src");
        string big = Path.Combine(src, "big.bin");
        File.WriteAllBytes(big, new byte[3 * 1024 * 1024]);
        string dst = sandbox.Combine("dst");

        using var cts = new CancellationTokenSource();
        var recorder = new CancellingProgress(cts);
        CopyResult result = FsOps.CopyMerge(src, dst, recorder, cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFiles(dst, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public void CopyMerge_SamePhysicalPosition_ReportsErrorAndWritesNothing()
    {
        using var sandbox = new TempDirectory("copy-same");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");

        CopyResult result = FsOps.CopyMerge(src, src);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(Directory.GetFiles(src, "*.part", SearchOption.AllDirectories));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(src, "a.txt")));
    }

    [Fact]
    public void CopyMerge_SourceRootIsFile_FailsClosed()
    {
        using var sandbox = new TempDirectory("copy-badsource");
        string file = sandbox.CreateFile("plain.txt", "x");

        CopyResult result = FsOps.CopyMerge(file, sandbox.Combine("dst"));

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void CopyMerge_HardlinkedSource_IsRelinked()
    {
        using var sandbox = new TempDirectory("copy-hardlink");
        string src = sandbox.CreateDirectoryPath("src");
        string first = sandbox.CreateFile(Path.Combine("src", "one.bin"), "shared-bytes");
        string second = Path.Combine(src, "two.bin");
        if (!NativeMethods.CreateHardLinkW(second, first, IntPtr.Zero))
        {
            return;
        }

        CopyResult result = FsOps.CopyMerge(src, sandbox.Combine("dst"));

        Assert.True(result.Ok);
        Assert.Equal(1, result.HardlinkedFiles);
        Assert.Equal("shared-bytes", File.ReadAllText(Path.Combine(sandbox.Combine("dst"), "two.bin")));
    }

    [Fact]
    public void CopyMerge_HardlinkedSource_UnderLongPath_IsRelinked()
    {
        // 路径推过 \\?\ 阈值（240 字符）：硬链接分支里"重建目标时间戳"这类 BCL 调用
        // 必须与复制主路径一样走 ToExtended，不能裸调（超长路径在未开启
        // LongPathsEnabled 的机器上行为不同，参见 PathTools.ToExtended 的约定）。
        using var sandbox = new TempDirectory("copy-hardlink-long");

        string src = sandbox.Combine("src");
        string dst = sandbox.Combine("dst");
        for (int i = 0; i < 8; i++)
        {
            string segment = $"level-{i:D2}-{"abcdefghij"}-{"0123456789"}";
            src = Path.Combine(src, segment);
            dst = Path.Combine(dst, segment);
        }

        Assert.True(src.Length > Catalog.LongPathThreshold, $"测试前提：源路径须超过 {Catalog.LongPathThreshold} 字符（实际 {src.Length}）");
        Assert.True(dst.Length > Catalog.LongPathThreshold, $"测试前提：目标路径须超过 {Catalog.LongPathThreshold} 字符（实际 {dst.Length}）");

        Directory.CreateDirectory(src);
        string first = Path.Combine(src, "one.bin");
        File.WriteAllText(first, "shared-bytes");
        string second = Path.Combine(src, "two.bin");
        if (!NativeMethods.CreateHardLinkW(PathTools.ToExtended(second), PathTools.ToExtended(first), IntPtr.Zero))
        {
            return;
        }

        CopyResult result = FsOps.CopyMerge(src, dst);

        Assert.True(result.Ok);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.HardlinkedFiles);
        Assert.Equal("shared-bytes", File.ReadAllText(Path.Combine(dst, "two.bin")));
    }

    private static bool TryCreateSymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>记录型进度出口。</summary>
internal sealed class RecordingProgress : ITransferProgress
{
    public List<TransferProgressInfo> Reports { get; } = new();

    public void Report(in TransferProgressInfo info) => Reports.Add(info);
}

/// <summary>第一次上报就取消的进度出口（用来在分块循环里触发取消）。</summary>
internal sealed class CancellingProgress : ITransferProgress
{
    private readonly CancellationTokenSource _cts;

    public CancellingProgress(CancellationTokenSource cts) => _cts = cts;

    public void Report(in TransferProgressInfo info)
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }
}
