using System.Text.Json;
using DirMover.Core.Engine;
using DirMover.Core.Registry;

namespace DirMover.Core.Tests;

/// <summary>T10：意图日志（原子写、残留读取、损坏可感知、注销规则、快照裁剪）。</summary>
public sealed class IntentJournalTests
{
    private static PlanItem Item(string key, string valueName, string current, string target) => new()
    {
        Spec = Catalog.ByKey(key)!,
        Current = current,
        Target = target,
        RegistryPayload = target,
        RegistryTargets = new[]
        {
            new RegistryTarget(Catalog.UserShellFoldersSubKey, valueName),
            new RegistryTarget(Catalog.ShellFoldersSubKey, valueName),
        },
    };

    private static string JournalPath(string backupDir) => Path.Combine(backupDir, IntentJournal.FileName);

    [Fact]
    public void Begin_WritesValidJson_AndLeavesNoTempFile()
    {
        using var sandbox = new TempDirectory("journal-begin");
        string backup = sandbox.CreateDirectoryPath("backups");
        var log = new FakeLog();
        IIntentJournal journal = IntentJournal.Open(backup, enabled: true, log);

        journal.Begin(new[] { Item("documents", "Personal", @"C:\src", @"D:\dst") }, snapshotPath: null);

        Assert.True(File.Exists(JournalPath(backup)));
        Assert.False(File.Exists(Path.Combine(backup, IntentJournal.TempFileName)));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(JournalPath(backup)));
        Assert.Equal(1, document.RootElement.GetProperty("Items").GetArrayLength());
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void ReadPending_NoFile_IsClean()
    {
        using var sandbox = new TempDirectory("journal-none");
        string backup = sandbox.CreateDirectoryPath("backups");

        PendingIntents pending = IntentJournal.ReadPending(backup);

        Assert.Empty(pending.Items);
        Assert.Null(pending.ReadError);
        Assert.True(pending.Clean);
    }

    [Fact]
    public void ReadPending_HalfWrittenJson_ReportsErrorInsteadOfLookingClean()
    {
        using var sandbox = new TempDirectory("journal-broken");
        string backup = sandbox.CreateDirectoryPath("backups");
        File.WriteAllText(JournalPath(backup), "{ \"Items\": [ { \"Key\": \"docum");

        PendingIntents pending = IntentJournal.ReadPending(backup);

        Assert.Empty(pending.Items);
        Assert.False(string.IsNullOrWhiteSpace(pending.ReadError));
        Assert.False(pending.Clean);
    }

    [Fact]
    public void CloseRun_RemovesPendingAndCancelled()
    {
        using var sandbox = new TempDirectory("journal-closerun");
        string backup = sandbox.CreateDirectoryPath("backups");
        IIntentJournal journal = IntentJournal.Open(backup, enabled: true, new FakeLog());

        PlanItem first = Item("documents", "Personal", @"C:\src", @"D:\dst");
        PlanItem second = Item("desktop", "Desktop", @"C:\desk", @"D:\desk");
        journal.Begin(new[] { first, second }, null);
        journal.Mark(second, TransferStage.Cancelled);

        journal.CloseRun();

        PendingIntents pending = IntentJournal.ReadPending(backup);
        Assert.Empty(pending.Items);
        Assert.Null(pending.ReadError);
        Assert.False(File.Exists(JournalPath(backup)));
    }

    [Fact]
    public void Finish_UnregistersItem_AndDeletesFileWhenLast()
    {
        using var sandbox = new TempDirectory("journal-finish");
        string backup = sandbox.CreateDirectoryPath("backups");
        IIntentJournal journal = IntentJournal.Open(backup, enabled: true, new FakeLog());

        PlanItem item = Item("documents", "Personal", @"C:\src", @"D:\dst");
        journal.Begin(new[] { item }, null);
        journal.Mark(item, TransferStage.SourceRemoved);
        journal.Finish(item);

        Assert.False(File.Exists(JournalPath(backup)));
        Assert.Empty(IntentJournal.ReadPending(backup).Items);
    }

    [Fact]
    public void CloseRun_KeepsHalfFinishedSourceRemovalFailure()
    {
        using var sandbox = new TempDirectory("journal-half");
        string backup = sandbox.CreateDirectoryPath("backups");
        IIntentJournal journal = IntentJournal.Open(backup, enabled: true, new FakeLog());

        PlanItem item = Item("documents", "Personal", @"C:\src", @"D:\dst");
        journal.Begin(new[] { item }, null);
        journal.Mark(item, TransferStage.SourceRemovalFailed);

        journal.CloseRun();

        PendingIntents pending = IntentJournal.ReadPending(backup);
        Assert.Single(pending.Items);
        Assert.Equal(TransferStage.SourceRemovalFailed, pending.Items[0].Stage);
    }

