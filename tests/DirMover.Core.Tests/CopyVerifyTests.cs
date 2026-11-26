using DirMover.Core.IO;
using Xunit.Abstractions;

namespace DirMover.Core.Tests;

/// <summary>T8：7 维校验、原子移动、目录创建、安全删源。</summary>
public sealed class CopyVerifyTests
{
    private readonly ITestOutputHelper _output;

    public CopyVerifyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void VerifyCopy_CleanTree_IsVerified()
    {
        using var sandbox = new TempDirectory("verify-ok");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        sandbox.CreateFile(Path.Combine("src", "sub", "b.txt"), "world");
        Directory.CreateDirectory(Path.Combine(src, "empty"));
        string dst = sandbox.Combine("dst");

        CopyResult copy = FsOps.CopyMerge(src, dst);
        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.True(copy.Ok);
        Assert.True(verify.Verified, verify.Detail);
    }

    [Fact]
    public void VerifyCopy_MissingTargetFile_FailsWithDimensionAndSample()
    {
        using var sandbox = new TempDirectory("verify-missing");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = sandbox.Combine("dst");
        CopyResult copy = FsOps.CopyMerge(src, dst);

        File.Delete(Path.Combine(dst, "a.txt"));
        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.False(verify.Verified);
        Assert.Contains("a.txt", verify.Detail);
        Assert.False(verify.Cancelled);
    }

    [Fact]
    public void VerifyCopy_MissingEmptyDirectory_Fails()
    {
        using var sandbox = new TempDirectory("verify-missing-dir");
        string src = sandbox.CreateDirectoryPath("src");
        Directory.CreateDirectory(Path.Combine(src, "empty"));
        string dst = sandbox.Combine("dst");
        CopyResult copy = FsOps.CopyMerge(src, dst);

        Directory.Delete(Path.Combine(dst, "empty"));
        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.False(verify.Verified);
        Assert.Contains("empty", verify.Detail);
    }

