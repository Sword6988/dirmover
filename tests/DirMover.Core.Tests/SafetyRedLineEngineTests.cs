using DirMover.Core.Engine;
using DirMover.Core.IO;
using DirMover.Core.Registry;
using Xunit.Abstractions;

namespace DirMover.Core.Tests;

/// <summary>
/// 独立验证补测（引擎级）：把简报 §10 的红线钉在<b>真正的执行路径</b>上。
/// </summary>
/// <remarks>
/// 为什么这些用例必须另建一份而不是复用 <c>TransferEngineTests</c>：
/// 原作者用例里的失败注入点集中在「建父目录之前」，而红线真正危险的位置在
/// <b>复制已落盘 → 校验 → 写注册表 → 删源</b> 这一段。这里用三个可控注入点覆盖它：
/// ① 源文件被独占句柄占住（复制中途失败）；② 进度回调里把目标文件删掉（校验失败）；
/// ③ 内存注册表后端的写失败钩子里顺手改源目录（删源前第二道闸门）。
/// 三个注入点都不碰真实注册表、不碰真实用户目录，可重复执行。
/// </remarks>
public sealed class SafetyRedLineEngineTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>构造（<see cref="ITestOutputHelper"/> 用于把"环境不具备"这类事实写进测试日志）。</summary>
    /// <param name="output">xunit 测试输出。</param>
    public SafetyRedLineEngineTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 红线 1：复制<b>中途</b>失败（部分文件已落盘）→ 不删源、不写注册表。
    /// </summary>
    /// <remarks>
    /// 与"建目录就失败"不同：这里第一个文件已经复制成功，第二个文件因被独占句柄占住而失败。
    /// 这是"复制未完成"的真实形态——目标侧已有部分数据，此时任何提前删源都会丢数据。
    /// </remarks>
    [Fact]
    public async Task RedLine1_MidCopyFailure_KeepsSourceAndWritesNoRegistry()
    {
        using var env = new EngineHarness("redline1-midcopy");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a-ok.txt"), "ok-content");
        string locked = env.Sandbox.CreateFile(Path.Combine("src", "b-locked.bin"), "locked-content");
        // 目标已存在 ⇒ 走安全通道（复制合并），而不是同卷原子改名。
        string dst = env.Sandbox.CreateDirectoryPath("dst");

        TransferReport report;
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = await env.Engine().ExecuteAsync(new[] { env.Item(src, dst) });
        }

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Failed, outcome.Status);
        Assert.Equal(TransferStage.CopyFailed, outcome.Stage);

        // 红线 1（第一半）：源目录与源内容一个字节都不许少。
        Assert.True(Directory.Exists(src));
        Assert.Equal("ok-content", File.ReadAllText(Path.Combine(src, "a-ok.txt")));
        Assert.Equal("locked-content", File.ReadAllText(locked));

        // 红线 1（第二半）：注册表一个值都没写。
        Assert.Empty(env.Backend.Writes);

        // 已经落盘的那部分留在目标侧：证明"复制确实开始过"，而不是根本没进复制分支。
        Assert.True(File.Exists(Path.Combine(dst, "a-ok.txt")));
    }

    /// <summary>
    /// 红线 1：复制完成但<b>校验失败</b> → 不删源、不写注册表。
    /// </summary>
    /// <remarks>
    /// 注入点在复制与校验之间：进度回调第一次上报"本项全部文件已完成"时，把目标侧的副本删掉，
    /// 于是校验维度 ① 必然失败。这样验的是"校验这道闸门真的拦住了删源与写注册表"，
    /// 而不是"复制失败时没删源"。
    /// </remarks>
    [Fact]
    public async Task RedLine1_VerifyFailure_KeepsSourceAndWritesNoRegistry()
    {
        using var env = new EngineHarness("redline1-verify");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = env.Sandbox.CreateDirectoryPath("dst");

        var tamper = new DeleteTargetAfterCopyProgress(Path.Combine(dst, "a.txt"));
        TransferReport report = await env.Engine().ExecuteAsync(new[] { env.Item(src, dst) }, tamper);

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.False(report.Cancelled);
        Assert.Equal(ItemStatus.Failed, outcome.Status);
        Assert.Equal(TransferStage.VerifyFailed, outcome.Stage);
        Assert.Contains("复制校验未通过", outcome.Message, StringComparison.Ordinal);

        Assert.True(Directory.Exists(src));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(src, "a.txt")));
        Assert.Empty(env.Backend.Writes);
    }

    /// <summary>
    /// 红线 4：删源前第二道闸门 —— 复制期间源目录里新增文件 → <b>保留源目录</b>。
    /// </summary>
    /// <remarks>
    /// 场景取"注册表已写成功之后源才被改动"：这是唯一能同时满足"数据已完整落盘"与
    /// "源目录不得删除"的形态。注册表脏了可以用快照回滚，源目录删了却无法回滚——
    /// 所以这里宁可留在 <see cref="TransferStage.RegistryWritten"/>（半完成、下次启动会提示）。
    /// </remarks>
    [Fact]
    public async Task RedLine4_SourceGainedFileAfterCopy_KeepsSourceEvenThoughRegistryWasWritten()
    {
        using var env = new EngineHarness("redline4-gate");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = env.Sandbox.CreateDirectoryPath("dst");
        string appeared = Path.Combine(src, "appeared-during-transfer.txt");

        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", @"C:\old-documents");
        TransferEngine engine = env.Engine();

        // 在"写注册表"这一刻往源目录塞进一个新文件：模拟复制→校验窗口内源被写入。
        env.Backend.WriteFailure = (subKey, name, attempt) =>
        {
            File.WriteAllText(appeared, "appeared");
            return false; // 只是借这个钩子产生副作用，写入本身要成功
        };

        TransferReport report = await engine.ExecuteAsync(new[] { env.Item(src, dst) });

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Ok, outcome.Status);
        Assert.Equal(TransferStage.RegistryWritten, outcome.Stage);
        Assert.Contains("源目录", outcome.Message, StringComparison.Ordinal);

        // 红线 4 核心：源目录连同它的全部内容（含新出现的那个文件）都还在。
        Assert.True(Directory.Exists(src));
        Assert.True(File.Exists(appeared));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(src, "a.txt")));

        // 数据同时也完整存在于目标侧（两份都在，不丢）。
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.NotEmpty(env.Backend.Writes);
    }

    /// <summary>
    /// 红线 5：写注册表失败 → 本项已写入的值就地回滚，源目录保留（复制通道）。
    /// </summary>
    /// <remarks>
    /// 复制通道与 rename 通道的收尾方式不同：rename 通道要把目录改回原位，复制通道则是
    /// "源根本还没删"。同一条红线在两条通道上都要被验过。
    /// </remarks>
    [Fact]
    public async Task RedLine5_RegistryFailureOnCopyPath_RollsBackAndKeepsSource()
    {
        using var env = new EngineHarness("redline5-copy");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = env.Sandbox.CreateDirectoryPath("dst");

        const string aliasName = "{F42EE2D3-909F-4907-8871-4C22FC0BF756}";
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", @"C:\old-documents");

        var item = new PlanItem
        {
            Spec = Catalog.ByKey("documents")!,
            Current = src,
            Target = dst,
            RegistryPayload = dst,
            RegistryTargets = new[]
            {
                new RegistryTarget(Catalog.UserShellFoldersSubKey, "Personal"),
                new RegistryTarget(Catalog.UserShellFoldersSubKey, aliasName),
            },
        };

        TransferEngine engine = env.Engine();
        // 第二个值（GUID 别名）写失败：典型形态是"权威值已改、别名没改"的错乱。
        env.Backend.WriteFailure = (subKey, name, attempt) =>
            string.Equals(name, aliasName, StringComparison.Ordinal);

        TransferReport report = await engine.ExecuteAsync(new[] { item });

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Failed, outcome.Status);
        Assert.Equal(TransferStage.RegistryFailed, outcome.Stage);

        // 红线 5（一）：源目录保留。
        Assert.True(Directory.Exists(src));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(src, "a.txt")));

        // 红线 5（二）：本项已写入的值被就地恢复成原值。
        Assert.Equal(2, env.Backend.Writes.Count);
        Assert.Equal(@"C:\old-documents", env.Backend.Writes[^1].Data);

        // 快照里"原本不存在"的别名必须被删掉，不能凭空留一个指向新位置的别名。
        (string SubKey, string Name) deleted = Assert.Single(env.Backend.Deletes);
        Assert.Equal(aliasName, deleted.Name);

        // 目标副本完整（数据不丢）。
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "a.txt")));
    }

    /// <summary>
    /// 红线 6：可用空间<b>读不到 → 阻断该项</b>；其余项照常执行。
    /// </summary>
    /// <remarks>
    /// 构造方式：目标落在一个不存在的盘符上 → <see cref="PathTools.FreeSpace"/> 返回
    /// <c>null</c>（"未知"）。绝不能用 0 代替未知：那会让一次探测失败变成"无需检查"，
    /// 一路复制到磁盘写满。
    /// </remarks>
    [Fact]
    public async Task RedLine6_UnknownFreeSpace_BlocksOnlyThatItem()
    {
        string? freeLetter = FindUnusedDriveLetter();
        if (freeLetter is null)
        {
            // 26 个盘符全被占用：无法构造"盘不存在 ⇒ 空间未知"，属环境限制。
            // 判据同跨卷用例：输出里出现 [ENV-MISSING] 就说明本条没跑过，不许记通过。
            _output.WriteLine(
                "[ENV-MISSING] 空间未知用例未执行：26 个盘符全部存在，无法构造\"目标盘不存在\"（环境不具备，不计为通过）");
            return;
        }

        using var env = new EngineHarness("redline6-unknown");
        string blockedSource = env.Sandbox.CreateDirectoryPath("blocked-src");
        env.Sandbox.CreateFile(Path.Combine("blocked-src", "a.txt"), "x");
        string blockedTarget = freeLetter + @":\UserData\Documents";
        PlanItem blocked = env.Item(blockedSource, blockedTarget);

        string okSource = env.Sandbox.CreateDirectoryPath("ok-src");
        env.Sandbox.CreateFile(Path.Combine("ok-src", "b.txt"), "y");
        PlanItem normal = env.Item(okSource, env.Sandbox.Combine("dst", "Documents"));

        TransferReport report = await env.Engine().ExecuteAsync(new[] { blocked, normal });

        Assert.Equal(2, report.Outcomes.Count);
        Assert.Equal(ItemStatus.Failed, report.Outcomes[0].Status);
        Assert.Contains("无法读取目标磁盘可用空间", report.Outcomes[0].Message, StringComparison.Ordinal);

        // 被阻断的项：源目录不动、注册表不写。
        Assert.True(Directory.Exists(blockedSource));
        Assert.Equal("x", File.ReadAllText(Path.Combine(blockedSource, "a.txt")));
        Assert.DoesNotContain(
            env.Backend.Writes,
            write => string.Equals(write.Data as string, blockedTarget, StringComparison.Ordinal));

        // 只阻断该项：第二项照常完成。
        Assert.Equal(ItemStatus.Ok, report.Outcomes[1].Status);
    }

    /// <summary>
    /// 红线 7：目标位于源之内 → 计划期即判为不可执行，执行期一次文件操作都不发生。
    /// </summary>
    /// <remarks>
    /// 这里验的是"闸门被接进 <see cref="TransferEngine.BuildPlan"/> 并真的挡住了执行"，
    /// 而不只是 <see cref="DestinationGuard"/> 单函数返回值正确——后者对了但没被调用，等于没有。
    /// </remarks>
    [Fact]
    public async Task RedLine7_TargetInsideSource_IsBlockedBeforeAnyFileOperation()
    {
        using var env = new EngineHarness("redline7-inside");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", src);

        // 临时目录位于 C:\Users\… 之下，本身就在"系统目录"禁区里；把系统盘挪开才能验到
        // "目标在源内"这条（否则会先被系统目录判定拦下，用例就变成了重复验别的规则）。
        PlanItem item = WithInjectedSystemDrive("Q:", () =>
        {
            IReadOnlyList<PlanItem> plan = env.Engine().BuildPlan(
                new[] { "documents" },
                destinationRoot: src,
                createUserDirectory: false,
                userName: "tester",
                scan: false);
            return Assert.Single(plan);
        });

        Assert.Contains("待转移目录内部", item.InvalidReason, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, item.BlockingReason());

        TransferReport report = await env.Engine().ExecuteAsync(new[] { item });

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Failed, outcome.Status);
        Assert.True(Directory.Exists(src));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(src, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(src, "src")));
        Assert.Empty(env.Backend.Writes);
    }

    /// <summary>
    /// 红线 8：注册表被改成系统目录（<c>&lt;系统盘&gt;\Windows</c>）当源 → 计划期即拒绝。
    /// </summary>
    /// <remarks>
    /// 注册表是外部可写数据，被改成系统目录时若不拒绝，后果是"把系统目录整棵搬走再删掉"。
    /// <para>
    /// 构造说明：临时目录位于 <c>C:\Users\…</c> 之下，本身就是目标禁区，会先于"源禁区"命中，
    /// 于是这条用例验不到源侧闸门。因此把系统盘注入成 <c>Q:</c>（禁区名单是<b>按当前系统盘现算</b>的，
    /// 这正是要一并验证的性质），源写成 <c>Q:\Windows</c>。源侧判定是纯字符串包含关系，
    /// 不依赖该路径真实存在，所以本用例不会碰任何真实系统目录。
    /// </para>
    /// <para>
    /// 本用例<b>只验到计划期</b>（<c>InvalidReason</c> 非空）：刻意不执行，避免在闸门万一
    /// 失效时真的对一个系统目录发起复制。
    /// </para>
    /// </remarks>
    [Fact]
    public void RedLine8_SystemDirectoryAsSource_IsBlockedAtPlanning()
    {
        using var env = new EngineHarness("redline8-source");
        const string injectedDrive = "Q:";
        string systemTree = injectedDrive + @"\Windows";
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", systemTree);

        (PlanItem item, SourceRisk cWindowsRisk) = WithInjectedSystemDrive(injectedDrive, () =>
        {
            IReadOnlyList<PlanItem> plan = env.Engine().BuildPlan(
                new[] { "documents" },
                destinationRoot: env.Sandbox.Combine("dest"),
                createUserDirectory: false,
                userName: "tester",
                scan: false);
            return (Assert.Single(plan), DestinationGuard.CheckSourceTree(@"C:\Windows").Risk);
        });

        Assert.Contains("系统目录", item.InvalidReason, StringComparison.Ordinal);
        Assert.Contains(systemTree, item.InvalidReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(string.Empty, item.BlockingReason());
        Assert.Empty(env.Backend.Writes);

        // 禁区名单随系统盘变化：同一注入下 C:\Windows 不再被判为系统目录（否则说明名单写死在 C:）。
        Assert.Equal(SourceRisk.None, cWindowsRisk);
    }

    /// <summary>
    /// 红线 2（引擎级）：目标已有同名不同内容的文件 → 两份都保留，校验通过后源才被删除。
    /// </summary>
    /// <remarks>
    /// IO 层已经证明"两份都在"。这里补的是删源那一端：校验必须按<b>目标实际落点</b>
    /// （<c>a (1).txt</c>）逐项比对，否则要么恒失败（搬家做不完），要么错误放行（丢数据）。
    /// </remarks>
    [Fact]
    public async Task RedLine2_ConflictThenVerifyPasses_BothCopiesSurviveSourceRemoval()
    {
        using var env = new EngineHarness("redline2-conflict");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "source-content");
        string dst = env.Sandbox.CreateDirectoryPath("dst");
        File.WriteAllText(Path.Combine(dst, "a.txt"), "target-content");

        TransferReport report = await env.Engine().ExecuteAsync(new[] { env.Item(src, dst) });

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Ok, outcome.Status);
        Assert.Equal(TransferStage.SourceRemoved, outcome.Stage);

        // 红线 2 核心：删源之后两份内容都还在（谁都没被覆盖）。
        Assert.False(Directory.Exists(src));
        Assert.Equal("target-content", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.Equal("source-content", File.ReadAllText(Path.Combine(dst, "a (1).txt")));
        Assert.Single(env.Backend.Writes);
    }

    /// <summary>
    /// 红线 5/10（试运行）：<c>dryRun</c> 下<b>备份目录与日志目录本身都不得被创建</b>。
    /// </summary>
    /// <remarks>
    /// 只断言"注册表写入次数为 0"会漏掉最危险的那类副作用：快照与意图日志是<b>文件</b>副作用，
    /// 它们的父目录（<c>backups\</c>）一旦被创建，说明代码走了"先建目录再判断是否试运行"的顺序——
    /// 那正是"试运行被骗成真跑"的形态。因此这里直接盯目录是否存在，而不是盯目录里有没有文件。
    /// </remarks>
    [Fact]
    public async Task RedLine10_DryRun_CreatesNeitherBackupNorLogDirectory()
    {
        using var env = new EngineHarness("redline10-dryrun");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = env.Sandbox.Combine("dst", "Documents");

        // 这个路径在运行前<b>不存在</b>：只要引擎碰了它，目录就会被建出来。
        string backupDir = env.Sandbox.Combine("backups-must-not-exist");
        var engine = new TransferEngine(
            new RegistryStore(env.Backend, env.Log),
            backupDir,
            dryRun: true,
            env.Log);

        TransferReport report = await engine.ExecuteAsync(new[] { env.Item(src, dst) });

        Assert.True(report.Ok);
        Assert.Null(report.SnapshotPath);
        Assert.Empty(env.Backend.Writes);

        Assert.False(Directory.Exists(backupDir), "试运行不得创建备份目录（快照与意图日志都落在这里）");
        Assert.True(Directory.Exists(src));
        Assert.False(Directory.Exists(dst));

        // 整个沙箱里不得出现任何快照 / 意图日志 / 日志文件 / .part 残留。
        // 注意：这里刻意<b>不</b>断言 env.Paths.LogDirectory 不存在——那由 TempAppPaths 的构造函数
        // 预先建好，断言它等于在测测试夹具而不是测引擎（Core 自身不写日志文件，日志由 App 的
        // FileLogger 负责）。所以要验的是"引擎没产生日志产物"，而不是"夹具没建目录"。
        foreach (string pattern in new[] { "intent.json", "intent.json.tmp", "registry-*.json", "*.part", "DirMover-*.log", "*.log" })
        {
            Assert.Empty(Directory.GetFileSystemEntries(env.Sandbox.Root, pattern, SearchOption.AllDirectories));
        }
    }

    /// <summary>
    /// 红线 10 / T11⑧⑨：`--scan` 第四段「读取告警」在 <c>Warnings</c> 非空时必须真的渲染出来。
    /// </summary>
    /// <remarks>
    /// 原用例只断言段标题「四、读取告警」出现在文本里——只要有标题就算过，
    /// 于是"告警列表根本没被输出"这种回归不会红。这里同时验两个分支：
    /// ① 有告警 → 逐条渲染（带 <c>·</c> 前缀）；② 无告警 → 输出单独一行的「无」。
    /// 并且告警要从<b>真实的读失败</b>里来，不是手工塞字符串。
    /// </remarks>
    [Fact]
    public void RedLine10_ScanReport_RendersWarningsFromRealReadFailures()
    {
        using var env = new EngineHarness("redline10-scanwarn");

        // ① 真实读失败（读值 + 枚举都失败）→ 两段告警都必须出现在文本里。
        var failing = new FailingRegistryBackend();
        var engine = new TransferEngine(new RegistryStore(failing, env.Log), backupDir: null, dryRun: true, env.Log);
        var config = new RunConfig
        {
            DestinationRoot = env.Sandbox.Combine("dest"),
            CreateUserDirectory = false,
        };

        ScanReportResult result = ScanReport.BuildResult(engine, config, Array.Empty<DriveVolume>());
        Assert.NotEmpty(result.Warnings);

        string text = result.ToText();
        Assert.Contains("四、读取告警", text, StringComparison.Ordinal);
        Assert.Contains("读取注册表失败", text, StringComparison.Ordinal);
        Assert.Contains("枚举注册表失败", text, StringComparison.Ordinal);
        Assert.Contains("· ", text, StringComparison.Ordinal);

        // ② 无告警时不能出现"看起来有告警"的残留，且必须给出显式「无」。
        var quiet = new ScanReportResult { Warnings = Array.Empty<string>() };
        string quietText = quiet.ToText();
        string[] quietLines = quietText.Split(Environment.NewLine);
        Assert.Contains(quietLines, line => string.Equals(line.Trim(), "无", StringComparison.Ordinal));
        Assert.False(quietText.Contains("· ", StringComparison.Ordinal), "无告警时不得渲染出告警条目");
    }

    /// <summary>
    /// 红线 1／3 的<b>跨卷端到端形态</b>：源在 C:、目标在另一块固定盘时，同卷原子改名必然失败，
    /// 必须完整走"复制 → 7 维校验 → 写注册表 → 删源"，且空目录也要跟着过去。
    /// </summary>
    /// <remarks>
    /// 这是整个验证里最值得有的一条：引擎其它用例的源与目标都在 <c>%TEMP%</c> 同一卷，
    /// 跨卷分支（<c>TryAtomicMove</c> 返回 <c>Moved=false</c> → <c>CopyMerge</c> → <c>VerifyCopy</c>
    /// → 在<b>另一块盘</b>上删源）无法被它们覆盖。
    /// <para>
    /// 环境不具备时的处理（xunit 2.9 没有 <c>Assert.Skip</c>，那是 v3 的 API，已实测编译不通过）：
    /// ① 本机<b>没有</b>非系统固定盘 → 记录一条 <c>[ENV-MISSING]</c> 输出后返回（这是"环境不具备"，不是通过）；
    /// ② 有盘但写不进去 → <b>直接失败</b>。因为"有盘却写不进"不是环境限制，是必须让人看见的事实。
    /// </para>
    /// <para>
    /// <b>判据（换机器时必须照做）</b>：执行输出里出现 <c>[ENV-MISSING]</c> 即"未跑过"，不得记作通过。
    /// <b>命令</b>：
    /// <code>
    /// dotnet test tests\DirMover.Core.Tests -p:Platform=x64 --logger "console;verbosity=detailed"
    /// </code>
    /// 然后在输出里检索 <c>[ENV-MISSING]</c> —— 命中即"没跑过"，未命中才是真跑。
    /// 默认 verbosity 下该输出不可见（实测 0 命中），所以<b>看绿点不算证据</b>。
    /// （本机不加 <c>-p:Platform=x64</c> 也可，两种跑法都验过。）
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RedLine1_CrossVolume_EndToEnd_KeepsDataAndRemovesSource()
    {
        IReadOnlyList<DriveVolume> drives = DriveInventory.ListFixedNonSystem();
        string? otherRoot = FindWritableRoot(drives);
        if (otherRoot is null && drives.Count == 0)
        {
            // 真正的"环境不具备"：没有第二块固定盘，跨卷路径物理上无法构造。
            // xunit 2.9 没有 Assert.Skip（v3 才有，实测编译不通过），只能在输出里留痕。
            // 判据：输出里出现 [ENV-MISSING] 就说明本条没跑过，不许记通过；
            // 该输出需 --logger "console;verbosity=detailed" 才可见（默认 verbosity 实测 0 命中）。
            _output.WriteLine("[ENV-MISSING] 跨卷用例未执行：本机无非系统固定盘（环境不具备，不计为通过）");
            return;
        }

        if (otherRoot is null)
        {
            // 有盘却写不进去：这不是环境限制，是必须让人看见的事实。
            Assert.Fail(
                $"找到 {drives.Count} 个非系统固定盘但均不可写（{string.Join("、", drives.Select(d => d.Root))}）："
                + "跨卷安全通道无法验证，需人工确认");
            return;
        }

        using var env = new EngineHarness("redline1-xvol");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello-cross-volume");
        env.Sandbox.CreateFile(Path.Combine("src", "sub", "b.txt"), "nested");
        Directory.CreateDirectory(Path.Combine(src, "empty")); // 红线 3 的跨卷版

        // 测试垃圾只落在独立目录里，绝不碰产品真实落点（<盘>\UserData）。
        string staging = Path.Combine(otherRoot, "dirmover-xvol-" + Guid.NewGuid().ToString("N"));
        string dst = Path.Combine(staging, "dest");
        bool cleaned = false;
        try
        {
            // 前提：这必须是真正的跨卷，否则用例退化成同卷 rename，等于什么都没验。
            Assert.NotEqual(PathTools.VolumeKey(src), PathTools.VolumeKey(dst));

            TransferReport report = await env.Engine().ExecuteAsync(new[] { env.Item(src, dst) });

            // 红线 1：整轮成功，且这一项走完了安全通道。
            Assert.True(report.Ok);
            ItemOutcome outcome = Assert.Single(report.Outcomes);
            Assert.Equal(ItemStatus.Ok, outcome.Status);
            Assert.Equal(TransferStage.SourceRemoved, outcome.Stage);

            // 数据在另一块盘上完整：文件、子目录、空目录。
            Assert.Equal("hello-cross-volume", File.ReadAllText(Path.Combine(dst, "a.txt")));
            Assert.Equal("nested", File.ReadAllText(Path.Combine(dst, "sub", "b.txt")));
            Assert.True(Directory.Exists(Path.Combine(dst, "empty")));

            // 跨卷路径上最难的一步：删源发生在源所在的那块盘上。
            Assert.False(Directory.Exists(src));

            // 与单卷 rename 通道不同：这里注册表必须真的写过。
            Assert.Single(env.Backend.Writes);
        }
        finally
        {
            cleaned = TryDeleteTree(staging);
        }

        // 数据盘上不许留垃圾（放在 finally 之后断言，避免掩盖真正的失败原因）。
        Assert.True(cleaned, $"清理失败：{staging}");
        Assert.False(Directory.Exists(staging), $"数据盘上残留了测试目录：{staging}");
    }

    // ================================================================ 辅助

    /// <summary>在给定盘里找一个<b>可写</b>的根（写不进去的盘不当作可用目标；全都不可写返回 <c>null</c>）。</summary>
    private static string? FindWritableRoot(IReadOnlyList<DriveVolume> drives)
    {
        foreach (DriveVolume drive in drives)
        {
            string probe = Path.Combine(drive.Root, "dirmover-qa-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(probe);
                Directory.Delete(probe);
                return drive.Root;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 该盘不可写：换下一个（只读盘 / 挂载点等）。
            }
        }

        return null;
    }

    /// <summary>清理跨卷用例留下的目录；返回是否已确认删除干净。</summary>
    private static bool TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return !Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>一切读写都抛 <see cref="RegistryReadException"/> 的后端（用于逼出「读取告警」）。</summary>
    private sealed class FailingRegistryBackend : IRegistryBackend
    {
        public RegistryValue? ReadValue(string subKey, string name) =>
            throw new RegistryReadException($"注入的读取失败：{subKey}\\{name}");

        public RegistryValue? ReadHklmValue(string subKey, string name) =>
            throw new RegistryReadException($"注入的读取失败（HKLM）：{subKey}\\{name}");

        public void WriteValue(string subKey, string name, object data, Microsoft.Win32.RegistryValueKind kind) =>
            throw new RegistryReadException($"注入的写入失败：{subKey}\\{name}");

        public void DeleteValue(string subKey, string name)
        {
        }

        public IReadOnlyDictionary<string, RegistryValue> EnumerateValues(string subKey) =>
            throw new RegistryReadException($"注入的枚举失败：{subKey}");
    }

    private static T WithInjectedSystemDrive<T>(string drive, Func<T> action)
    {
        string original = Catalog.SystemDrive;
        try
        {
            Catalog.SystemDrive = drive;
            return action();
        }
        finally
        {
            Catalog.SystemDrive = original;
        }
    }

    private static string? FindUnusedDriveLetter()
    {
        string systemDrive = Catalog.SystemDrive;
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (string.Equals(letter + ":", systemDrive, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Directory.Exists(letter + ":\\"))
            {
                return letter.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// 审计 A-11：删源前复检发现目标（或其上级）变成链接/联接点 → <b>保留源目录</b>，
    /// 并按"半完成状态"记账（阶段 = <see cref="TransferStage.SourceRemovalFailed"/>、条目<b>不注销</b>）。
    /// </summary>
    /// <remarks>
    /// 真实形态是"复制 → 校验 → 写注册表"这段窗口里目标被 <c>mklink /J</c> 换掉，靠抢时序无法
    /// 确定性复现（大文件 + 后台线程 = flaky）。因此走 <see cref="DestinationGuard.Hook"/>：
    /// 复制一开始上报进度就把钩子"上膛"，于是<b>下一次</b>重解析点判定（= 删源前复检）必然命中。
    /// 断言里的 <c>Hits &gt; 0</c> 是防空转的关键——否则"钩子从未生效"也会让用例变绿。
    /// </remarks>
    [Fact]
    public async Task RedLine11_TargetBecameLinkBeforeSourceRemoval_KeepsSourceAndLeavesPendingIntent()
    {
        using var env = new EngineHarness("a11-late-link");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        // 目标已存在 ⇒ 走复制通道；rename 通道压根没有"删源"这一步，测不到本分支。
        string dst = env.Sandbox.CreateDirectoryPath("dst");
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", @"C:\old-documents");

        TransferEngine engine = env.Engine();
        using var lateLink = new TargetBecomesLinkAfterCopyProgress(dst);

        TransferReport report = await engine.ExecuteAsync(new[] { env.Item(src, dst) }, lateLink);

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Failed, outcome.Status);
        Assert.Equal(TransferStage.SourceRemovalFailed, outcome.Stage);
        Assert.Contains("保留源目录", outcome.Message, StringComparison.Ordinal);
        // 区分器：这条文案只能来自 A-11 的复检分支，而不是"删除失败"（后者是另一句话）。
        Assert.Contains("删源前复检发现", outcome.Message, StringComparison.Ordinal);

        // 注入确实生效（没有这一条，钩子失效时用例会假绿）。
        Assert.True(lateLink.Armed, "进度回调没有把钩子上膛，本用例没有真正进入 A-11 分支");
        Assert.True(lateLink.Hits > 0, "钩子从未被调用，说明删源前复检这条路没走到");

        // ① 源目录连同内容一个字节都没少。
        Assert.True(Directory.Exists(src));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(src, "a.txt")));

        // 前提也是红线的一部分：数据在目标侧同样完整，注册表已写（正是"半完成"的含义）。
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.NotEmpty(env.Backend.Writes);

        // ② + ③ 意图日志：阶段 = SourceRemovalFailed，且条目仍在（未 Finish、未被 CloseRun 清掉）。
        PendingIntents pending = IntentJournal.ReadPending(env.Paths.BackupDirectory);
        Assert.Null(pending.ReadError);
        PendingIntent entry = Assert.Single(pending.Items);
        Assert.Equal(TransferStage.SourceRemovalFailed, entry.Stage);
        Assert.Equal(src, entry.Source, ignoreCase: true);
        Assert.False(pending.Clean, "A-11 分支必须留下未完成记录，否则下次启动不会再提示");

        // 反证"未 Finish"：Finish 会删掉最后一条并删除 intent.json 本身。
        Assert.True(PathTools.Exists(Path.Combine(env.Paths.BackupDirectory, IntentJournal.FileName)));
    }

    /// <summary>
    /// 在"复制完成、校验尚未开始"的那一刻把目标副本删掉，从而必然触发校验失败。
    /// </summary>
    /// <remarks>
    /// 这是唯一可确定复现"校验不通过"的注入点：靠改源文件内容/时间戳会依赖调度，
    /// 而靠删目标文件只依赖"本项文件数已全部完成"这件事本身。
    /// </remarks>
    private sealed class DeleteTargetAfterCopyProgress : ITransferProgress
    {
        private readonly string _targetFile;
        private int _tampered;

        internal DeleteTargetAfterCopyProgress(string targetFile) => _targetFile = targetFile;

        public void Report(in TransferProgressInfo info)
        {
            if (_tampered != 0 || info.TotalInItem <= 0 || info.DoneInItem < info.TotalInItem)
            {
                return;
            }

            _tampered = 1;
            try
            {
                if (File.Exists(_targetFile))
                {
                    File.Delete(_targetFile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉就是注入失败：让断言直接红，而不是静默变成别的失败原因。
            }
        }
    }

    /// <summary>
    /// 既当进度回调、又接管 <see cref="DestinationGuard.Hook"/> 的注入器：
    /// 复制一开始上报进度就"上膛"，此后任何重解析点判定都返回命中。
    /// </summary>
    /// <remarks>
    /// 为什么用"复制已开始"而不是"第 N 次调用"来上膛：一次复制通道的项在执行期要过
    /// <b>三处</b>重解析点判定（引擎建目录前 → <c>FsOps.CopyMerge</c> 内部 → 删源前复检），
    /// 且以后加/减一处就会让"第 N 次"这种写法静默失准。进度上报只发生在复制<b>之中</b>，
    /// 用它做分界线与调用次数无关。
    /// <para>
    /// 复位纪律：装配即接管、<see cref="Dispose"/> 必定还原（用例侧用 <c>using</c>），
    /// 保证不会把钩子泄漏给同进程的其它用例。
    /// </para>
    /// </remarks>
    private sealed class TargetBecomesLinkAfterCopyProgress : ITransferProgress, IDisposable
    {
        private readonly string _hitPath;
        private readonly Func<string, string>? _originalHook;
        private int _hits;

        internal TargetBecomesLinkAfterCopyProgress(string hitPath)
        {
            _hitPath = hitPath;
            _originalHook = DestinationGuard.Hook;
            DestinationGuard.Hook = Probe;
        }

        /// <summary>钩子是否已上膛（用例断言它，防"钩子没生效也变绿"）。</summary>
        internal bool Armed { get; private set; }

        /// <summary>上膛之后被调用的次数。</summary>
        internal int Hits => _hits;

        public void Report(in TransferProgressInfo info)
        {
            // 任何一次针对本项文件的进度上报都意味着"已在复制中"，此时 CopyMerge 内部那道闸
            // 早已通过，后续判定只可能是删源前复检。
            if (info.TotalInItem > 0)
            {
                Armed = true;
            }
        }

        private string Probe(string path)
        {
            if (!Armed)
            {
                return string.Empty;
            }

            _hits++;
            return _hitPath;
        }

        public void Dispose() => DestinationGuard.Hook = _originalHook;
    }

    /// <summary>引擎用例的共用环境：临时沙箱 + 内存注册表 + 临时落点。</summary>
    private sealed class EngineHarness : IDisposable
    {
        private readonly List<RegistrySeedEntry> _hkcu = new();

        internal EngineHarness(string prefix)
        {
            Sandbox = new TempDirectory(prefix);
            Paths = new TempAppPaths(Sandbox.CreateDirectoryPath("app"));
            Log = new FakeLog();
        }

        internal TempDirectory Sandbox { get; }

        internal TempAppPaths Paths { get; }

        internal FakeLog Log { get; }

        internal InMemoryRegistryBackend Backend { get; private set; } = new();

        internal void SeedValue(string subKey, string name, string value) =>
            _hkcu.Add(new RegistrySeedEntry(
                subKey,
                name,
                new RegistryValue(value, Microsoft.Win32.RegistryValueKind.ExpandString)));

        internal TransferEngine Engine()
        {
            Backend = new InMemoryRegistryBackend(_hkcu);
            var store = new RegistryStore(Backend, Log);
            return new TransferEngine(store, Paths.BackupDirectory, dryRun: false, Log);
        }

        internal PlanItem Item(string current, string target) => new()
        {
            Spec = Catalog.ByKey("documents")!,
            Current = current,
            Target = target,
            RegistryPayload = target,
            RegistryTargets = new[]
            {
                new RegistryTarget(Catalog.UserShellFoldersSubKey, "Personal"),
            },
        };

        public void Dispose() => Sandbox.Dispose();
    }
}
