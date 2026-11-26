using System.Text;
using DirMover.Core.Interop;
using DirMover.Core.IO;
using Xunit.Abstractions;

namespace DirMover.Core.Tests;

/// <summary>
/// 路径工具（T4 验收项 ①–⑥）：长路径前缀、规范化比较、物理同位置、8.3 长名还原、
/// 盘枚举、可用空间、体积格式化。
/// </summary>
public sealed class PathToolsTests
{
    private readonly ITestOutputHelper _output;

    public PathToolsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ================================================================ 长路径前缀

    [Fact]
    public void ToExtended_LeavesShortPathsUntouched()
    {
        Assert.Equal(@"D:\UserData", PathTools.ToExtended(@"D:\UserData"));
    }

    [Fact]
    public void ToExtended_PrependsPrefixOnlyAtThreshold()
    {
        string belowThreshold = @"C:\" + new string('a', Catalog.LongPathThreshold - 4);
        Assert.True(belowThreshold.Length < Catalog.LongPathThreshold);
        Assert.Equal(belowThreshold, PathTools.ToExtended(belowThreshold));

        string aboveThreshold = @"C:\" + new string('b', Catalog.LongPathThreshold + 40);
        string extended = PathTools.ToExtended(aboveThreshold);
        Assert.StartsWith(@"\\?\", extended, StringComparison.Ordinal);
        Assert.Equal(@"\\?\" + Path.GetFullPath(aboveThreshold), extended);
    }

    [Fact]
    public void ToExtended_ConvertsUncToUncExtendedPrefix()
    {
        string unc = @"\\server\share\" + new string('c', Catalog.LongPathThreshold + 20);
        string extended = PathTools.ToExtended(unc);
        Assert.StartsWith(@"\\?\UNC\server\share\", extended, StringComparison.Ordinal);
    }

    [Fact]
    public void ToExtended_DoesNotDoublePrefix()
    {
        string already = @"\\?\C:\" + new string('d', Catalog.LongPathThreshold + 20);
        Assert.Equal(already, PathTools.ToExtended(already));
        Assert.Equal(@"\\.\C:\x", PathTools.ToExtended(@"\\.\C:\x"));
    }

    [Fact]
    public void FromExtended_And_StripExtendedPrefixRestoreCleanPaths()
    {
        Assert.Equal(@"\\server\share\x", PathTools.FromExtended(@"\\?\UNC\server\share\x"));
        Assert.Equal(@"C:\x", PathTools.FromExtended(@"\\?\C:\x"));
        Assert.Equal(@"C:\x", PathTools.StripExtendedPrefix(@"\\?\C:\x"));
        Assert.Equal(@"C:\x", PathTools.StripExtendedPrefix(@"\\.\C:\x"));
        Assert.Equal(@"C:\x", PathTools.StripExtendedPrefix(@"C:\x"));
    }

    // ================================================================ 规范化比较

    [Fact]
    public void SamePath_IgnoresCaseAndTrailingSeparator()
    {
        Assert.True(PathTools.SamePath(@"C:\Users\Me\Documents", @"c:\users\me\documents\"));
        Assert.False(PathTools.SamePath(@"C:\Users\Me\Documents", @"C:\Users\Me\Document"));
    }

    [Fact]
    public void SamePath_TreatsEmptyAsNotEqual()
    {
        // 两个空串被判为相同会让"当前位置未知"的项被当成「无需转移」静默跳过（那是漏项）。
        Assert.False(PathTools.SamePath(string.Empty, string.Empty));
        Assert.False(PathTools.SamePath(@"C:\x", string.Empty));
    }

    [Fact]
    public void Normalize_PreservesDriveRootSeparator()
    {
        Assert.Equal(@"c:\", PathTools.Normalize(@"C:\"));
        Assert.Equal(@"c:\users\me", PathTools.Normalize(@"C:\Users\Me\"));
    }

    [Fact]
    public void IsWithin_MatchesSelfAndDescendantsOnly()
    {
        Assert.True(PathTools.IsWithin(@"C:\a\b", @"C:\a"));
        Assert.True(PathTools.IsWithin(@"C:\a", @"C:\a"));
        Assert.True(PathTools.IsWithin(@"C:\a\b", @"c:\A\"));
        Assert.False(PathTools.IsWithin(@"C:\ab", @"C:\a"));
        Assert.False(PathTools.IsWithin(@"C:\a", @"C:\a\b"));
        Assert.False(PathTools.IsWithin(string.Empty, @"C:\a"));
    }

    // ================================================================ 物理同位置

    [Fact]
    public void ResolvesToSame_DetectsJunctionPointingAtSource()
    {
        using var sandbox = new TempDirectory("samefile");
        string real = sandbox.CreateDirectoryPath("real");
        sandbox.CreateFile(@"real\a.txt");
        string link = sandbox.Combine("link");

        if (!TestEnv.CanCreateJunction || !TestEnv.TryCreateJunction(link, real))
        {
            _output.WriteLine("[ENV-MISSING] ResolvesToSame_DetectsJunctionPointingAtSource 未执行：本机无法创建目录联接点（CanCreateJunction 为假或 TryCreateJunction 失败），不计为通过");
            return;
        }

        Assert.False(PathTools.SamePath(link, real), "字符串形态必须不同，否则这条用例没意义");
        Assert.True(PathTools.ResolvesToSame(link, real));
        Assert.True(PathTools.ResolvesToSame(real, real));
    }

    // ================================================================ 8.3 短名还原

    [Fact]
    public void LongPath_RestoresEightDotThreeShortName()
    {
        string programFiles = Path.Combine(Catalog.SystemDrive + "\\", "Program Files");
        if (!Directory.Exists(programFiles))
        {
            _output.WriteLine($"[ENV-MISSING] LongPath_RestoresEightDotThreeShortName 未执行：{programFiles} 不存在，无法构造短名形态，不计为通过");
            return;
        }

        string? shortName = TryGetShortName(programFiles);
        if (shortName is null)
        {
            _output.WriteLine($"[ENV-MISSING] LongPath_RestoresEightDotThreeShortName 未执行：该卷关闭了 8.3 名生成（GetShortPathNameW 返回与长名一致），无法构造短名形态，不计为通过");
            return;
        }

        Assert.Equal(programFiles, PathTools.LongPath(shortName));
        Assert.Equal(programFiles + @"\sub", PathTools.LongPath(shortName + @"\sub"));
    }

    [Fact]
    public void LongPath_ReturnsInputWhenNothingToRestore()
    {
        using var sandbox = new TempDirectory("longpath");
        string existing = sandbox.CreateDirectoryPath("plain");
        Assert.Equal(existing, PathTools.LongPath(existing));
    }

    // ================================================================ 体积与基名

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1099511627776L, "1.0 TB")]
    public void HumanSize_UsesBinaryUnitsWithOneDecimal(long bytes, string expected)
    {
        Assert.Equal(expected, PathTools.HumanSize(bytes));
    }

    [Fact]
    public void HumanSize_NegativeIsClampedToZero()
    {
        Assert.Equal("0 B", PathTools.HumanSize(-1));
    }

    [Fact]
    public void TailName_TakesLastSegmentNotDisplayName()
    {
        Assert.Equal("Documents", PathTools.TailName(@"C:\Users\me\Documents"));
        Assert.Equal("Documents", PathTools.TailName(@"C:\Users\me\Documents\"));
        Assert.Equal(string.Empty, PathTools.TailName(string.Empty));
    }

    // ================================================================ 重解析点

    [Fact]
    public void IsReparsePoint_IsFalseForPlainDirectoryAndMissingPath()
    {
        using var sandbox = new TempDirectory("reparse");
        string plain = sandbox.CreateDirectoryPath("plain");

        Assert.False(PathTools.IsReparsePoint(plain));
        Assert.False(PathTools.IsReparsePoint(sandbox.Combine("never-existed")));
    }

    [Fact]
    public void IsReparsePoint_IsTrueForJunction()
    {
        using var sandbox = new TempDirectory("reparse-link");
        string target = sandbox.CreateDirectoryPath("target");
        string link = sandbox.Combine("link");

        if (!TestEnv.CanCreateJunction || !TestEnv.TryCreateJunction(link, target))
        {
            _output.WriteLine("[ENV-MISSING] IsReparsePoint_IsTrueForJunction 未执行：本机无法创建目录联接点（CanCreateJunction 为假或 TryCreateJunction 失败），不计为通过");
            return;
        }

        Assert.True(PathTools.IsReparsePoint(link));
    }

    // ================================================================ 盘与空间

    [Fact]
    public void FreeSpace_ReturnsValueForExistingDirectoryAndNullForUnknown()
    {
        using var sandbox = new TempDirectory("free-space");

        long? free = PathTools.FreeSpace(sandbox.Root);
        Assert.NotNull(free);
        Assert.True(free!.Value >= 0);

        // 未知盘符必须返回 null（不是 0、也不是抛异常）：0 会被调用方当成"无需检查"而放行，
        // 探测失败时就会一路复制到磁盘写满。盘符不能写死 Z:——本机 Z: 被映射成网络盘后
        // GetDiskFreeSpaceExW 会成功，这条与本次修复无关的用例会假红。现取一个确实不存在的盘符。
        string unusedDrive = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
            .Select(letter => letter + ":")
            .First(letter => !Directory.Exists(letter + "\\"));
        Assert.Null(PathTools.FreeSpace(unusedDrive + @"\no-such-drive\no-such-dir"));
    }

    [Fact]
    public void ListFixedNonSystem_ExcludesSystemDrive()
    {
        IReadOnlyList<DriveVolume> drives = DriveInventory.ListFixedNonSystem();

        // 允许多种机器状态（可能只有一个 C 盘）：只断言"不含系统盘、且条目都自洽"。
        Assert.DoesNotContain(drives, d => string.Equals(d.Letter, Catalog.SystemDrive, StringComparison.OrdinalIgnoreCase));
        foreach (DriveVolume drive in drives)
        {
            Assert.Matches("^[A-Z]:$", drive.Letter);
            Assert.Equal(drive.Letter + @"\", drive.Root);
            Assert.Equal(drive.Letter + @"\UserData", drive.TargetRoot);
            Assert.True(drive.TotalBytes >= drive.FreeBytes);
        }
    }

    [Fact]
    public void PreferredTargetRoot_PrefersDThenFirstThenEmpty()
    {
        Assert.Equal(string.Empty, DriveInventory.PreferredTargetRoot(Array.Empty<DriveVolume>()));

        var d = new DriveVolume("D:", @"D:\", 2000, 1000);
        var e = new DriveVolume("E:", @"E:\", 4000, 3000);
        Assert.Equal(@"D:\UserData", DriveInventory.PreferredTargetRoot(new[] { e, d }));
        Assert.Equal(@"E:\UserData", DriveInventory.PreferredTargetRoot(new[] { e }));
    }

    [Fact]
    public void DriveVolume_SpaceLabelMatchesPrdWording()
    {
        var drive = new DriveVolume(
            "D:",
            @"D:\",
            TotalBytes: (long)(2.0 * 1024 * 1024 * 1024 * 1024),
            FreeBytes: (long)(1.2 * 1024 * 1024 * 1024 * 1024));

        Assert.Equal(@"D:\  可用 1.2 TB / 2.0 TB", drive.SpaceLabel);
    }

    private static string? TryGetShortName(string longPath)
    {
        var buffer = new StringBuilder(1024);
        uint length = NativeMethods.GetShortPathNameW(longPath, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
        {
            return null;
        }

        string shortName = buffer.ToString();
        return string.Equals(shortName, longPath, StringComparison.OrdinalIgnoreCase) ? null : shortName;
    }
}
