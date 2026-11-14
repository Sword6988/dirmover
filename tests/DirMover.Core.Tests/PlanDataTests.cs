using DirMover.Core.Engine;
using DirMover.Core.IO;
using DirMover.Core.Registry;

namespace DirMover.Core.Tests;

/// <summary>T9：引擎数据契约（<see cref="PlanItem.BlockingReason"/> 顺序、摘要、节流）。</summary>
public sealed class PlanDataTests
{
    private static FolderSpec Spec => Catalog.ByKey("documents")!;

    private static PlanItem Item(string current, string target) => new()
    {
        Spec = Spec,
        Current = current,
        Target = target,
        RegistryPayload = target,
        RegistryTargets = new[] { new RegistryTarget(Catalog.UserShellFoldersSubKey, Spec.ValueName) },
    };

    [Fact]
    public void BlockingReason_Same_IsNotBlocking()
    {
        PlanItem item = Item(@"C:\Users\me\Documents", @"C:\Users\me\Documents");
        item.Same = true;

        Assert.Equal(string.Empty, item.BlockingReason());
    }

    [Fact]
    public void BlockingReason_EmptyCurrent_Blocks()
    {
        PlanItem item = Item(string.Empty, @"D:\UserData\Documents");

        Assert.Contains("无法确定当前位置", item.BlockingReason());
    }

    [Fact]
    public void BlockingReason_MissingCurrent_Blocks()
    {
        using var sandbox = new TempDirectory("plan-missing");
        PlanItem item = Item(sandbox.Combine("gone"), sandbox.Combine("dst"));

        Assert.Contains("当前位置不存在", item.BlockingReason());
    }

    [Fact]
    public void BlockingReason_InvalidReason_WinsOverSpaceGate()
    {
        using var sandbox = new TempDirectory("plan-invalid");
        string current = sandbox.CreateDirectoryPath("src");
        PlanItem item = Item(current, sandbox.Combine("dst"));
        item.InvalidReason = "禁区内";

        Assert.Equal("禁区内", item.BlockingReason());
    }

    [Fact]
    public void BlockingReason_UnreadableItems_Blocks()
    {
        using var sandbox = new TempDirectory("plan-unreadable");
        string current = sandbox.CreateDirectoryPath("src");
        PlanItem item = Item(current, sandbox.Combine("dst"));
        item.Stat = new TreeStat { Unreadable = 2 };

        Assert.Contains("无法读取", item.BlockingReason());
    }

    [Fact]
    public void BlockingReason_SameVolumeRename_SkipsSpaceGateEntirely()
    {
        using var sandbox = new TempDirectory("plan-rename");
        string current = sandbox.CreateDirectoryPath("src");
        PlanItem item = Item(current, sandbox.Combine("dst", "Documents"));
        // 体积故意设成"任何磁盘都装不下"，同卷 rename 仍然必须放行。
        item.Stat = new TreeStat { Bytes = long.MaxValue / 4 };
        item.TargetExists = false;

        Assert.Equal(string.Empty, item.BlockingReason());
    }

    [Fact]
    public void BlockingReason_NotEnoughSpace_BlocksWithDetail()
    {
        using var sandbox = new TempDirectory("plan-space");
        string current = sandbox.CreateDirectoryPath("src");
        string target = sandbox.CreateDirectoryPath("dst");
        PlanItem item = Item(current, target);
        // 目标已存在 ⇒ 走复制合并 ⇒ 必须过空间闸门。
        item.TargetExists = true;
        item.Stat = new TreeStat { Bytes = long.MaxValue / 4 };

        string reason = item.BlockingReason();

        Assert.Contains("空间不足", reason);
    }

    [Fact]
    public void BlockingReason_SamePhysicalLocation_IsAllowed()
    {
        using var sandbox = new TempDirectory("plan-same-physical");
        string real = sandbox.CreateDirectoryPath("real");
        string link = sandbox.Combine("link");
        if (!TestEnv.CanCreateJunction || !TestEnv.TryCreateJunction(link, real))
        {
            return;
        }

        PlanItem item = Item(link, real);

        Assert.Equal(string.Empty, item.BlockingReason());
    }

    [Fact]
    public void BlockingReason_RegistryOnly_IgnoresMissingSource()
    {
        using var sandbox = new TempDirectory("plan-registry-only");
        PlanItem item = Item(sandbox.Combine("gone"), sandbox.Combine("dst"));
        item.RegistryOnly = true;

        Assert.Equal(string.Empty, item.BlockingReason());
    }

    [Fact]
    public void NeedsMove_RequiresBothPathsAndDifferentLocation()
    {
        Assert.False(Item(string.Empty, @"D:\x").NeedsMove);
        Assert.True(Item(@"C:\a", @"D:\b").NeedsMove);

        PlanItem same = Item(@"C:\a", @"C:\a");
        same.Same = true;
        Assert.False(same.NeedsMove);
    }