    [Fact]
    public void VerifyCopy_SourceModifiedAfterCopy_Fails()
    {
        using var sandbox = new TempDirectory("verify-src-changed");
        string src = sandbox.CreateDirectoryPath("src");
        string file = sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = sandbox.Combine("dst");
        CopyResult copy = FsOps.CopyMerge(src, dst);

        File.WriteAllText(file, "hello-but-longer");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));

        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.False(verify.Verified);
        Assert.Contains("a.txt", verify.Detail);
    }

    [Fact]
    public void VerifyCopy_SourceGainedNewFile_Fails()
    {
        using var sandbox = new TempDirectory("verify-src-added");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = sandbox.Combine("dst");
        CopyResult copy = FsOps.CopyMerge(src, dst);

        sandbox.CreateFile(Path.Combine("src", "later.txt"), "appeared");

        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.False(verify.Verified);
        Assert.Contains("多出", verify.Detail);
    }

    [Fact]
    public void VerifyCopy_SkippedNames_FailsClosed()
    {
        using var sandbox = new TempDirectory("verify-skipped");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "keep.txt"), "a");
        sandbox.CreateFile(Path.Combine("src", "UserData", "skip.txt"), "b");
        string dst = sandbox.Combine("dst");

        CopyResult copy = FsOps.CopyMerge(src, dst, skipNames: new[] { "UserData" });
        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.NotEmpty(copy.Skipped);
        Assert.False(verify.Verified);
        Assert.Contains("跳过", verify.Detail);
    }

    [Fact]
    public void VerifyCopy_DanglingSymlink_IsRebuiltAndDoesNotFailForever()
    {
        using var sandbox = new TempDirectory("verify-dangling");
        string src = sandbox.CreateDirectoryPath("src");
        string missing = sandbox.Combine("does-not-exist");
        string link = Path.Combine(src, "dangling");
        try
        {
            File.CreateSymbolicLink(link, missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _output.WriteLine($"[ENV-MISSING] VerifyCopy_DanglingSymlink_IsRebuiltAndDoesNotFailForever 未执行：本机无法创建符号链接（{ex.GetType().Name}），不计为通过");
            return;
        }

        string dst = sandbox.Combine("dst");
        CopyResult copy = FsOps.CopyMerge(src, dst);
        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.True(copy.Ok);
        Assert.True(verify.Verified, verify.Detail);
    }

    [Fact]
    public void TryAtomicMove_SameVolume_Moves()
    {
        using var sandbox = new TempDirectory("move-same");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");

        MoveResult result = FsOps.TryAtomicMove(src, sandbox.Combine("dst"));

        Assert.True(result.Moved);
        Assert.False(Directory.Exists(src));
        Assert.True(File.Exists(sandbox.Combine("dst", "a.txt")));
    }

    [Fact]
    public void TryAtomicMove_TargetExists_RefusesAndSaysUseCopy()
    {
        using var sandbox = new TempDirectory("move-exists");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = sandbox.CreateDirectoryPath("dst");

        MoveResult result = FsOps.TryAtomicMove(src, dst);

        Assert.False(result.Moved);
        Assert.Contains("复制", result.Message);
        Assert.True(Directory.Exists(src));
    }

    [Fact]
    public void TryAtomicMove_CrossVolume_Refuses()
    {
        IReadOnlyList<DriveVolume> drives = DriveInventory.ListFixedNonSystem();
        if (drives.Count == 0)
        {
            _output.WriteLine("[ENV-MISSING] TryAtomicMove_CrossVolume_Refuses 未执行：本机无非系统固定盘（ListFixedNonSystem 返回 0 个），不计为通过");
            return;
        }

        using var sandbox = new TempDirectory("move-cross");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");

        string dst;
        try
        {
            dst = Path.Combine(drives[0].Root, "dirmover-cross-" + Guid.NewGuid().ToString("N"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _output.WriteLine($"[ENV-MISSING] TryAtomicMove_CrossVolume_Refuses 未执行：无法在 {drives[0].Root} 上构造跨卷临时目录（{ex.GetType().Name}），不计为通过");
            return;
        }

        MoveResult result = FsOps.TryAtomicMove(src, dst);

        Assert.False(result.Moved);
        Assert.Contains("复制", result.Message);
        Assert.True(Directory.Exists(src));
    }

    [Fact]
    public void EnsureDirectory_CreatesNestedPath()
    {
        using var sandbox = new TempDirectory("ensure-dir");
        string deep = sandbox.Combine("a", "b", "c");

        OpResult result = FsOps.EnsureDirectory(deep);

        Assert.True(result.Ok);
        Assert.True(Directory.Exists(deep));
    }

    [Fact]
    public void SafeRemoveDirectory_ClearsReadOnlyAndDeletes()
    {
        using var sandbox = new TempDirectory("remove-readonly");
        string dir = sandbox.CreateDirectoryPath("tree");
        string file = Path.Combine(dir, "locked.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        OpResult result = FsOps.SafeRemoveDirectory(dir);

        Assert.True(result.Ok);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void SafeRemoveDirectory_LockedFile_FailsWithRestartHint()
    {
        using var sandbox = new TempDirectory("remove-locked");
        string dir = sandbox.CreateDirectoryPath("tree");
        string file = Path.Combine(dir, "busy.txt");
        File.WriteAllText(file, "x");

        using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            OpResult result = FsOps.SafeRemoveDirectory(dir);

            Assert.False(result.Ok);
            Assert.Contains("重启本机后即可删除", result.Message);
        }
    }

    [Fact]
    public void SafeRemoveDirectory_MissingDirectory_IsSuccess()
    {
        using var sandbox = new TempDirectory("remove-missing");

        OpResult result = FsOps.SafeRemoveDirectory(sandbox.Combine("nothing-here"));

        Assert.True(result.Ok);
    }
}