    [Fact]
    public void UnfinishedRegistryTargets_ExcludesSourceRemoved()
    {
        using var sandbox = new TempDirectory("journal-unfinished");
        string backup = sandbox.CreateDirectoryPath("backups");
        IIntentJournal journal = IntentJournal.Open(backup, enabled: true, new FakeLog());

        PlanItem done = Item("documents", "Personal", @"C:\src", @"D:\dst");
        PlanItem half = Item("desktop", "Desktop", @"C:\desk", @"D:\desk");
        journal.Begin(new[] { done, half }, null);
        journal.Mark(done, TransferStage.SourceRemoved);
        journal.Mark(half, TransferStage.Copied);

        ISet<RegistryTarget> targets = IntentJournal.UnfinishedRegistryTargets(IntentJournal.ReadPending(backup));

        Assert.DoesNotContain(targets, target => target.Name == "Personal");
        Assert.Contains(targets, target => target.Name == "Desktop");
    }

    [Fact]
    public void ReadPending_LegacyEntryWithoutRegistry_IsMarkedUnknownAndNotTrimmable()
    {
        using var sandbox = new TempDirectory("journal-legacy");
        string backup = sandbox.CreateDirectoryPath("backups");
        File.WriteAllText(
            JournalPath(backup),
            """
            {
              "CreatedAt": "2024-01-01T00:00:00Z",
              "Items": [
                {
                  "Key": "documents",
                  "Display": "我的文档",
                  "Source": "C:\\src",
                  "Target": "D:\\dst",
                  "Stage": "Copied",
                  "Snapshot": ""
                }
              ]
            }
            """);

        PendingIntents pending = IntentJournal.ReadPending(backup);

        Assert.Null(pending.ReadError);
        Assert.Single(pending.Items);
        Assert.False(pending.Items[0].RegistryKnown);
        Assert.Empty(IntentJournal.UnfinishedRegistryTargets(pending));
    }

    [Fact]
    public void ReadPending_UnknownStage_IsTreatedAsCorruption()
    {
        using var sandbox = new TempDirectory("journal-badstage");
        string backup = sandbox.CreateDirectoryPath("backups");
        File.WriteAllText(
            JournalPath(backup),
            """{ "Items": [ { "Key": "documents", "Target": "D:\\dst", "Stage": "Nonsense" } ] }""");

        PendingIntents pending = IntentJournal.ReadPending(backup);

        Assert.False(string.IsNullOrWhiteSpace(pending.ReadError));
    }

    [Fact]
    public void SnapshotTrimmer_RestrictsCaseInsensitively_AndCountsExcluded()
    {
        var snapshot = new RegistrySnapshot(
            new[]
            {
                new RegistryEntry(Catalog.UserShellFoldersSubKey, "Personal", @"C:\old", Microsoft.Win32.RegistryValueKind.ExpandString, true, false),
                new RegistryEntry(Catalog.UserShellFoldersSubKey, "Desktop", @"C:\desk", Microsoft.Win32.RegistryValueKind.ExpandString, true, false),
                new RegistryEntry(Catalog.ShellFoldersSubKey, "Personal", @"C:\old", Microsoft.Win32.RegistryValueKind.String, true, false),
            },
            "2024-01-01T00:00:00Z");

        (RegistrySnapshot trimmed, int excluded) = SnapshotTrimmer.Restrict(
            snapshot,
            new[] { new RegistryTarget(Catalog.UserShellFoldersSubKey.ToUpperInvariant(), "personal") });

        Assert.Equal("Personal", Assert.Single(trimmed.Entries).Name);
        Assert.Equal(2, excluded);
    }

    [Fact]
    public void Begin_WhenBackupDirectoryIsNotWritable_DisablesJournalWithoutThrowing()
    {
        using var sandbox = new TempDirectory("journal-readonly");
        string blocker = sandbox.CreateFile("blocked", "not a directory");
        var log = new FakeLog();
        IIntentJournal journal = IntentJournal.Open(blocker, enabled: true, log);

        journal.Begin(new[] { Item("documents", "Personal", @"C:\src", @"D:\dst") }, null);
        journal.Mark(Item("documents", "Personal", @"C:\src", @"D:\dst"), TransferStage.Copying);
        journal.CloseRun();

        Assert.NotEmpty(log.Errors);
    }

    [Fact]
    public void Open_Disabled_IsNoOp()
    {
        using var sandbox = new TempDirectory("journal-disabled");
        string backup = sandbox.CreateDirectoryPath("backups");
        IIntentJournal journal = IntentJournal.Open(backup, enabled: false, new FakeLog());

        journal.Begin(new[] { Item("documents", "Personal", @"C:\src", @"D:\dst") }, null);

        Assert.False(File.Exists(JournalPath(backup)));
    }
}