    [Fact]
    public void Describe_FollowsFiveBranches()
    {
        PlanItem same = Item(@"C:\a", @"C:\a");
        same.Same = true;
        Assert.Equal(UiText.StatusSame, same.Describe());

        PlanItem registryOnly = Item(@"C:\a", @"C:\b");
        registryOnly.RegistryOnly = true;
        Assert.Equal(UiText.StatusRegistryOnly, registryOnly.Describe());

        PlanItem pending = Item(@"C:\a", @"D:\b");
        Assert.Equal(UiText.StatusPending, pending.Describe());

        PlanItem scanned = Item(@"C:\a", @"D:\b");
        scanned.Scanned = true;
        scanned.Stat = new TreeStat { Files = 3, Bytes = 8 };
        Assert.Equal("待转移，3 个文件 / 8 B", scanned.Describe());

        PlanItem merge = Item(@"C:\a", @"D:\b");
        merge.Scanned = true;
        merge.Merge = true;
        merge.Stat = new TreeStat { Files = 3, Bytes = 8 };
        Assert.Equal("将合并（3 个文件 / 8 B）", merge.Describe());
    }

    [Fact]
    public void ThrottledProgress_MergesWithinWindow_ButAlwaysPassesFinal()
    {
        var inner = new RecordingProgress();
        var throttled = new ThrottledTransferProgress(inner, TimeSpan.FromMilliseconds(100));

        throttled.Report(new TransferProgressInfo("k", "d", 1, 10, 100, "a"));
        throttled.Report(new TransferProgressInfo("k", "d", 2, 10, 200, "a"));
        throttled.Report(new TransferProgressInfo("k", "d", 5, 10, 500, "a"));
        throttled.Report(new TransferProgressInfo("k", "d", 10, 10, 1000, "a"));

        Assert.Equal(2, inner.Reports.Count);
        Assert.Equal(10, inner.Reports[^1].DoneInItem);
    }

    [Fact]
    public void ProgressScope_InjectsKeyAndDisplay()
    {
        var inner = new RecordingProgress();
        ITransferProgress progress = new ItemProgressScope(inner, "documents", "我的文档");

        progress.Report(new TransferProgressInfo(string.Empty, string.Empty, 1, 2, 10, "a.txt"));

        Assert.Single(inner.Reports);
        Assert.Equal("documents", inner.Reports[0].ItemKey);
        Assert.Equal("我的文档", inner.Reports[0].Display);
        Assert.Equal("a.txt", inner.Reports[0].CurrentRelativePath);
    }

    [Fact]
    public void ProgressBridge_ForwardsToIProgress()
    {
        var inner = new RecordingProgress();
        ITransferProgress bridge = new TransferProgressBridge(new SyncProgress(inner));

        bridge.Report(new TransferProgressInfo("key", "我的文档", 1, 2, 10, "a.txt"));

        Assert.Single(inner.Reports);
        Assert.Equal("key", inner.Reports[0].ItemKey);
    }

    [Fact]
    public void TransferReport_Summary_OmitsZeroSectionsAndJoinsNotes()
    {
        var report = new TransferReport();
        report.Outcomes.Add(Outcome(ItemStatus.Ok));
        report.Outcomes.Add(Outcome(ItemStatus.Ok));

        Assert.Equal("完成 2 项", report.Summary());
        Assert.True(report.Ok);

        report.Outcomes.Add(Outcome(ItemStatus.Failed));
        report.Outcomes.Add(Outcome(ItemStatus.Skipped));
        report.Cancelled = true;
        report.Notes.Add("注册表备份未能落盘，本次无法一键回滚");
        report.Notes.Add("另一条提示");

        Assert.Equal(
            "完成 2 项，失败 1 项，跳过 1 项，用户取消（注册表备份未能落盘，本次无法一键回滚；另一条提示）",
            report.Summary());
        Assert.False(report.Ok);
        Assert.Single(report.Failed);
        Assert.Equal(2, report.Succeeded.Count);
    }

    [Fact]
    public void ItemOutcome_Label_AndStage_AreExposed()
    {
        var outcome = new ItemOutcome
        {
            Key = "documents",
            Display = "我的文档",
            Status = ItemStatus.Failed,
            Stage = TransferStage.RegistryFailed,
        };

        Assert.Equal("失败", outcome.Label);
        Assert.Equal(TransferStage.RegistryFailed, outcome.Stage);
    }

    private static ItemOutcome Outcome(ItemStatus status) => new()
    {
        Key = Guid.NewGuid().ToString("N"),
        Display = "项",
        Status = status,
    };

    private sealed class SyncProgress : IProgress<TransferProgressInfo>
    {
        private readonly RecordingProgress _inner;

        public SyncProgress(RecordingProgress inner) => _inner = inner;

        public void Report(TransferProgressInfo value) => _inner.Reports.Add(value);
    }
}
