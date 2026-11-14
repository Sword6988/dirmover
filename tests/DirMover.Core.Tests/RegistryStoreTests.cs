using DirMover.Core.Registry;
using Microsoft.Win32;

namespace DirMover.Core.Tests;

/// <summary>
/// 注册表层红线（简报 §10-9、§10-11）：可展开类型判定、读失败 ≠ 值不存在、
/// 快照期读失败必须跳过回滚、快照落盘失败不抛。
/// </summary>
/// <remarks>
/// 这些用例全程使用 <see cref="InMemoryRegistryBackend"/>：一旦触碰真实注册表，
/// 就会在构建机上改写真实的 <c>User Shell Folders</c>——这本身就是一次生产事故。
/// </remarks>
public sealed class RegistryStoreTests
{
    private const string SubKey = Catalog.UserShellFoldersSubKey;

    // ================================================================ 类型判定（红线 11）

    [Theory]
    [InlineData(@"%USERPROFILE%\Documents", RegistryValueKind.ExpandString)]
    [InlineData("%USERPROFILE%", RegistryValueKind.ExpandString)]
    [InlineData(@"%USERPROFILE%\Desktop", RegistryValueKind.ExpandString)]
    [InlineData(@"D:\a%USERPROFILE%b\UserData", RegistryValueKind.String)]
    [InlineData("100%", RegistryValueKind.String)]
    [InlineData("%FOO", RegistryValueKind.String)]
    [InlineData("%FOO%bar", RegistryValueKind.String)]
    [InlineData(@"D:\UserData\me\Documents", RegistryValueKind.String)]
    public void SetText_PicksExpandStringOnlyForLeadingVariableForm(string value, RegistryValueKind expected)
    {
        RegistryStore store = NewStore(out InMemoryRegistryBackend backend);
        RegistryValueKind actual = store.SetText(SubKey, "Personal", value);

        Assert.Equal(expected, actual);
        Assert.Equal(expected, backend.Writes[^1].Kind);
    }

    [Fact]
    public void GetText_ExpandsExpandableValuesAndKeepsRawWhenAsked()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        RegistryStore store = NewStore(out _);
        store.SetText(SubKey, "Personal", @"%USERPROFILE%\Documents");

