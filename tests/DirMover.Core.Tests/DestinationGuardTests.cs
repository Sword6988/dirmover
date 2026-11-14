using System.Text;
using DirMover.Core.Interop;
using DirMover.Core.IO;

namespace DirMover.Core.Tests;

/// <summary>
/// 目标/源禁区校验（P0-7 九条）与重解析点祖先链。
/// </summary>
/// <remarks>
/// 用例组织方式：每条红线至少一条"必须拒绝"，外加至少一条"必须放行"——只有拒绝侧的用例
/// 会让实现倾向于"一律拒绝"，从而把正常迁移路径也挡死。放行侧的用例通常需要临时改写
/// <see cref="Catalog.SystemDrive"/>（否则临时目录位于 <c>C:\Users\…</c> 之下，本身就属于目标禁区）。
/// </remarks>
public sealed class DestinationGuardTests
{
    // ================================================================ 基本形态（1–3 条校验）

    [Fact]
    public void EmptyTargetIsRejected()
    {
        GuardResult result = DestinationGuard.ValidateDestination("   ", Array.Empty<string>());
        Assert.False(result.Ok);
        Assert.Equal("目标路径不能为空。", result.Reason);
    }

    [Theory]
    [InlineData(@"C:\UserData\a<b")]
    [InlineData(@"C:\UserData\a|b")]
    [InlineData(@"C:\UserData\a?b")]
    [InlineData(@"C:\UserData\a*b")]
    [InlineData(@"C:\UserData\a:b")]
    public void IllegalCharactersAreRejected(string target)
    {
        GuardResult result = DestinationGuard.ValidateDestination(target, Array.Empty<string>());
        Assert.False(result.Ok);
        Assert.Contains("非法字符", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RelativePathIsRejected()
    {
        GuardResult result = DestinationGuard.ValidateDestination("UserData", Array.Empty<string>());
        Assert.False(result.Ok);
        Assert.Contains("绝对路径", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("CON.txt")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("COM¹")]
    public void ReservedDeviceNamesAreRejected(string segment)
    {
        string target = Path.Combine(Catalog.SystemDrive + "\\", "UserData", segment);
        GuardResult result = DestinationGuard.ValidateDestination(target, Array.Empty<string>());
        Assert.False(result.Ok);
        Assert.Contains("不可用", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingDotOrSpaceInAnySegmentIsRejected()
    {
        string drive = Catalog.SystemDrive + "\\";

        GuardResult dot = DestinationGuard.ValidateDestination(drive + @"UserData\abc.\def", Array.Empty<string>());
        Assert.False(dot.Ok);
        Assert.Contains("以点/空格结尾", dot.Reason, StringComparison.Ordinal);

        GuardResult space = DestinationGuard.ValidateDestination(drive + @"UserData\abc \def", Array.Empty<string>());
        Assert.False(space.Ok);
        Assert.Contains("以点/空格结尾", space.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDriveIsRejected()
    {
        string? letter = FindUnusedDriveLetter();
        if (letter is null)
        {
            return;
        }

        GuardResult result = DestinationGuard.ValidateDestination(letter + @":\UserData", Array.Empty<string>());
        Assert.False(result.Ok);
        Assert.Contains("不存在或未就绪", result.Reason, StringComparison.Ordinal);
    }

    // ================================================================ 系统目录禁区（第 4 条）

    [Theory]
    [InlineData("Windows")]
    [InlineData("Windows\\Temp")]
    [InlineData("Program Files")]
    [InlineData("Program Files (x86)")]
    [InlineData("ProgramData")]
    [InlineData("Users\\someone\\Documents")]
    public void SystemDirectoriesAreRejectedAsTarget(string relative)
    {
        string target = Path.Combine(Catalog.SystemDrive + "\\", relative);
        GuardResult result = DestinationGuard.ValidateDestination(target, Array.Empty<string>());
        Assert.False(result.Ok);
        Assert.Contains("系统目录", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtendedPrefixDoesNotBypassSystemDirectoryGuard()
    {
        // \\?\C:\Windows 与 C:\Windows 是同一目录；不剥前缀的话既不等也不包含，闸门会被绕过。
        string target = @"\\?\" + Catalog.SystemDrive + @"\Windows";
        GuardResult result = DestinationGuard.ValidateDestination(target, Array.Empty<string>());
        Assert.False(result.Ok);
    }

    [Fact]
    public void EightDotThreeShortNameDoesNotBypassSystemDirectoryGuard()
    {
        string programFiles = Path.Combine(Catalog.SystemDrive + "\\", "Program Files");
        if (!Directory.Exists(programFiles))
        {
            return;
        }

        string? shortName = TryGetShortName(programFiles);
        if (shortName is null)
        {
            // 该卷未生成 8.3 名：无法构造短名形态，软跳过。
            return;
        }

        GuardResult result = DestinationGuard.ValidateDestination(shortName + @"\evil", Array.Empty<string>());
        Assert.False(result.Ok);
        Assert.Contains("系统目录", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ForbiddenTargetRootsFollowInjectedSystemDrive()
    {
        // 系统盘不是 C: 的机器上，"C:\Windows" 不再是系统盘系统目录——名单必须随系统盘变化，
        // 否则改过盘符的机器上整道闸门 fail-open。
        string original = Catalog.SystemDrive;
        try
        {
            Catalog.SystemDrive = "Q:";
            GuardResult notOnSystemDrive = DestinationGuard.ValidateDestination(@"C:\Windows", Array.Empty<string>());
            Assert.True(notOnSystemDrive.Ok, notOnSystemDrive.Reason);

            GuardResult onSystemDrive = DestinationGuard.ValidateDestination(@"Q:\Windows", Array.Empty<string>());
            Assert.False(onSystemDrive.Ok);
        }
        finally
        {
            Catalog.SystemDrive = original;
        }
    }

    // ================================================================ 源/目标相对位置（第 5 条）

    [Fact]
    public void TargetInsideSourceIsRejected()
    {
        WithScratchAsValidTargetArea(sandbox =>
        {
            string source = sandbox.CreateDirectoryPath("source");
            string target = Path.Combine(source, "nested");

            GuardResult result = DestinationGuard.ValidateDestination(target, new[] { source });
            Assert.False(result.Ok);
            Assert.Contains("目标位于待转移目录内部", result.Reason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SourceInsideTargetIsRejected()
    {
        WithScratchAsValidTargetArea(sandbox =>
        {
            string target = sandbox.CreateDirectoryPath("target");
            string source = Path.Combine(target, "nested");

            GuardResult result = DestinationGuard.ValidateDestination(target, new[] { source });
            Assert.False(result.Ok);
            Assert.Contains("待转移目录位于目标内部", result.Reason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SamePhysicalLocationIsRejected()
    {
        WithScratchAsValidTargetArea(sandbox =>
        {
            string source = sandbox.CreateDirectoryPath("source");
            string link = sandbox.Combine("link");
            if (!TestEnv.CanCreateJunction || !TestEnv.TryCreateJunction(link, source))
            {
                return;
            }

            GuardResult result = DestinationGuard.ValidateDestination(link, new[] { source });
            Assert.False(result.Ok);
            Assert.Contains("实际指向同一位置", result.Reason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SamePathIsNotTreatedAsInvalidTarget()
    {
        WithScratchAsValidTargetArea(sandbox =>
        {
            string source = sandbox.CreateDirectoryPath("source");

            // 同路径由调用方按「路径相同，无需转移」处理，不属于"非法目标"。
            GuardResult result = DestinationGuard.ValidateDestination(source, new[] { source });
            Assert.True(result.Ok, result.Reason);
        });
    }

    // ================================================================ 重解析祖先链（第 6 条）

    [Fact]
    public void ReparseAncestorFindsJunctionInAncestorChain()
    {
        using var sandbox = new TempDirectory("reparse-ancestor");
        string real = sandbox.CreateDirectoryPath("real");
        string link = sandbox.Combine("link");

        if (!TestEnv.CanCreateJunction || !TestEnv.TryCreateJunction(link, real))
        {
            return;
        }

        // 目标那一级本身还不存在（工具会自己新建），但它的上级是联接点。
        string nested = Path.Combine(link, "not-yet-created", "deeper");
        Assert.Equal(link, DestinationGuard.ReparseAncestor(nested));
        Assert.Equal(string.Empty, DestinationGuard.ReparseAncestor(Path.Combine(sandbox.Root, "plain-missing")));
    }

    [Fact]
    public void TargetUnderJunctionIsRejectedByPlanningGate()
    {
        WithScratchAsValidTargetArea(sandbox =>
        {
            string real = sandbox.CreateDirectoryPath("real");
            string link = sandbox.Combine("link");
            if (!TestEnv.CanCreateJunction || !TestEnv.TryCreateJunction(link, real))
            {
                return;
            }

            string target = Path.Combine(link, "UserData");
            GuardResult result = DestinationGuard.ValidateDestination(target, Array.Empty<string>());
            Assert.False(result.Ok);
            Assert.Contains("链接/联接点", result.Reason, StringComparison.Ordinal);
        });
    }

    // ================================================================ 源位置禁区（第 7 条）

    [Fact]
    public void SourceDriveRootIsRejected()
    {
        SourceRiskResult result = DestinationGuard.CheckSourceTree(Catalog.SystemDrive + "\\");
        Assert.Equal(SourceRisk.DriveRoot, result.Risk);
        Assert.Contains("磁盘根目录", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Windows\\System32")]
    [InlineData("Program Files")]
    [InlineData("ProgramData")]
    public void SourceInsideSystemDirectoryIsRejected(string relative)
    {
        string path = Path.Combine(Catalog.SystemDrive + "\\", relative);
        SourceRiskResult result = DestinationGuard.CheckSourceTree(path);
        Assert.Equal(SourceRisk.SystemDirectory, result.Risk);
    }

    [Fact]
    public void SourceEqualToUsersIsRejectedButChildrenAreAllowed()
    {
        // \Users 只做精确匹配：用户把资料目录放在 C:\Users\me\MyDocs 是完全合法的状态。
        SourceRiskResult exact = DestinationGuard.CheckSourceTree(Catalog.SystemDrive + "\\Users");
        Assert.Equal(SourceRisk.SystemDirectory, exact.Risk);
        Assert.Contains("就是系统目录", exact.Reason, StringComparison.Ordinal);

        SourceRiskResult child = DestinationGuard.CheckSourceTree(
            Path.Combine(Catalog.SystemDrive + "\\", "Users", "someone", "MyDocs"));
        Assert.Equal(SourceRisk.None, child.Risk);
        Assert.Equal(string.Empty, child.Reason);
    }

    [Fact]
    public void ExtendedPrefixDoesNotBypassSourceGuard()
    {
        string path = @"\\?\" + Catalog.SystemDrive + @"\Windows";
        SourceRiskResult result = DestinationGuard.CheckSourceTree(path);
        Assert.Equal(SourceRisk.SystemDirectory, result.Risk);
    }

    [Fact]
    public void EmptySourceIsRiskFree()
    {
        SourceRiskResult result = DestinationGuard.CheckSourceTree(string.Empty);
        Assert.Equal(SourceRisk.None, result.Risk);
    }

    // ================================================================ 恢复路径专用

    [Fact]
    public void CheckRestoreSource_RejectsSystemDirectoriesAndDriveRoot()
    {
        GuardResult windows = DestinationGuard.CheckRestoreSource(
            Catalog.SystemDrive + "\\Windows",
            Path.Combine(Catalog.SystemDrive + "\\", "Users", "someone", "Documents"));
        Assert.False(windows.Ok);
        Assert.Contains("不能搬回", windows.Reason, StringComparison.Ordinal);

        GuardResult root = DestinationGuard.CheckRestoreSource(
            Catalog.SystemDrive + "\\",
            Path.Combine(Catalog.SystemDrive + "\\", "Users", "someone", "Documents"));
        Assert.False(root.Ok);
        Assert.Contains("不能整体搬回系统默认位置", root.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckRestoreSource_RejectsMutualAncestorRelation()
    {
        using var sandbox = new TempDirectory("restore-guard");
        string source = sandbox.CreateDirectoryPath("source");
        string nested = Path.Combine(source, "nested");

        GuardResult targetInsideSource = DestinationGuard.CheckRestoreSource(source, nested);
        Assert.False(targetInsideSource.Ok);
        Assert.Contains("自我嵌套复制", targetInsideSource.Reason, StringComparison.Ordinal);

        GuardResult sourceInsideTarget = DestinationGuard.CheckRestoreSource(nested, source);
        Assert.False(sourceInsideTarget.Ok);
        Assert.Contains("连带删除默认位置", sourceInsideTarget.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckRestoreSource_AllowsOrdinaryUserDirectory()
    {
        using var sandbox = new TempDirectory("restore-ok");
        string source = sandbox.CreateDirectoryPath("source");
        string defaultTarget = sandbox.CreateDirectoryPath("Documents");

        GuardResult result = DestinationGuard.CheckRestoreSource(source, defaultTarget);
        Assert.True(result.Ok, result.Reason);
    }

    // ================================================================ 辅助

    /// <summary>
    /// 在"临时目录可以被当作合法目标"的前提下执行断言。
    /// </summary>
    /// <remarks>
    /// 临时目录位于 <c>C:\Users\…\Temp</c>，在真实系统盘下属于目标禁区（<c>\Users</c>）。
    /// 故把系统盘临时改成一个不存在的盘符，让禁区名单离开临时目录所在盘——这正是
    /// <see cref="Catalog.SystemDrive"/> 提供 internal setter 的用途。
    /// </remarks>
    private static void WithScratchAsValidTargetArea(Action<TempDirectory> action)
    {
        string original = Catalog.SystemDrive;
        using var sandbox = new TempDirectory("guard");
        try
        {
            Catalog.SystemDrive = "Q:";
            action(sandbox);
        }
        finally
        {
            Catalog.SystemDrive = original;
        }
    }

    private static string? FindUnusedDriveLetter()
    {
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            string root = letter + ":\\";
            if (!Directory.Exists(root))
            {
                return letter.ToString();
            }
        }

        return null;
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
