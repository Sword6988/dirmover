using DirMover.Core.Diagnostics;
using DirMover.Core.Engine;
using DirMover.Core.IO;
using DirMover.Core.Registry;

namespace DirMover.Core.Tests;

/// <summary>T11：引擎编排（单项事务、就地表回滚、闸门、取消、试运行、只读扫描报告）。</summary>
public sealed class TransferEngineTests
{
    [Fact]
    public void BlockOverlappingTargets_SameTarget_DefensivelyBlocksBoth()
    {
        // 目标叶子名现在固定取规格表的英文出厂名，正常规划下两项不可能撞车。
        // 这个闸门保留为防御：一旦以后有人改回"按运行时名字拼目标"，同一目标必须两项一起拦，
        // 否则先搬的删源、后搬的走 Merge，两份内容混进一个目录且两个注册表值指向它。
        using var env = new EngineEnv("engine-overlap");
        TransferEngine engine = env.Engine();

        var items = new List<PlanItem>
        {
            new()
            {
                Spec = Catalog.ByKey("documents")!,
                Current = @"C:\src\Documents",
                Target = @"D:\UserData\Shared",
            },
            new()
            {
                Spec = Catalog.ByKey("pictures")!,
                Current = @"C:\src\Pictures",
                Target = @"D:\UserData\Shared",
            },
        };

        engine.BlockOverlappingTargets(items);

        Assert.All(items, item =>
        {
            Assert.NotEqual(string.Empty, item.InvalidReason);
            Assert.NotEqual(string.Empty, item.BlockingReason());
        });
        Assert.Contains("合并", items[0].InvalidReason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlan_DifferentTargets_AreNotBlocked()
    {
        // 反向保护：正常的两项不能被重叠判定误伤（否则所有转移都做不了）。
        using var env = new EngineEnv("engine-no-overlap");
        string docSrc = env.Sandbox.CreateDirectoryPath("src-documents");
        string picSrc = env.Sandbox.CreateDirectoryPath("src-pictures");
        env.Sandbox.CreateFile(Path.Combine("src-documents", "a.txt"), "x");
        env.Sandbox.CreateFile(Path.Combine("src-pictures", "b.txt"), "y");

        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", docSrc);
        env.SeedValue(Catalog.UserShellFoldersSubKey, "My Pictures", picSrc);

        TransferEngine engine = env.Engine();
        IReadOnlyList<PlanItem> plan = engine.BuildPlan(
            new[] { "documents", "pictures" },
            @"D:\UserData",
            createUserDirectory: false,
            userName: "tester",
            scan: false);

        Assert.Equal(2, plan.Count);
        Assert.NotEqual(plan[0].Target, plan[1].Target, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan, item => Assert.Equal(string.Empty, item.InvalidReason));
    }

    [Fact]
    public async Task ExecuteAsync_CopyPath_CopiesWritesRegistryAndRemovesSource()
    {
        using var env = new EngineEnv("engine-copy");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = env.Sandbox.CreateDirectoryPath("dst"); // 目标已存在 → 走复制合并

        TransferReport report = await env.Engine().ExecuteAsync(new[] { env.Item(src, dst) });

        Assert.True(report.Ok);
        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Ok, outcome.Status);
        Assert.Equal(TransferStage.SourceRemoved, outcome.Stage);
        Assert.Equal(1, outcome.Files);
        Assert.False(Directory.Exists(src));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "a.txt")));

        Assert.Single(env.Backend.Writes);
        Assert.Equal(dst, env.Backend.Writes[0].Data);

        Assert.NotNull(report.SnapshotPath);
        Assert.True(File.Exists(report.SnapshotPath!));
        // 全部收尾 → 意图日志被删除
        Assert.False(File.Exists(Path.Combine(env.Paths.BackupDirectory, IntentJournal.FileName)));
    }

    [Fact]
    public async Task ExecuteAsync_RenamePath_MovesWithoutCopying()
    {
        using var env = new EngineEnv("engine-rename");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = env.Sandbox.Combine("dst", "Documents");

        TransferReport report = await env.Engine().ExecuteAsync(new[] { env.Item(src, dst) });

        Assert.True(report.Ok);
        Assert.Equal(TransferStage.SourceRemoved, report.Outcomes[0].Stage);
        Assert.False(Directory.Exists(src));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "a.txt")));
    }

    [Fact]
    public async Task ExecuteAsync_CopyFailure_KeepsSourceAndDoesNotWriteRegistry()
    {
        using var env = new EngineEnv("engine-copyfail");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        // 目标的"上级"是一个文件 → 建目录必失败 → 复制失败
        string blocker = env.Sandbox.CreateFile("blocker", "not a directory");
        string dst = Path.Combine(blocker, "Documents");

        TransferReport report = await env.Engine().ExecuteAsync(new[] { env.Item(src, dst) });

        Assert.False(report.Ok);
        Assert.Equal(ItemStatus.Failed, report.Outcomes[0].Status);
        Assert.Equal(TransferStage.CopyFailed, report.Outcomes[0].Stage);
        Assert.True(Directory.Exists(src));
        Assert.Empty(env.Backend.Writes);
    }

    [Fact]
    public async Task ExecuteAsync_RegistryWriteFailure_RollsBackAndKeepsSource()
    {
        using var env = new EngineEnv("engine-regfail");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = env.Sandbox.Combine("dst", "Documents");

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
        env.Backend.WriteFailure = (subKey, name, attempt) => string.Equals(name, aliasName, StringComparison.Ordinal);

        TransferReport report = await engine.ExecuteAsync(new[] { item });

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Failed, outcome.Status);
        Assert.Equal(TransferStage.RegistryFailed, outcome.Stage);
        Assert.Contains("写入注册表失败", outcome.Message);
        // 源目录保留（rename 路径必须改回原位）
        Assert.True(Directory.Exists(src));
        // 而且必须是"真的改回去了"：目标侧不能还留着已迁移的那份。
        // 只断言"源还在"是不够的 —— 如果改回那一步被删掉，源本来就没被动过，用例照样绿。
        Assert.False(Directory.Exists(dst), "回滚后目标侧不应残留已迁移的目录");
        Assert.True(File.Exists(Path.Combine(src, "a.txt")), "回滚后源内容必须完好");
        // 本项已写入的值就地回滚：第二次写入是恢复原值
        Assert.Equal(2, env.Backend.Writes.Count);
        Assert.Equal(@"C:\old-documents", env.Backend.Writes[^1].Data);
        Assert.NotEmpty(env.Backend.Deletes);
    }

    [Fact]
    public async Task ExecuteAsync_NotEnoughSpace_BlocksOnlyThatItem()
    {
        using var env = new EngineEnv("engine-space");
        string blockedSource = env.Sandbox.CreateDirectoryPath("blocked");
        env.Sandbox.CreateFile(Path.Combine("blocked", "a.txt"), "x");
        string blockedTarget = env.Sandbox.CreateDirectoryPath("blocked-dst");

        var blocked = env.Item(blockedSource, blockedTarget);
        blocked.Scanned = true;
        blocked.TargetExists = true;
        blocked.Stat = new TreeStat { Bytes = long.MaxValue / 4 };

        string okSource = env.Sandbox.CreateDirectoryPath("ok-src");
        env.Sandbox.CreateFile(Path.Combine("ok-src", "b.txt"), "y");
        PlanItem normal = env.Item(okSource, env.Sandbox.Combine("dst", "Documents"));

        TransferReport report = await env.Engine().ExecuteAsync(new[] { blocked, normal });

        Assert.Equal(2, report.Outcomes.Count);
        Assert.Equal(ItemStatus.Failed, report.Outcomes[0].Status);
        Assert.Contains("空间不足", report.Outcomes[0].Message);
        Assert.Equal(ItemStatus.Ok, report.Outcomes[1].Status);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_AllItemsCancelledAndNothingWritten()
    {
        using var env = new EngineEnv("engine-cancel");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = env.Sandbox.Combine("dst", "Documents");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        TransferReport report = await env.Engine().ExecuteAsync(new[] { env.Item(src, dst) }, ct: cts.Token);

        Assert.True(report.Cancelled);
        Assert.False(report.Ok);
        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Cancelled, outcome.Status);
        Assert.True(Directory.Exists(src));
        Assert.False(Directory.Exists(dst));
        Assert.Empty(env.Backend.Writes);
        Assert.False(File.Exists(Path.Combine(env.Paths.BackupDirectory, IntentJournal.FileName)));
    }

    [Fact]
    public async Task ExecuteAsync_DryRun_HasNoSideEffects()
    {
        using var env = new EngineEnv("engine-dryrun");
        string src = env.Sandbox.CreateDirectoryPath("src");
        env.Sandbox.CreateFile(Path.Combine("src", "a.txt"), "hello");
        string dst = env.Sandbox.Combine("dst", "Documents");

        TransferReport report = await env.Engine(dryRun: true).ExecuteAsync(new[] { env.Item(src, dst) });

        Assert.True(report.Ok);
        Assert.Contains("[试运行]", report.Outcomes[0].Message);
        Assert.Null(report.SnapshotPath);
        Assert.Empty(env.Backend.Writes);
        Assert.True(Directory.Exists(src));
        Assert.False(Directory.Exists(dst));
        Assert.Empty(Directory.GetFiles(env.Paths.BackupDirectory));
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedException_DoesNotStopRemainingItems()
    {
        using var env = new EngineEnv("engine-unexpected");
        string brokenSource = env.Sandbox.CreateDirectoryPath("broken");
        env.Sandbox.CreateFile(Path.Combine("broken", "a.txt"), "x");
        PlanItem broken = env.Item(brokenSource, env.Sandbox.Combine("broken-dst", "Documents"));
        broken.Scanned = true;
        broken.Stat = null!; // 故意制造未预期异常（单项边界必须吃掉）

        string okSource = env.Sandbox.CreateDirectoryPath("ok");
        env.Sandbox.CreateFile(Path.Combine("ok", "b.txt"), "y");
        PlanItem normal = env.Item(okSource, env.Sandbox.Combine("dst", "Documents"));

        TransferReport report = await env.Engine().ExecuteAsync(new[] { broken, normal });

        Assert.Equal(2, report.Outcomes.Count);
        Assert.Equal(ItemStatus.Failed, report.Outcomes[0].Status);
        Assert.Equal(TransferStage.UnexpectedError, report.Outcomes[0].Stage);
        Assert.Equal(ItemStatus.Ok, report.Outcomes[1].Status);
    }

    [Fact]
    public void BuildPlan_ResolvesCurrentAndComposesTarget()
    {
        using var env = new EngineEnv("engine-buildplan");
        string current = env.Sandbox.Combine("src", "Documents");
        Directory.CreateDirectory(current);
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", current);

        string destinationRoot = env.Sandbox.Combine("dest");
        IReadOnlyList<PlanItem> plan = env.Engine().BuildPlan(
            new[] { "documents" },
            destinationRoot,
            createUserDirectory: false,
            userName: "tester",
            scan: false);

        PlanItem item = Assert.Single(plan);
        Assert.Equal(current, item.Current);
        // 目标叶子名固定取规格表英文出厂名；中文显示由 desktop.ini 负责，不参与路径合成。
        Assert.Equal(Path.Combine(destinationRoot, "Documents"), item.Target);
        Assert.False(item.Same);
        Assert.Single(item.RegistryTargets);
    }

    [Fact]
    public void BuildPlan_RegistryReadFailure_IsInvalidReasonFailClosed()
    {
        var log = new FakeLog();
        var store = new RegistryStore(new AlwaysFailingRegistryBackend(), log);
        var engine = new TransferEngine(store, backupDir: null, dryRun: false, log);

        IReadOnlyList<PlanItem> plan = engine.BuildPlan(
            new[] { "documents" },
            @"D:\UserData",
            createUserDirectory: false,
            userName: "tester",
            scan: false);

        PlanItem item = Assert.Single(plan);
        Assert.Contains("读取注册表失败", item.InvalidReason);
        Assert.NotEqual(string.Empty, item.BlockingReason());
    }

    [Fact]
    public void BuildRestorePlan_MissingSource_IsRegistryOnlyAndKeepsPercentForm()
    {
        using var env = new EngineEnv("engine-restore");
        string gone = env.Sandbox.Combine("gone");
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", gone);

        IReadOnlyList<PlanItem> plan = env.Engine().BuildRestorePlan(new[] { "documents" }, scan: false);

        PlanItem item = Assert.Single(plan);
        FolderSpec spec = Catalog.ByKey("documents")!;
        Assert.True(item.RegistryOnly);
        Assert.Equal(spec.DefaultPath, item.RegistryPayload);
        Assert.Contains("%USERPROFILE%", item.RegistryPayload);
        Assert.Equal(PathTools.Expand(spec.DefaultPath), item.Target);
        Assert.Equal(string.Empty, item.BlockingReason());
    }

    [Fact]
    public async Task ExecuteAsync_RegistryOnly_WritesRegistryWithoutTouchingFiles()
    {
        using var env = new EngineEnv("engine-registryonly");
        string gone = env.Sandbox.Combine("gone");
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", gone);

        IReadOnlyList<PlanItem> plan = env.Engine().BuildRestorePlan(new[] { "documents" }, scan: false);
        TransferReport report = await env.Engine().ExecuteAsync(plan);

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.Equal(ItemStatus.Skipped, outcome.Status);
        Assert.Equal(TransferStage.RegistryWritten, outcome.Stage);
        Assert.Equal(UiText.StatusRegistryOnly, outcome.Message);

        FolderSpec spec = Catalog.ByKey("documents")!;
        Assert.Single(env.Backend.Writes);
        Assert.Equal(spec.DefaultPath, env.Backend.Writes[0].Data);
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public async Task ExecuteAsync_SamePath_IsSkippedWithoutWrites()
    {
        using var env = new EngineEnv("engine-samepath");
        string destinationRoot = env.Sandbox.Combine("dest");
        // 当前位置的物理尾名与目标解析结果一致 → Same 判定成立。
        // 用源物理名而非任何语言的显示名，保持用例语言无关。
        string current = Path.Combine(destinationRoot, "Documents");
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", current);

        IReadOnlyList<PlanItem> plan = env.Engine().BuildPlan(
            new[] { "documents" },
            destinationRoot,
            createUserDirectory: false,
            userName: "tester",
            scan: false);

        TransferReport report = await env.Engine().ExecuteAsync(plan);

        ItemOutcome outcome = Assert.Single(report.Outcomes);
        Assert.True(plan[0].Same);
        Assert.Equal(ItemStatus.Skipped, outcome.Status);
        Assert.Equal(UiText.StatusSame, outcome.Message);
        Assert.Empty(env.Backend.Writes);
    }

    [Fact]
    public async Task ScanReportBuildAsync_ProducesResultAndWritesNothing()
    {
        using var env = new EngineEnv("engine-scanasync");
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", @"C:\Users\me\Documents");
        TransferEngine engine = env.Engine();

        ScanReportResult result = await ScanReport.BuildAsync(engine.Store);

        Assert.Equal(Catalog.All.Count, result.Items.Count);
        Assert.Empty(env.Backend.Writes);
        Assert.False(Directory.Exists(env.Sandbox.Combine("logs")));

        string text = result.ToText();
        Assert.Contains("一、7 项用户文件夹", text);
        Assert.Contains("三、注册表值计数", text);
        Assert.Contains("四、读取告警", text);
    }

    [Fact]
    public void ScanReport_MarksSystemDirectorySourceAsRisky()
    {
        using var env = new EngineEnv("engine-scanrisk");
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", @"C:\Windows");

        var config = new RunConfig
        {
            DestinationRoot = env.Sandbox.Combine("dest"),
            CreateUserDirectory = false,
        };

        ScanReportResult result = ScanReport.BuildResult(env.Engine(), config, Array.Empty<DriveVolume>());

        ScanReportItem documents = result.Items.Single(item => item.Key == "documents");
        Assert.Equal("系统目录", documents.RiskLabel);
        Assert.StartsWith("不可执行：", documents.State, StringComparison.Ordinal);
        Assert.Empty(env.Backend.Writes);
    }

    [Fact]
    public void ScanReportResult_WriteToFile_CreatesReadableFile()
    {
        using var sandbox = new TempDirectory("engine-scanfile");
        var result = new ScanReportResult
        {
            Items = new[]
            {
                new ScanReportItem("documents", "我的文档", @"C:\Users\me\Documents", true, @"D:\UserData\Documents", "可执行", "正常"),
            },
            Drives = Array.Empty<DriveVolume>(),
            UserShellFolderValues = 18,
            ShellFolderValues = 17,
        };

        string path = Path.Combine(sandbox.Root, "logs", "scan-20240101-000000.txt");
        bool written = result.WriteToFile(path);

        Assert.True(written);
        Assert.True(File.Exists(path));
        string text = File.ReadAllText(path);
        Assert.Contains("我的文档", text);
        Assert.Contains("18 个值", text);
        Assert.Contains("17 个值", text);
    }

    [Fact]
    public void ScanReport_HasThreeSections_AndWritesNothing()
    {
        using var env = new EngineEnv("engine-scanreport");
        env.SeedValue(Catalog.UserShellFoldersSubKey, "Personal", @"C:\Users\me\Documents");

        var config = new RunConfig
        {
            DestinationRoot = env.Sandbox.Combine("dest"),
            CreateUserDirectory = true,
        };

        string text = ScanReport.Build(env.Engine(), config, Array.Empty<DriveVolume>());

        Assert.Contains("一、7 项用户文件夹", text);
        Assert.Contains("二、可用的非系统固定磁盘", text);
        Assert.Contains("三、注册表值计数", text);
        Assert.Contains("我的文档", text);
        Assert.Empty(env.Backend.Writes);
    }

    /// <summary>引擎用例的共用环境：临时沙箱 + 内存注册表 + 临时落点。</summary>
    private sealed class EngineEnv : IDisposable
    {
        private readonly List<RegistrySeedEntry> _hkcu = new();

        internal EngineEnv(string prefix)
        {
            Sandbox = new TempDirectory(prefix);
            Paths = new TempAppPaths(Sandbox.CreateDirectoryPath("app"));
            Log = new FakeLog();
        }

        internal TempDirectory Sandbox { get; }

        internal TempAppPaths Paths { get; }

        internal FakeLog Log { get; }

        internal InMemoryRegistryBackend Backend { get; private set; } = new();

        internal RegistryStore Store { get; private set; } = new(new InMemoryRegistryBackend(), new FakeLog());

        /// <summary>放一个 HKCU 值（下一次取 Store 时生效）。</summary>
        internal void SeedValue(string subKey, string name, string value)
        {
            _hkcu.Add(new RegistrySeedEntry(
                subKey,
                name,
                new RegistryValue(value, Microsoft.Win32.RegistryValueKind.ExpandString)));
        }

        internal TransferEngine Engine(bool dryRun = false)
        {
            Backend = new InMemoryRegistryBackend(_hkcu);
            Store = new RegistryStore(Backend, Log);
            return new TransferEngine(Store, Paths.BackupDirectory, dryRun, Log);
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

    /// <summary>一切读写都抛 <see cref="RegistryReadException"/> 的后端（测 fail-closed）。</summary>
    private sealed class AlwaysFailingRegistryBackend : IRegistryBackend
    {
        public RegistryValue? ReadValue(string subKey, string name) =>
            throw new RegistryReadException("模拟读失败");

        public RegistryValue? ReadHklmValue(string subKey, string name) =>
            throw new RegistryReadException("模拟读失败");

        public void WriteValue(string subKey, string name, object data, Microsoft.Win32.RegistryValueKind kind) =>
            throw new RegistryReadException("模拟写失败");

        public void DeleteValue(string subKey, string name)
        {
        }

        public IReadOnlyDictionary<string, RegistryValue> EnumerateValues(string subKey) =>
            throw new RegistryReadException("模拟枚举失败");
    }
}
