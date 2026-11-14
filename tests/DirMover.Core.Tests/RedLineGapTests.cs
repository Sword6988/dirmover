using System.Text;
using DirMover.Core.Interop;
using DirMover.Core.IO;
using Xunit.Abstractions;

namespace DirMover.Core.Tests;

/// <summary>
/// 独立验证补测（IO / 禁区判定层）：补上原用例里"有名无实"或缺失的断言。
/// </summary>
/// <remarks>
/// 三类缺口：
/// ① 红线 4 只测了"源新增文件"，没测"源少了一个清单内文件"——后者是删源后丢失文件的
///    同一枚硬币的另一面（清单里有、源侧没了，说明复制清单与源已经对不上）。
/// ② 校验维度 ②（大小）从来没有被单独打红过：原用例删文件打的是维度 ①，
///    改内容打的是 ③/④，维度 ② 的断言实际上没被执行过。
/// ③ 红线 8 的"8.3 短名"只验过目标侧，源侧（<c>CheckSourceTree</c>）的短名还原没有用例。
/// <para>
/// 本文件里依赖宿主能力的用例（8.3 短名、长路径）在环境不具备时只能 <c>return</c>
/// ——xunit 2.9.2 没有 <c>Assert.Skip</c>（v3 才有）。为了不让"绿但没跑"混过去，
/// 那些分支都会写一行 <c>[ENV-MISSING]</c> 说明。
/// <b>判据：用 <c>--logger "console;verbosity=detailed"</c> 跑并 grep <c>[ENV-MISSING]</c>，
/// 命中即"没跑过"、不许记通过</b>（默认 verbosity 下该输出不可见，实测 0 命中）。
/// 真跑过时对应分支会写 <c>[OK]</c> 一行，便于反向确认。
/// </para>
/// </remarks>
public sealed class RedLineGapTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>构造。</summary>
    /// <param name="output">xunit 测试输出（用于留"环境不具备、本次未验证"的痕迹）。</param>
    public RedLineGapTests(ITestOutputHelper output) => _output = output;
    /// <summary>
    /// 红线 4：复制完成后源侧少了一个清单内文件 → 校验必须失败。
    /// </summary>
    /// <remarks>
    /// 危险形态：清单是在复制前枚举的，若校验只比"目标 vs 清单"而不重新清点源，
    /// 源侧消失的文件会被判为"目标里已经有了"，随后删源——丢的就是它。
    /// </remarks>
    [Fact]
    public void RedLine4_SourceLostManifestFile_FailsVerification()
    {
        using var sandbox = new TempDirectory("gap-src-lost");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "keep.txt"), "keep");
        string doomed = sandbox.CreateFile(Path.Combine("src", "doomed.txt"), "doomed");
        string dst = sandbox.Combine("dst");

        CopyResult copy = FsOps.CopyMerge(src, dst);
        Assert.True(copy.Ok);

        File.Delete(doomed);

        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.False(verify.Verified);
        Assert.False(verify.Cancelled);
        Assert.Contains("少了", verify.Detail, StringComparison.Ordinal);
        Assert.Contains("doomed.txt", verify.Detail, StringComparison.Ordinal);

        // 校验不删除任何东西：源侧剩下的文件与目标副本都还在。
        Assert.True(File.Exists(Path.Combine(src, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "doomed.txt")));
    }

    /// <summary>
    /// 红线 3：目标文件大小与源不一致（尾部被追加）→ 校验失败（维度 ②）。
    /// </summary>
    /// <remarks>
    /// 维度 ② 的意义在于"摘要算法出错时还有一道独立的字节数比对"。只验 ①/③ 会让
    /// "大小比对被误删"这种回归无人发现。
    /// </remarks>
    [Fact]
    public void RedLine3_TargetFileGrownAfterCopy_FailsOnSizeDimension()
    {
        using var sandbox = new TempDirectory("gap-size");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = sandbox.Combine("dst");

        CopyResult copy = FsOps.CopyMerge(src, dst);
        Assert.True(copy.Ok);

        File.AppendAllText(Path.Combine(dst, "a.txt"), "-extra-bytes-never-in-source");

        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.False(verify.Verified);
        Assert.Contains("大小不一致", verify.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 红线 1：目标文件<b>大小与修改时间都对得上、内容不同</b> → 必须靠摘要维度打红。
    /// </summary>
    /// <remarks>
    /// 这是原用例集里唯一没被打红过的校验维度：既有用例要么删文件（维度 ①）、
    /// 要么改源大小（维度 ④ 顺带命中），摘要比对（维度 ③）从来没有成为失败原因。
    /// 而"摘要属于源、不属于目标"正是原项目的 P0 级历史缺陷——若校验只比目标自己的
    /// size + mtime，复制过程中写坏的内容会被判为通过，随后删源即永久丢失。
    /// </remarks>
    [Fact]
    public void RedLine1_SameSizeAndMtimeDifferentContent_FailsOnDigestDimension()
    {
        using var sandbox = new TempDirectory("gap-digest");
        string src = sandbox.CreateDirectoryPath("src");
        string sourceFile = sandbox.CreateFile(Path.Combine("src", "a.bin"), "AAAAA");
        string dst = sandbox.Combine("dst");

        CopyResult copy = FsOps.CopyMerge(src, dst);
        Assert.True(copy.Ok);

        // 目标侧篡改：长度相同、修改时间也保持原样，只有内容不同。
        string targetFile = Path.Combine(dst, "a.bin");
        File.WriteAllText(targetFile, "BBBBB");
        File.SetLastWriteTimeUtc(targetFile, File.GetLastWriteTimeUtc(sourceFile));

        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.False(verify.Verified);
        Assert.Contains("摘要不一致", verify.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("大小不一致", verify.Detail, StringComparison.Ordinal);

        // 源侧一个字节都没被动过（校验本身绝不写任何东西）。
        Assert.Equal("AAAAA", File.ReadAllText(sourceFile));
    }

    /// <summary>
    /// 红线 1／3 的<b>长路径形态</b>：真实超过 <c>MAX_PATH</c> 的目录树必须能被复制并校验通过。
    /// </summary>
    /// <remarks>
    /// 原项目的 P0 级历史缺陷正是"源明明在，却因裸 <c>Directory.Exists</c> 在 &gt;MAX_PATH 时返回
    /// <c>false</c> 而被判为'当前位置不存在'"。既有用例只用字符串验证过
    /// <see cref="PathTools.ToExtended"/> 的拼接，没有任何一条真的去读写超长路径——
    /// 于是"前缀只在调 Win32 时才加"这条约定在端到端上是空的。
    /// </remarks>
    [Fact]
    public void RedLine1_LongPathTree_IsCopiedVerifiedAndSourceKept()
    {
        using var sandbox = new TempDirectory("gap-longpath");
        string src = sandbox.CreateDirectoryPath("src");
        string deepDir = src;
        try
        {
            while (deepDir.Length < 320)
            {
                deepDir = Path.Combine(deepDir, "a-very-long-directory-name-segment");
                Directory.CreateDirectory(deepDir);
            }

            File.WriteAllText(PathTools.ToExtended(Path.Combine(deepDir, "deep.txt")), "deep-content");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PathTooLongException
            or NotSupportedException)
        {
            // 宿主卷不支持长路径：属环境限制，不把环境问题判成功能缺陷。
            _output.WriteLine($"[ENV-MISSING] 长路径用例未执行：本机无法创建 >260 字符路径（{ex.GetType().Name}），不计为通过");
            return;
        }

        string deepFile = Path.Combine(deepDir, "deep.txt");
        Assert.True(deepFile.Length > 260, "用例前提：路径长度必须超过 MAX_PATH");
        Assert.True(PathTools.Exists(deepFile), "长路径下的存在性判断必须为真（历史缺陷形态）");

        string dst = sandbox.Combine("dst");
        CopyResult copy = FsOps.CopyMerge(src, dst);
        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.True(copy.Ok, string.Join("；", copy.Errors));
        Assert.True(verify.Verified, verify.Detail);
        Assert.Equal("deep-content", File.ReadAllText(PathTools.ToExtended(deepFile)));
    }

    /// <summary>
    /// 红线 2：同名不同内容 → 两份都在、记入冲突，且校验仍以源集合为准通过。
    /// </summary>
    /// <remarks>
    /// 第三半（"且校验通过"）是新增断言：只验"两份都在"不够——若校验因多出的
    /// <c>a (1).txt</c> 而恒失败，这条路径就永远走不完（功能缺陷），而若校验把
    /// <c>a.txt</c>（目标原文件）当成源副本，删源就会丢掉源内容（数据缺陷）。
    /// </remarks>
    [Fact]
    public void RedLine2_ConflictKeepsBothCopiesAndStillVerifies()
    {
        using var sandbox = new TempDirectory("gap-conflict");
        string src = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(Path.Combine("src", "a.txt"), "source-content");
        string dst = sandbox.CreateDirectoryPath("dst");
        File.WriteAllText(Path.Combine(dst, "a.txt"), "target-content");

        CopyResult copy = FsOps.CopyMerge(src, dst);
        VerifyResult verify = FsOps.VerifyCopy(src, dst, copy);

        Assert.True(copy.Ok);
        Assert.Single(copy.Conflicts);
        Assert.Contains("a (1).txt", copy.Conflicts[0], StringComparison.Ordinal);
        Assert.True(verify.Verified, verify.Detail);

        Assert.Equal("target-content", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.Equal("source-content", File.ReadAllText(Path.Combine(dst, "a (1).txt")));

        // IO 层永不删除源（删源只能由引擎在两道闸门之后决定）。
        Assert.Equal("source-content", File.ReadAllText(Path.Combine(src, "a.txt")));
    }

    /// <summary>
    /// 红线 8：源位置用 8.3 短名（<c>C:\PROGRA~1</c>）写成系统目录 → 必须同样拒绝。
    /// </summary>
    /// <remarks>
    /// 原用例只验了目标侧的短名绕过；源侧走的是另一段代码（<c>CheckSourceTree</c> 的
    /// 短名还原），不验就等于把闸门的一半留在暗处。
    /// </remarks>
    [Fact]
    public void RedLine8_ShortNameSourceDoesNotBypassSystemDirectoryGuard()
    {
        string programFiles = Path.Combine(Catalog.SystemDrive + "\\", "Program Files");
        if (!Directory.Exists(programFiles))
        {
            _output.WriteLine($"[ENV-MISSING] 8.3 短名用例未执行：{programFiles} 不存在（环境不具备，不计为通过）");
            return;
        }

        string? shortName = TryGetShortName(programFiles);
        if (shortName is null)
        {
            // 该卷未生成 8.3 名（或已关闭短名）：无法构造短名形态，属环境限制。
            _output.WriteLine(
                $"[ENV-MISSING] 8.3 短名用例未执行：本机未为 {programFiles} 生成 8.3 短名"
                + "（fsutil 8dot3name 可能已禁用，不计为通过）");
            return;
        }

        _output.WriteLine($"[OK] 实际验证的短名形态：{shortName}（长名 {programFiles}）");

        SourceRiskResult byShortName = DestinationGuard.CheckSourceTree(shortName);
        SourceRiskResult byLongName = DestinationGuard.CheckSourceTree(programFiles);

        Assert.Equal(SourceRisk.SystemDirectory, byShortName.Risk);
        Assert.Equal(SourceRisk.SystemDirectory, byLongName.Risk);
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