        Assert.Equal(profile + @"\Documents", store.GetText(SubKey, "Personal"));
        Assert.Equal(@"%USERPROFILE%\Documents", store.GetText(SubKey, "Personal", expand: false));
    }

    [Fact]
    public void GetText_MissingValueReturnsEmptyString()
    {
        RegistryStore store = NewStore(out _);
        Assert.Equal(string.Empty, store.GetText(SubKey, "NotThere"));
    }

    // ================================================================ 读失败 ≠ 值不存在

    [Fact]
    public void ReadFailureIsDistinguishableFromMissingValue()
    {
        var backend = new ThrowingRegistryBackend(new[] { "Personal" });
        var store = new RegistryStore(backend, new FakeLog());

        // 读失败：Exists 只能是 false（旧契约），但必须能通过 ConsumeReadError 看到失败。
        Assert.False(store.Exists(SubKey, "Personal"));
        string? error = store.ConsumeReadError();
        Assert.NotNull(error);
        Assert.Contains("Personal", error!, StringComparison.Ordinal);

        // 取走即清空：第二次不会再报同一条错误。
        Assert.Null(store.ConsumeReadError());

        // 真正不存在：Exists=false，但没有任何读取错误。
        Assert.False(store.Exists(SubKey, "Absolutely-Not-There"));
        Assert.Null(store.ConsumeReadError());
    }

    [Fact]
    public void EnumerateFailureIsReportedAndDoesNotThrow()
    {
        var backend = new ThrowingRegistryBackend(Array.Empty<string>(), failEnumerate: true);
        var log = new FakeLog();
        var store = new RegistryStore(backend, log);

        IReadOnlyDictionary<string, RegistryValue> values = store.EnumerateValues(SubKey);

        Assert.Empty(values);
        Assert.NotEmpty(log.Warns);
        Assert.NotNull(store.LastReadError);
    }

    // ================================================================ 快照 / 回滚（红线 9）

    [Fact]
    public void Snapshot_DeduplicatesTargetsCaseInsensitively()
    {
        RegistryStore store = NewStore(out _);
        var targets = new[]
        {
            new RegistryTarget(SubKey, "Personal"),
            new RegistryTarget(SubKey, "personal"),
            new RegistryTarget(SubKey, "Desktop"),
        };

        RegistrySnapshot snapshot = store.Snapshot(targets);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.False(string.IsNullOrEmpty(snapshot.CreatedAt));
    }

    [Fact]
    public void Snapshot_RecordsExistedFlagForPresentAndAbsentValues()
    {
        RegistryStore store = NewStore(out _, seed: s => s
            .Add(SubKey, "Personal", @"%USERPROFILE%\Documents"));

        RegistrySnapshot snapshot = store.Snapshot(new[]
        {
            new RegistryTarget(SubKey, "Personal"),
            new RegistryTarget(SubKey, "Desktop"),
        });

        RegistryEntry personal = Assert.Single(snapshot.Entries, e => e.Name == "Personal");
        Assert.True(personal.Existed);
        Assert.False(personal.Unknown);
        Assert.Equal(RegistryValueKind.ExpandString, personal.Kind);

        RegistryEntry desktop = Assert.Single(snapshot.Entries, e => e.Name == "Desktop");
        Assert.False(desktop.Existed);
        Assert.False(desktop.Unknown);
    }

    [Fact]
    public void Rollback_UnknownEntriesAreSkippedAndNeverDeleted()
    {
        // 红线 9：快照期读失败的条目，回滚时既不能恢复也不能删除。
        var backend = new ThrowingRegistryBackend(Array.Empty<string>());
        var log = new FakeLog();
        var store = new RegistryStore(backend, log);

        backend.FailReadsFor.Add("Personal");
        RegistrySnapshot snapshot = store.Snapshot(new[] { new RegistryTarget(SubKey, "Personal") });
        Assert.True(snapshot.Entries[0].Unknown);

        backend.FailReadsFor.Clear();
        (int restored, int removed) = store.Rollback(snapshot);

        Assert.Equal(0, restored);
        Assert.Equal(0, removed);
        Assert.Equal(1, store.LastRollbackSkipped);
        Assert.Empty(backend.Inner.Deletes);
        Assert.NotEmpty(log.Warns);
    }

    [Fact]
    public void Rollback_RestoresExistedValuesAndDeletesAbsentOnes()
    {
        RegistryStore store = NewStore(out InMemoryRegistryBackend backend, seed: s => s
            .Add(SubKey, "Personal", @"%USERPROFILE%\Documents", expandable: false));

        RegistrySnapshot snapshot = store.Snapshot(new[]
        {
            new RegistryTarget(SubKey, "Personal"),
            new RegistryTarget(SubKey, "Desktop"),
        });

        // 模拟执行期把两个值都改了：回滚必须恢复前者、删除后者。
        store.SetText(SubKey, "Personal", @"D:\UserData\me\Documents");
        store.SetText(SubKey, "Desktop", @"D:\UserData\me\Desktop");

        (int restored, int removed) = store.Rollback(snapshot);

        Assert.Equal(1, restored);
        Assert.Equal(1, removed);
        Assert.Equal(@"%USERPROFILE%\Documents", backend.ReadValue(SubKey, "Personal")!.Value.Data);
        Assert.Null(backend.ReadValue(SubKey, "Desktop"));
        Assert.Equal(0, store.LastRollbackSkipped);
    }

    // ================================================================ 快照落盘

    [Fact]
    public void SaveSnapshotThenLoadSnapshotRoundTripsValuesAndKinds()
    {
        using var sandbox = new TempDirectory("snapshot");
        RegistryStore store = NewStore(out _, seed: s => s
            .Add(SubKey, "Personal", @"%USERPROFILE%\Documents"));

        RegistrySnapshot snapshot = store.Snapshot(new[]
        {
            new RegistryTarget(SubKey, "Personal"),
            new RegistryTarget(SubKey, "Desktop"),
        });

        string? path = store.SaveSnapshot(sandbox.Root, snapshot);
        Assert.NotNull(path);
        Assert.True(File.Exists(path!));
        Assert.Empty(Directory.GetFiles(sandbox.Root, "*.tmp"));

        RegistrySnapshot loaded = RegistryStore.LoadSnapshot(path!);
        Assert.Equal(snapshot.Entries.Count, loaded.Entries.Count);

        RegistryEntry personal = Assert.Single(loaded.Entries, e => e.Name == "Personal");
        Assert.True(personal.Existed);
        Assert.Equal(@"%USERPROFILE%\Documents", personal.Data);
        Assert.Equal(RegistryValueKind.ExpandString, personal.Kind);

        RegistryEntry desktop = Assert.Single(loaded.Entries, e => e.Name == "Desktop");
        Assert.False(desktop.Existed);
    }

    [Fact]
    public void SaveSnapshot_TwiceWithinSameSecond_NeitherSnapshotOverwritesTheOther()
    {
        // 同一秒内保存两份快照（rename 通道的单项事务可达毫秒级）不得互相覆盖：
        // 覆盖后意图日志里指向同一文件的多个项只剩最后一份的数据，
        // 跨启动回滚时更早那项的原注册表值就还原不出来了。
        using var sandbox = new TempDirectory("snapshot-same-second");
        RegistryStore store = NewStore(out _, seed: s => s
            .Add(SubKey, "Personal", @"%USERPROFILE%\Documents"));

        // 预先占据"本秒"的文件名：无论测试跑得多快，第一份 SaveSnapshot 都会撞上已存在的
        // 同名文件 → 必须走"递增序号"分支，而不是静默覆盖它。（若秒恰在两步之间翻转，
        // 新名字天然不冲突，后续断言同样成立——不依赖时钟精度，不会 flaky。）
        string occupiedPath = Path.Combine(
            sandbox.Root, $"registry-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(occupiedPath, "occupied");

        RegistrySnapshot first = store.Snapshot(new[] { new RegistryTarget(SubKey, "Personal") });
        string? firstPath = store.SaveSnapshot(sandbox.Root, first);
        Assert.NotNull(firstPath);

        // 第二份快照内容不同（Desktop 未播种 → 不存在），两次调用背靠背落盘。
        RegistrySnapshot second = store.Snapshot(new[] { new RegistryTarget(SubKey, "Desktop") });
        string? secondPath = store.SaveSnapshot(sandbox.Root, second);
        Assert.NotNull(secondPath);

        Assert.NotEqual(firstPath, secondPath);
        Assert.True(File.Exists(firstPath!));
        Assert.True(File.Exists(secondPath!));
        // 既有文件不被覆盖：先占位的那份凭据保持原样。
        Assert.Equal("occupied", File.ReadAllText(occupiedPath));
        Assert.Empty(Directory.GetFiles(sandbox.Root, "*.tmp"));

        // 各自还原各自的内容：第一份只有 Personal，第二份只有 Desktop——谁也没覆盖谁。
        RegistrySnapshot loadedFirst = RegistryStore.LoadSnapshot(firstPath!);
        RegistrySnapshot loadedSecond = RegistryStore.LoadSnapshot(secondPath!);

        Assert.Single(loadedFirst.Entries, e => e.Name == "Personal");
        Assert.Single(loadedSecond.Entries, e => e.Name == "Desktop");
    }

    [Fact]
    public void SaveSnapshot_ReturnsNullInsteadOfThrowingWhenDirectoryIsUnusable()
    {
        using var sandbox = new TempDirectory("snapshot-bad");
        string filePath = sandbox.CreateFile("not-a-directory.txt");

        var log = new FakeLog();
        var store = new RegistryStore(new InMemoryRegistryBackend(), log);
        RegistrySnapshot snapshot = store.Snapshot(Array.Empty<RegistryTarget>());

        // 落盘失败不阻断搬运：必须返回 null 并留日志，而不是抛异常中断整个事务。
        string? path = store.SaveSnapshot(filePath, snapshot);

        Assert.Null(path);
        Assert.NotEmpty(log.Errors);
    }

    [Fact]
    public void LoadSnapshot_ThrowsInvalidDataForCorruptedJson()
    {
        using var sandbox = new TempDirectory("snapshot-corrupt");
        string path = sandbox.CreateFile("registry-broken.json", "{\"createdAt\":\"2025-01-01T00:00:00Z\",\"entries\":[");

        Assert.Throws<InvalidDataException>(() => RegistryStore.LoadSnapshot(path));
    }

    [Fact]
    public void LoadSnapshot_ThrowsInvalidDataWhenEntriesMissing()
    {
        using var sandbox = new TempDirectory("snapshot-shape");
        string path = sandbox.CreateFile("registry-shape.json", "{\"createdAt\":\"2025-01-01T00:00:00Z\"}");

        Assert.Throws<InvalidDataException>(() => RegistryStore.LoadSnapshot(path));
    }

    // ================================================================ 辅助

    private static RegistryStore NewStore(
        out InMemoryRegistryBackend backend,
        Func<RegistrySeed, RegistrySeed>? seed = null)
    {
        RegistrySeed builder = seed?.Invoke(new RegistrySeed()) ?? new RegistrySeed();
        backend = builder.ToBackend();
        return new RegistryStore(backend, new FakeLog());
    }

    /// <summary>
    /// 可注入读失败的装饰器：对指定值名抛 <see cref="RegistryReadException"/>。
    /// </summary>
    private sealed class ThrowingRegistryBackend : IRegistryBackend
    {
        private readonly bool _failEnumerate;

        public ThrowingRegistryBackend(IEnumerable<string> failingNames, bool failEnumerate = false)
        {
            _failEnumerate = failEnumerate;
            FailReadsFor = new HashSet<string>(failingNames, StringComparer.OrdinalIgnoreCase);
        }

        public InMemoryRegistryBackend Inner { get; } = new();

        public ISet<string> FailReadsFor { get; }

        public RegistryValue? ReadValue(string subKey, string name)
        {
            if (FailReadsFor.Contains(name))
            {
                throw new RegistryReadException($"注入的读取失败：{subKey}\\{name}");
            }

            return Inner.ReadValue(subKey, name);
        }

        public RegistryValue? ReadHklmValue(string subKey, string name)
        {
            if (FailReadsFor.Contains(name))
            {
                throw new RegistryReadException($"注入的读取失败（HKLM）：{subKey}\\{name}");
            }

            return Inner.ReadHklmValue(subKey, name);
        }

        public void WriteValue(string subKey, string name, object data, RegistryValueKind kind) =>
            Inner.WriteValue(subKey, name, data, kind);

        public void DeleteValue(string subKey, string name) => Inner.DeleteValue(subKey, name);

        public IReadOnlyDictionary<string, RegistryValue> EnumerateValues(string subKey)
        {
            if (_failEnumerate)
            {
                throw new RegistryReadException($"注入的枚举失败：{subKey}");
            }

            return Inner.EnumerateValues(subKey);
        }
    }
}
