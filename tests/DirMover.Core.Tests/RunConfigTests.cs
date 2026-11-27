using System.Reflection;
using System.Text;
using DirMover.Core.Engine;
using DirMover.Core.IO;
using DirMover.Core.Registry;

namespace DirMover.Core.Tests;

/// <summary>
/// 目标路径与显示名分离：磁盘路径始终取规格表里的英文出厂物理名，
/// 中文显示只通过目标目录的 <c>desktop.ini</c> 生效，不参与任何路径合成。
/// </summary>
/// <remarks>
/// 这里刻意<b>不写死任何一种语言的期望名</b>：报障机器与构建机可能语言不同，用户也可能自定义过
/// 显示名。用例通过「在临时源目录里真实写入 desktop.ini」来注入名字，并特意用中/日/法/英多种语言
/// 的字面名做参数化 —— 任何把某种语言硬编码进磁盘路径的实现都会在这里打红。
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
    public void ComposeTarget_UsesEnglishPhysicalName_NotLocalizedDisplayName(
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

        Assert.Equal(Path.Combine(@"D:\UserData", "tester", englishTail), target);
        Assert.Equal(englishTail, Path.GetFileName(target));
        Assert.DoesNotContain(localizedName, target, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeTarget_WithoutUserDirectory_StillUsesEnglishPhysicalName()
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
            @"D:\UserData\Desktop",
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
    public void ComposeTarget_IgnoresSourcePhysicalTail_WhenItWasRenamedToChinese()
    {
        // 上一次误修复可能已经把源目录改成了中文名；新实现必须从规格表取英文出厂名，
        // 不能从 currentPath 再取一次物理尾名，否则错误会一直延续。
        using var sandbox = new TempDirectory("compose-renamed-source");
        string chineseSource = sandbox.CreateDirectoryPath("文档");
        var config = new RunConfig
        {
            DestinationRoot = @"D:\UserData",
            CreateUserDirectory = false,
        };

        Assert.Equal(
            @"D:\UserData\Documents",
            config.ComposeTarget(Catalog.ByKey("documents")!, chineseSource, "tester"));
    }

    [Fact]
    public void ComposeTarget_EmptyCurrent_StillReturnsCanonicalEnglishPath()
    {
        // 当前位置未知时目标路径仍可确定；该项会被 BlockingReason() 拦下，不会执行。
        var config = new RunConfig
        {
            DestinationRoot = @"D:\UserData",
            CreateUserDirectory = false,
        };

        Assert.Equal(
            @"D:\UserData\Documents",
            config.ComposeTarget(Catalog.ByKey("documents")!, string.Empty, "tester"));
    }

    // ================================================================ 显示名写入（只改显示，不改路径）

    [Fact]
    public void FolderDisplayName_RawResourceName_IsNotResolved()
    {
        // 目标 desktop.ini 必须写回原始间接串，换一台不同 UI 语言的机器也能正确显示。
        using var sandbox = new TempDirectory("raw-resource");
        string source = sandbox.CreateDirectoryPath("source");
        const string indirect = @"@%SystemRoot%\system32\shell32.dll,-21770";
        WriteDesktopIni(source, indirect);

        Assert.Equal(indirect, FolderDisplayName.ReadRawResourceName(source));
        Assert.NotEqual(indirect, FolderDisplayName.Resolve(source));
    }

    [Fact]
    public void ApplyToDirectory_UpsertsResource_AndSetsShellAttributes()
    {
        using var sandbox = new TempDirectory("apply-display");
        string target = sandbox.CreateDirectoryPath("Documents");
        string ini = Path.Combine(target, "desktop.ini");
        File.WriteAllText(
            ini,
            Section + "\r\nIconResource=icon.ico,0\r\nLocalizedResourceName=Old\r\n\r\n[ViewState]\r\nMode=1\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        OpResult result = FolderDisplayName.ApplyToDirectory(target, "文档");

        Assert.True(result.Ok, result.Message);
        string content = File.ReadAllText(ini);
        Assert.Contains("IconResource=icon.ico,0", content, StringComparison.Ordinal);
        Assert.Contains("[ViewState]", content, StringComparison.Ordinal);
        Assert.Contains("LocalizedResourceName=文档", content, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalizedResourceName=Old", content, StringComparison.Ordinal);
        Assert.Equal("文档", FolderDisplayName.Resolve(target));
        Assert.Equal("Documents", Path.GetFileName(target));
        Assert.True((File.GetAttributes(ini) & (FileAttributes.Hidden | FileAttributes.System)) ==
            (FileAttributes.Hidden | FileAttributes.System));
        Assert.True((File.GetAttributes(target) & FileAttributes.System) != 0);
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
    public async Task ExecuteAsync_CreatesEnglishDirectoryWithChineseDisplayAndWritesEnglishPathToRegistry(
        string localizedName)
    {
        // 端到端：目标必须是英文物理目录，中文只出现在 desktop.ini 的显示名里；
        // 注册表写入值、文件读写路径都必须是英文路径。
        using var sandbox = new TempDirectory("e2e-display-name");
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
            string expectedTarget = Path.Combine(destinationRoot, "Documents");
            Assert.Equal(expectedTarget, item.Target);
            Assert.Equal(expectedTarget, item.RegistryPayload);

            TransferReport report = await engine.ExecuteAsync(plan);

            ItemOutcome outcome = Assert.Single(report.Outcomes);
            Assert.Equal(ItemStatus.Ok, outcome.Status);
            Assert.True(Directory.Exists(expectedTarget));
            Assert.False(Directory.Exists(Path.Combine(destinationRoot, localizedName)));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(expectedTarget, "a.txt")));
            // 目标根下只应出现英文物理目录，不能另外生成中文目录。
            Assert.Equal(
                new[] { "Documents" },
                Directory.GetDirectories(destinationRoot)
                    .Select(path => Path.GetFileName(path)!)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());

            // 中文只存在于 desktop.ini，且 Shell 属性必须就位，否则资源管理器不会应用显示名。
            string ini = Path.Combine(expectedTarget, "desktop.ini");
            Assert.True(File.Exists(ini));
            Assert.Contains("LocalizedResourceName=" + localizedName, File.ReadAllText(ini));
            Assert.Equal(
                localizedName,
                FolderDisplayName.Resolve(expectedTarget));
            Assert.Equal(
                FileAttributes.Hidden | FileAttributes.System,
                File.GetAttributes(ini) & (FileAttributes.Hidden | FileAttributes.System));
            Assert.True((File.GetAttributes(expectedTarget) & FileAttributes.System) != 0);

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
