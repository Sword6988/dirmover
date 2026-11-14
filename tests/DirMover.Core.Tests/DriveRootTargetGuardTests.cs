using DirMover.Core.IO;

namespace DirMover.Core.Tests;

/// <summary>
/// 红线 8：<b>盘根作为目标</b>必须被拒绝，且 <c>\\?\</c> 形态既不得绕过、也不得被误拒。
/// </summary>
/// <remarks>
/// 这条红线原先有一个真实缺口：<c>ValidateDestination(@"C:\")</c> 曾返回 <c>Ok = true</c> ——
/// 只判了"盘符是否存在"，而 <c>InvalidSegment</c> 又把盘根从待检查串里切掉，于是没有任何片段可查。
/// Core 侧已修复，本文件因此从"缺口记录"转为<b>回归用例</b>。
/// <para>
/// 顺带钉住修复暴露出的两个副作用：
/// ① <c>\\?\</c> 形态的拒绝理由必须落在"盘根 / 系统目录"上，而不是碰巧命中"非法字符"
/// ——旧实现里 <c>FirstIllegalCharacter</c> 会先撞到盘符那个冒号（<c>candidate[1]</c> 不是 <c>':'</c>，
/// 扫描从 0 开始），使"剥前缀比禁区"那段代码对 <c>\\?\</c> 输入永远不可达；
/// ② 合法的 <c>\\?\</c> 目标不得被误拒——只修"该拒的拒"很容易顺手把"该放的"也拒掉。
/// </para>
/// <para>
/// 盘根用例统一用<b>当前系统盘</b>而不是写死 <c>D:</c>：断言不该依赖某个具体盘符在这台机器上存在
/// （本机确实有 C:/D:/E: 三个固定盘，但换机器就没有）。非系统盘那条单独用条件守卫。
/// </para>
/// </remarks>
public sealed class DriveRootTargetGuardTests
{
    /// <summary>红线 8：裸盘根作为目标 → 拒绝，且理由必须是"盘根"。</summary>
    [Fact]
    public void DriveRootAsTarget_IsRejected()
    {
        GuardResult result = DestinationGuard.ValidateDestination(
            Catalog.SystemDrive + "\\",
            Array.Empty<string>());

        Assert.False(result.Ok);
        Assert.Contains("盘根", result.Reason, StringComparison.Ordinal);
        Assert.Contains("不能作为转移目标", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>红线 8：<c>\\?\</c> 前缀的盘根同样被拒，理由仍必须是"盘根"。</summary>
    [Fact]
    public void ExtendedPrefixDriveRoot_IsRejectedForDriveRootReason()
    {
        GuardResult result = DestinationGuard.ValidateDestination(
            @"\\?\" + Catalog.SystemDrive + "\\",
            Array.Empty<string>());

        Assert.False(result.Ok);
        Assert.Contains("盘根", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// 红线 8：<c>\\?\</c> 前缀的系统目录被拒，且<b>理由必须是"系统目录"</b>而不是"非法字符"。
    /// </summary>
    /// <remarks>
    /// "不合法但因为别的原因被拒"是这个项目里最典型的假通过形态：旧实现正是靠
    /// "<c>?</c> 属于非法字符"才让这条用例绿着，而真正想验的禁区判定一次都没执行过。
    /// </remarks>
    [Fact]
    public void ExtendedPrefixSystemDirectory_IsRejectedForSystemDirectoryReason()
    {
        GuardResult result = DestinationGuard.ValidateDestination(
            @"\\?\" + Catalog.SystemDrive + @"\Windows",
            Array.Empty<string>());

        Assert.False(result.Ok);
        Assert.Contains("系统目录", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("非法字符", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>红线 8 的反向对照：合法的 <c>\\?\</c> 目标不得被误拒。</summary>
    [Fact]
    public void ExtendedPrefixOrdinaryTarget_IsNotFalselyRejected()
    {
        string target = @"\\?\" + Catalog.SystemDrive + @"\UserData\me\dest";

        GuardResult result = DestinationGuard.ValidateDestination(target, Array.Empty<string>());

        Assert.True(result.Ok, result.Reason);
    }

    /// <summary>对照：裸写的系统目录仍被拒（修复不得只覆盖 <c>\\?\</c> 形态）。</summary>
    [Fact]
    public void BareSystemDirectory_IsStillRejected()
    {
        GuardResult result = DestinationGuard.ValidateDestination(
            Catalog.SystemDrive + @"\Windows",
            Array.Empty<string>());

        Assert.False(result.Ok);
        Assert.Contains("系统目录", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>非系统盘的盘根同样被拒（盘根判定不得只挂在系统盘上）。</summary>
    [Fact]
    public void NonSystemDriveRoot_IsAlsoRejected()
    {
        IReadOnlyList<DriveVolume> drives = DriveInventory.ListFixedNonSystem();
        if (drives.Count == 0)
        {
            // 本机没有第二个固定盘：该形态无法构造（属环境限制，不是断言失败）。
            return;
        }

        GuardResult result = DestinationGuard.ValidateDestination(
            drives[0].Root,
            Array.Empty<string>());

        Assert.False(result.Ok);
        Assert.Contains("盘根", result.Reason, StringComparison.Ordinal);
    }
}
