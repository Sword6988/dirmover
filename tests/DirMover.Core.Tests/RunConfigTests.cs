using System.Reflection;
using System.Text;
using DirMover.Core.Engine;
using DirMover.Core.IO;
using DirMover.Core.Registry;

namespace DirMover.Core.Tests;

/// <summary>
/// Bug 2 回归：目标目录名必须在<b>运行机器上</b>按源目录 <c>desktop.ini</c> 的本地化显示名解析，
/// Core 里不得写死任何语言的目录名；解析不到时回退源物理尾名（语言无关）。
/// </summary>
/// <remarks>
/// 这里刻意<b>不写死任何一种语言的期望名</b>：报障机器与构建机可能语言不同，用户也可能自定义过
/// 显示名。用例通过「在临时源目录里真实写入 desktop.ini」来注入名字，并特意用中/日/法/英多种语言
/// 的字面名做参数化 —— 任何把某种语言硬编码进 Core 的实现都会在这里打红。
/// </remarks>
public sealed class RunConfigTests
{
    private const string Section = "[.ShellClassInfo]";

    // ================================================================ 静态真源：不得有语言字表

    [Fact]
    public void Catalog_FolderSpecHasNoHardcodedTargetNameColumn()
    {
        // 规格表一旦带"目标目录名"列，就等于把构建机的语言写进 Core。
        string[] properties = typeof(FolderSpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static p => p.Name)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "Aliases", "DefaultPath", "Display", "Key", "ValueName" },
            properties);
    }

    [Fact]
    public void Catalog_StillKeepsSevenSpecsWithOriginalDisplayAndDefaultPath()
    {
        Assert.Equal(7, Catalog.All.Count);
        Assert.Equal(
            new[] { "documents", "favorites", "pictures", "music", "videos", "downloads", "desktop" },
            Catalog.AllKeys.ToArray());

        // Display 是界面标签、DefaultPath 是恢复默认值用的英文出厂名，两者都保留原值。
        foreach (FolderSpec spec in Catalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Display));
            Assert.StartsWith("%USERPROFILE%", spec.DefaultPath, StringComparison.Ordinal);
            Assert.NotEmpty(spec.Aliases);
        }
    }

    // ================================================================ FolderDisplayName 解析

    [Theory]
    [InlineData("文档")]
    [InlineData("ドキュメント")]
    [InlineData("Documents localisés")]
    [InlineData("My Localized Docs")]
    public void FolderDisplayName_LiteralName_IsUsedVerbatim(string localizedName)
    {
        using var sandbox = new TempDirectory("fdn-literal");
        string source = sandbox.CreateDirectoryPath("src");
        WriteDesktopIni(source, localizedName);

        Assert.Equal(localizedName, FolderDisplayName.Resolve(source));
    }

    [Fact]
    public void FolderDisplayName_Utf16Ini_IsParsed()
    {
        // 系统生成的 desktop.ini 常是 UTF-16LE with BOM；BOM 探测必须两个目标框架都生效。
        using var sandbox = new TempDirectory("fdn-utf16");
        string source = sandbox.CreateDirectoryPath("src");
        string ini = Path.Combine(source, "desktop.ini");
        File.WriteAllText(
            ini,
            Section + "\r\nLocalizedResourceName=My Docs\r\n",
            Encoding.Unicode);

        Assert.Equal("My Docs", FolderDisplayName.Resolve(source));
    }

    [Fact]
    public void FolderDisplayName_IndirectString_NeverReturnsRawIndirectText()
    {
        // 间接串由 SHLoadIndirectString 按运行机器的 MUI 语言解析。不同语言/版本上可能解析失败，
        // 但**绝不能**把 @"%SystemRoot%\system32\shell32.dll,-21770" 这种原始串当目录名。
        using var sandbox = new TempDirectory("fdn-indirect");
        string source = sandbox.CreateDirectoryPath("src");
        const string raw = @"@%SystemRoot%\system32\shell32.dll,-21770";
        WriteDesktopIni(source, raw);

        string? resolved = FolderDisplayName.Resolve(source);

        Assert.NotEqual(raw, resolved);
        if (resolved is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(resolved));
            Assert.DoesNotContain("@", resolved);
            Assert.DoesNotContain("\\", resolved);
            Assert.DoesNotContain("/", resolved);
        }
    }

    [Fact]
    public void FolderDisplayName_MissingIni_ReturnsNull()
    {
        using var sandbox = new TempDirectory("fdn-noini");
        string source = sandbox.CreateDirectoryPath("src");

        Assert.Null(FolderDisplayName.Resolve(source));
    }

    [Fact]
    public void FolderDisplayName_IniWithoutLocalizedResourceName_ReturnsNull()
    {
        using var sandbox = new TempDirectory("fdn-nokey");
        string source = sandbox.CreateDirectoryPath("src");
        sandbox.CreateFile(
            Path.Combine("src", "desktop.ini"),
            Section + "\r\nIconResource=%SystemRoot%\\system32\\imageres.dll,-112\r\n");

        Assert.Null(FolderDisplayName.Resolve(source));
    }

    [Theory]
    [InlineData("bad\\name")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void FolderDisplayName_InvalidDirectoryName_ReturnsNull(string invalid)
    {
        using var sandbox = new TempDirectory("fdn-invalid");
        string source = sandbox.CreateDirectoryPath("src");
        WriteDesktopIni(source, invalid);

        Assert.Null(FolderDisplayName.Resolve(source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FolderDisplayName_BlankPath_ReturnsNull(string path)
    {
        Assert.Null(FolderDisplayName.Resolve(path));
    }

    // ================================================================ ComposeTarget

    [Theory]
    [InlineData("documents", "Documents", "文档")]
    [InlineData("favorites", "Favorites", "お気に入り")]
    [InlineData("pictures", "Pictures", "Images localisées")]
    [InlineData("music", "Music", "My Music")]
    [InlineData("videos", "Videos", "视频")]
    [InlineData("downloads", "Downloads", "Téléchargements")]
    [InlineData("desktop", "Desktop", "デスクトップ")]
    public void ComposeTarget_UsesNameFromSourceDesktopIni_NotEnglishPhysicalTail(
        string key,
        string englishTail,
        string localizedName)
    {
        using var sandbox = new TempDirectory("compose-localized");
        string source = sandbox.CreateDirectoryPath(Path.Combine("src", englishTail));
        WriteDesktopIni(source, localizedName);

        var config = new RunConfig
        {
            DestinationRoot = @"D:\UserData",
            CreateUserDirectory = true,
        };

        string target = config.ComposeTarget(Catalog.ByKey(key)!, source, "tester");

        Assert.Equal(Path.Combine(@"D:\UserData", "tester", localizedName), target);
        // 断言"目标叶子名不是英文物理尾名"，而不是"整条路径不含该英文串"——
        // 合法的本地化名完全可能包含英文词（例如 "My Music"），子串断言会误伤。
        Assert.NotEqual(englishTail, Path.GetFileName(target));
    }

    [Fact]
    public void ComposeTarget_WithoutUserDirectory_StillUsesLocalizedName()
    {
        using var sandbox = new TempDirectory("compose-nouser");
        string source = sandbox.CreateDirectoryPath("Desktop");
        WriteDesktopIni(source, "Bureau");

        var config = new RunConfig
        {
            DestinationRoot = @"D:\UserData",
            CreateUserDirectory = false,
        };

        Assert.Equal(
            @"D:\UserData\Bureau",
            config.ComposeTarget(Catalog.ByKey("desktop")!, source, "tester"));
    }

    [Fact]
    public void ComposeTarget_EmptyRoot_StillReturnsCurrentPath()
    {
        // 没有可用目标盘时"目标 = 当前位置"的同路径语义必须保持（PRD §2.1）。
        var config = new RunConfig { DestinationRoot = string.Empty, CreateUserDirectory = true };

        Assert.Equal(
            @"C:\Users\tester\Documents",
            config.ComposeTarget(
                Catalog.ByKey("documents")!,
                @"C:\Users\tester\Documents",
                "tester"));
    }

    [Fact]
    public void ComposeTarget_NoIni_FallsBackToSourcePhysicalTail()
    {
        // 回退链第一级：解析不到本地化名时用源路径最后一段的物理名（语言无关，不凭空造名）。
        using var sandbox = new TempDirectory("compose-fallback");
        string source = sandbox.CreateDirectoryPath("Documents");
        var config = new RunConfig
        {
            DestinationRoot = @"D:\UserData",
            CreateUserDirectory = false,
        };

        Assert.Equal(
            @"D:\UserData\Documents",
            config.ComposeTarget(Catalog.ByKey("documents")!, source, "tester"));
    }

    [Fact]
    public void ComposeTarget_NoIniAndEmptyCurrent_ReturnsCurrentInsteadOfInventingName()
    {
        // 回退链第二级：既无本地化名也无物理基名时，原样返回当前位置让 Same 判定接管。
        // 刻意**不**回退到 spec.Display —— 那是中文界面标签，写进路径等于又引入一种语言。
        var config = new RunConfig
        {
            DestinationRoot = @"D:\UserData",
            CreateUserDirectory = false,
        };

        Assert.Equal(
            string.Empty,
            config.ComposeTarget(Catalog.ByKey("documents")!, string.Empty, "tester"));
    }

    // ================================================================ 引擎链路

    [Fact]
    public void BuildPlan_RegistryPayloadEqualsTarget()
    {
        // 写入注册表的值必须与目标实际文件夹名一致，否则 shell 认的路径和盘上的目录对不上。
        var backend = new InMemoryRegistryBackend();
        var store = new RegistryStore(backend, new FakeLog());
        var engine = new TransferEngine(store, backupDir: null, dryRun: true, new FakeLog());

        IReadOnlyList<PlanItem> plan = engine.BuildPlan(
            Catalog.AllKeys,
            @"D:\UserData",
            createUserDirectory: false,
            userName: "tester",
            scan: false);

        Assert.Equal(7, plan.Count);
        foreach (PlanItem item in plan)
        {
            Assert.Equal(item.Target, item.RegistryPayload);
            Assert.False(string.IsNullOrWhiteSpace(item.Target));
        }
    }

    [Theory]
    [InlineData("文档")]
    [InlineData("Mes documents")]
    [InlineData("ドキュメント")]
    public async Task ExecuteAsync_CreatesLocalizedNamedDirectoryAndWritesSamePathToRegistry(
        string localizedName)
    {
        // 端到端：源是英文物理名 + 本地化 desktop.ini，目标必须落成该本地化名目录，
        // 注册表写入值等于该路径；且不能同时生成英文物理名目录。
        using var sandbox = new TempDirectory("e2e-localized");
        string source = sandbox.CreateDirectoryPath(Path.Combine("src", "Documents"));
        sandbox.CreateFile(Path.Combine("src", "Documents", "a.txt"), "hello");
        WriteDesktopIni(source, localizedName);
        string destinationRoot = sandbox.Combine("dest");
        var paths = new TempAppPaths(sandbox.CreateDirectoryPath("app"));

        var backend = new RegistrySeed()
            .Add(Catalog.UserShellFoldersSubKey, "Personal", source, expandable: false)
            .ToBackend();
        var store = new RegistryStore(backend, new FakeLog());
        var engine = new TransferEngine(store, paths.BackupDirectory, dryRun: false, new FakeLog());

        // 临时沙箱在 C:\Users\… 之下，而目标禁区名单含 <系统盘>\Users（规划期整段拒绝）。
        // 把系统盘临时改成一个不存在的盘，让用例只验"目标目录名 / 注册表写入值"这条性质。
        string originalDrive = Catalog.SystemDrive;
        Catalog.SystemDrive = "Q:";
        try
        {
            IReadOnlyList<PlanItem> plan = engine.BuildPlan(
                new[] { "documents" },
                destinationRoot,
                createUserDirectory: false,
                userName: "tester",
                scan: true);

            PlanItem item = Assert.Single(plan);
            string expectedTarget = Path.Combine(destinationRoot, localizedName);
            Assert.Equal(expectedTarget, item.Target);
            Assert.Equal(expectedTarget, item.RegistryPayload);

            TransferReport report = await engine.ExecuteAsync(plan);

            ItemOutcome outcome = Assert.Single(report.Outcomes);
            Assert.Equal(ItemStatus.Ok, outcome.Status);
            Assert.True(Directory.Exists(expectedTarget));
            Assert.False(Directory.Exists(Path.Combine(destinationRoot, "Documents")));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(expectedTarget, "a.txt")));
            // 目标根下只应出现这一个目录：防止"本地化名 + 英文物理名同时生成"的半修复被判绿。
            Assert.Equal(
                new[] { localizedName },
                Directory.GetDirectories(destinationRoot)
                    .Select(path => Path.GetFileName(path)!)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());

            var write = Assert.Single(backend.Writes, entry => entry.Name == "Personal");
            Assert.Equal(expectedTarget, write.Data as string);
        }
        finally
        {
            Catalog.SystemDrive = originalDrive;
        }
    }

    // ================================================================ 辅助

    private static void WriteDesktopIni(string sourceDirectory, string localizedResourceName)
    {
        File.WriteAllText(
            Path.Combine(sourceDirectory, "desktop.ini"),
            Section + "\r\nLocalizedResourceName=" + localizedResourceName + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
