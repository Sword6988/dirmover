using System.Reflection;
using DirMover.Core.Engine;

namespace DirMover.Core.Tests;

/// <summary>
/// 文案契约（PRD §6）与静态真源（PRD §2）的逐条断言。
/// </summary>
/// <remarks>
/// 这些用例的价值在于"改文案会红"：状态列的 5 个分支、摘要的省略规则、11 种阶段标签
/// 都是产品契约，不是实现细节。没有断言时，任何人为了"读起来更顺"改一句就会被放行。
/// </remarks>
public sealed class UiTextTests
{
    // ================================================================ 静态真源（7 项规格）

    [Fact]
    public void Catalog_KeepsSevenSpecsInSpecOrder()
    {
        Assert.Equal(7, Catalog.All.Count);
        Assert.Equal(
            new[] { "documents", "favorites", "pictures", "music", "videos", "downloads", "desktop" },
            Catalog.AllKeys.ToArray());
    }

    [Theory]
    [InlineData("documents", "我的文档", "Personal", @"%USERPROFILE%\Documents")]
    [InlineData("favorites", "收藏夹", "Favorites", @"%USERPROFILE%\Favorites")]
    [InlineData("pictures", "我的图片", "My Pictures", @"%USERPROFILE%\Pictures")]
    [InlineData("music", "我的音乐", "My Music", @"%USERPROFILE%\Music")]
    [InlineData("videos", "我的视频", "My Video", @"%USERPROFILE%\Videos")]
    [InlineData("desktop", "桌面", "Desktop", @"%USERPROFILE%\Desktop")]
    public void Catalog_TraditionalValueNamesMatchSpec(string key, string display, string valueName, string defaultPath)
    {
        FolderSpec? spec = Catalog.ByKey(key);
        Assert.NotNull(spec);
        Assert.Equal(display, spec!.Display);
        Assert.Equal(valueName, spec.ValueName);
        Assert.Equal(defaultPath, spec.DefaultPath);
        Assert.Single(spec.Aliases);
        Assert.StartsWith("{", spec.Aliases[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_DownloadsUsesGuidAsAuthoritativeValueNameAndADifferentGuidAsAlias()
    {
        FolderSpec? spec = Catalog.ByKey("downloads");
        Assert.NotNull(spec);

        // downloads 的权威值名**本身**就是 GUID，且不存在传统值名（如 "Downloads"）。
        Assert.Equal("{374DE290-123F-4565-9164-39C4925E467B}", spec!.ValueName);
        Assert.Equal(@"%USERPROFILE%\Downloads", spec.DefaultPath);

        // 别名是**另一个** GUID：把两者混为一谈会让"资源管理器显示 A、系统实际认 B"。
        Assert.Single(spec.Aliases);
        Assert.Equal("{7D83EE9B-2244-4E70-B1F5-5393042AF1E4}", spec.Aliases[0]);
        Assert.NotEqual(spec.ValueName, spec.Aliases[0]);
    }

    [Fact]
    public void Catalog_UnknownKeyReturnsNullWithoutThrowing()
    {
        Assert.Null(Catalog.ByKey("cookies"));
        Assert.Null(Catalog.ByKey(string.Empty));
    }

    [Fact]
    public void Catalog_DefaultPathsKeepExpandableVariableForm()
    {
        // 恢复默认值时必须把 %USERPROFILE% 原样写回注册表（与系统原生写法一致），
        // 因此 DefaultPath 不允许是已展开的绝对路径。
        foreach (FolderSpec spec in Catalog.All)
        {
            Assert.StartsWith("%USERPROFILE%\\", spec.DefaultPath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Catalog_ForbiddenListsFollowCurrentSystemDrive()
    {
        string original = Catalog.SystemDrive;
        try
        {
            Catalog.SystemDrive = "D:";

            // 断言的是"随系统盘现算"这条性质：写死 C: 会让改过盘符的机器上闸门 fail-open。
            Assert.Contains(@"D:\Windows", Catalog.ForbiddenTargetRoots());
            Assert.Contains(@"D:\Program Files", Catalog.ForbiddenTargetRoots());
            Assert.Contains(@"D:\Users", Catalog.ForbiddenTargetRoots());
            Assert.DoesNotContain(@"C:\Windows", Catalog.ForbiddenTargetRoots());

            // 源禁区的"子树"部分不包含 \Users（用户把资料放在 C:\Users\me\... 是合法状态）。
            Assert.DoesNotContain(@"D:\Users", Catalog.ForbiddenSourceTrees());
            Assert.Contains(@"D:\Users", Catalog.ForbiddenSourceExact());
        }
        finally
        {
            Catalog.SystemDrive = original;
        }
    }

    // ================================================================ 状态列（PRD §6.1，5 个分支）

    [Fact]
    public void DescribePlan_SamePathWinsOverEverything()
    {
        // 2026-11-14 文案缩短（用户反馈：状态列连续 7 行重复整句太啰嗦），
        // 与 PRD §6.1 的表格同步更新；分支优先级断言本身不变。
        Assert.Equal("无需转移", UiText.DescribePlan(
            same: true, registryOnly: true, merge: true, scanned: true, files: 9, bytes: 4096));
    }

    [Fact]
    public void DescribePlan_RegistryOnlyWhenSourceMissing()
    {
        Assert.Equal("仅更新注册表", UiText.DescribePlan(
            same: false, registryOnly: true, merge: false, scanned: false, files: 0, bytes: 0));
    }

    [Fact]
    public void DescribePlan_MergeBranchShowsZeroScaleWhenNotScanned()
    {
        // 未扫描时规模显示 0 个文件 / 0 B 是原实现的既有行为，重写不得"顺手修正"。
        Assert.Equal("将合并（0 个文件 / 0 B）", UiText.DescribePlan(
            same: false, registryOnly: false, merge: true, scanned: false, files: 0, bytes: 0));
        Assert.Equal("将合并（3 个文件 / 1.5 KB）", UiText.DescribePlan(
            same: false, registryOnly: false, merge: true, scanned: false, files: 3, bytes: 1536));
    }

    [Fact]
    public void DescribePlan_NotScannedIsPlainPending()
    {
        Assert.Equal("待转移", UiText.DescribePlan(
            same: false, registryOnly: false, merge: false, scanned: false, files: 0, bytes: 0));
    }

    [Fact]
    public void DescribePlan_ScannedShowsScale()
    {
        Assert.Equal("待转移，3 个文件 / 1.5 KB", UiText.DescribePlan(
            same: false, registryOnly: false, merge: false, scanned: true, files: 3, bytes: 1536));
    }

    // ================================================================ 摘要与状态栏

    [Fact]
    public void SelectionSummary_CountsAndZeroSelectionHint()
    {
        Assert.Equal("已选择 7 / 7 项", UiText.SelectionSummary(7, 7));
        Assert.Equal("已选择 3 / 7 项", UiText.SelectionSummary(3, 7));
        Assert.Equal("已选择 0 / 7 项（请先勾选项目）", UiText.SelectionSummary(0, 7));
    }

    [Fact]
    public void SelectionSummaryDetail_SplitsActionableFromNotNeeded()
    {
        // 全部无需转移：不该出现"已选择 7 / 7 项"这种让人以为有事可做的说法。
        Assert.Equal("全部 7 项无需转移", UiText.SelectionSummaryDetail(0, 0, 7));
        // 全部可转移：沿用旧格式，不额外啰嗦"无需转移 0 项"。
        Assert.Equal("已选择 5 / 5 项", UiText.SelectionSummaryDetail(5, 5, 0));
        // 混合：两个数字都要出现。
        Assert.Equal("可转移 2 / 3 项 · 无需转移 4 项", UiText.SelectionSummaryDetail(2, 3, 4));
        // 混合且零选：追加提示。
        Assert.Equal("可转移 0 / 3 项 · 无需转移 4 项（请先勾选项目）", UiText.SelectionSummaryDetail(0, 3, 4));
    }

    [Fact]
    public void Processing_UsesDisplayNameAndProgress()
    {
        Assert.Equal("正在处理 我的文档（2/7）", UiText.Processing("我的文档", 2, 7));
    }

    [Fact]
    public void Summarize_OmitsZeroSegmentsEntirely()
    {
        Assert.Equal("完成 7 项", UiText.Summarize(7, 0, 0, cancelled: false));
    }

    [Fact]
    public void Summarize_AddsFailureSkipAndCancelSegments()
    {
        Assert.Equal("完成 5 项，失败 1 项", UiText.Summarize(5, 1, 0, cancelled: false));
        Assert.Equal("完成 5 项，跳过 2 项", UiText.Summarize(5, 0, 2, cancelled: false));
        Assert.Equal("完成 4 项，失败 1 项，跳过 1 项，用户取消", UiText.Summarize(4, 1, 1, cancelled: true));
    }

    [Fact]
    public void Summarize_AppendsNotesInChineseParentheses()
    {
        string text = UiText.Summarize(3, 0, 0, cancelled: false, new[] { UiText.SnapshotNotSaved });
        Assert.Equal("完成 3 项（注册表备份未能落盘，本次无法一键回滚）", text);
        Assert.Contains(UiText.SnapshotNotSaved, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarize_JoinsMultipleNotesWithSemicolon()
    {
        Assert.Equal(
            "完成 1 项（甲；乙）",
            UiText.Summarize(1, 0, 0, cancelled: false, new[] { "甲", "乙" }));
    }

    // ================================================================ 阶段标签（11 种，PRD §4.3-3）

    [Fact]
    public void StageLabel_CoversAllElevenStagesWithFixedText()
    {
        var expected = new Dictionary<TransferStage, string>
        {
            [TransferStage.Pending] = "未开始",
            [TransferStage.Copying] = "正在复制（目标可能只写入一部分）",
            [TransferStage.Copied] = "复制校验完成，源目录未删除",
            [TransferStage.RegistryWritten] = "注册表已更新，源目录未删除",
            [TransferStage.SourceRemoved] = "源目录已删除",
            [TransferStage.Cancelled] = "已取消",
            [TransferStage.CopyFailed] = "复制失败",
            [TransferStage.VerifyFailed] = "复制校验未通过",
            [TransferStage.RegistryFailed] = "注册表写入失败",
            [TransferStage.SourceRemovalFailed] = "源目录删除失败",
            [TransferStage.UnexpectedError] = "发生未预期错误",
        };

        Assert.Equal(11, expected.Count);
        Assert.Equal(11, Enum.GetValues<TransferStage>().Length);

        foreach ((TransferStage stage, string label) in expected)
        {
            Assert.Equal(label, UiText.StageLabel(stage));
        }

        Assert.Equal(11, expected.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void StatusLabel_MapsAllItemStatuses()
    {
        Assert.Equal("完成", UiText.StatusLabel(ItemStatus.Ok));
        Assert.Equal("失败", UiText.StatusLabel(ItemStatus.Failed));
        Assert.Equal("跳过", UiText.StatusLabel(ItemStatus.Skipped));
        Assert.Equal("已取消", UiText.StatusLabel(ItemStatus.Cancelled));
        Assert.Equal("待处理", UiText.StatusLabel(ItemStatus.Pending));
    }

    [Fact]
    public void ResultTitle_CoversFourOutcomes()
    {
        Assert.Equal("操作完成", UiText.ResultTitle(failed: false, cancelled: false, anyOk: true));
        Assert.Equal("部分项目未完成", UiText.ResultTitle(failed: true, cancelled: false, anyOk: true));
        Assert.Equal("已停止", UiText.ResultTitle(failed: false, cancelled: true, anyOk: false));
        Assert.Equal("执行出错", UiText.ResultTitle(failed: true, cancelled: false, anyOk: false));
    }

    // ================================================================ 失败文案必须给出下一步（PRD §6.4）

    [Fact]
    public void FailureTexts_AlwaysStateWhatToDoNext()
    {
        Assert.Equal(
            @"复制失败：磁盘已满；源目录已保留，目标副本位于 D:\UserData\me\Documents，修正后重试即可",
            UiText.CopyFailed("磁盘已满", @"D:\UserData\me\Documents"));

        Assert.Equal(
            "复制校验未通过，已保留源目录（摘要不一致 x3）",
            UiText.VerifyFailed("摘要不一致 x3"));

        Assert.Equal(
            @"写入注册表失败 Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders\Personal：拒绝访问；已回滚本项 2 处注册表值",
            UiText.RegistryWriteFailed(
                Catalog.UserShellFoldersSubKey, "Personal", "拒绝访问", 2));

        Assert.Contains("重启本机后手动删除即可", UiText.SourceRemoveFailed("拒绝访问", @"D:\old"), StringComparison.Ordinal);
        Assert.Contains("无法读取目标磁盘可用空间", UiText.SpaceUnknown, StringComparison.Ordinal);
        Assert.Contains("空间不足", UiText.SpaceInsufficient("10.0 GB", "10.5 GB", "2.0 GB"), StringComparison.Ordinal);
        Assert.Contains("源目录未改动", UiText.TargetIsLink(@"D:\UserData"), StringComparison.Ordinal);
        Assert.Contains("保留源目录", UiText.SamePhysicalLocation(@"C:\a", @"D:\b"), StringComparison.Ordinal);
        Assert.Contains("保留源目录", UiText.SourceChangedDuringCopy(1, 2, 3, @"C:\a", @"D:\b"), StringComparison.Ordinal);
        Assert.Contains("请勿重复搬迁", UiText.DataAtTargetNoRegistryChange(@"D:\b"), StringComparison.Ordinal);
        Assert.Contains("改回", UiText.MovedBackToCurrent(@"D:\b", @"C:\a"), StringComparison.Ordinal);
        Assert.Contains("[试运行]", UiText.DryRunOutcome("已规划"), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmTransferBody_ContainsCountTargetRootAndSafetyNote()
    {
        string text = UiText.ConfirmTransferBody(3, null, @"D:\UserData");
        Assert.Contains("即将转移 3 项", text, StringComparison.Ordinal);
        Assert.Contains(UiText.ScaleUnavailable, text, StringComparison.Ordinal);
        Assert.Contains(@"目标根目录：D:\UserData", text, StringComparison.Ordinal);
        Assert.Contains("保留源文件", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmRestoreBody_ListsEveryDisplayName()
    {
        string text = UiText.ConfirmRestoreBody(new[] { "我的文档", "桌面" });
        Assert.Contains("· 我的文档", text, StringComparison.Ordinal);
        Assert.Contains("· 桌面", text, StringComparison.Ordinal);
        Assert.Contains("不会删源", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmExitBody_DistinguishesCancelableTask()
    {
        Assert.Contains("建议先点主按钮上的「停止」", UiText.ConfirmExitBody(true), StringComparison.Ordinal);
        Assert.Contains("没有停止入口", UiText.ConfirmExitBody(false), StringComparison.Ordinal);
    }

    [Fact]
    public void RollbackResult_ReportsFourCounters()
    {
        string text = UiText.RollbackResult(2, 1, 3, 4);
        Assert.Contains("恢复 2 处", text, StringComparison.Ordinal);
        Assert.Contains("删除 1 处", text, StringComparison.Ordinal);
        Assert.Contains("已跳过 3 处属于已完成项目", text, StringComparison.Ordinal);
        Assert.Contains("因原值未知跳过 4 处", text, StringComparison.Ordinal);

        // 计数为 0 的段落整段省略（避免"已跳过 0 处"这种噪音）。
        string minimal = UiText.RollbackResult(1, 0, 0, 0);
        Assert.Equal("恢复 1 处 / 删除 0 处", minimal);
    }

    [Fact]
    public void UnknownReason_ComesFromUiTextAndRendersInsideFailureText()
    {
        // 审计 A-14：`FirstError` 的兜底文案曾硬编码在 TransferEngine 里（"未知原因"），
        // 属于"文案唯一出口"的结构性漏网。这条用例钉住三件事：
        // ① 文案确实由 UiText 提供；② 不是空串（空串会让"复制失败："后面什么都没有）；
        // ③ 它落在既有失败文案句子里时读得通，不会产生"复制失败：；"这种空括号形态。
        Assert.Equal("未知原因", UiText.UnknownReason);
        Assert.False(string.IsNullOrWhiteSpace(UiText.UnknownReason));

        string text = UiText.CopyFailed(UiText.UnknownReason, @"D:\UserData\me\Documents");
        Assert.Contains("复制失败：未知原因", text, StringComparison.Ordinal);
        Assert.DoesNotContain("复制失败：；", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetBecameLinkBeforeRemove_StatesSourceKeptAndRegistryWritten()
    {
        // 审计 A-11 的加固分支必须"说清两件事"：源还在（数据安全）、注册表已经写过（半完成状态）。
        // 少了任何一句，用户都会误判该不该手动处理。
        string text = UiText.TargetBecameLinkBeforeRemove(@"D:\UserData\me\Documents", @"C:\Users\me\Documents");
        Assert.Contains("保留源目录", text, StringComparison.Ordinal);
        Assert.Contains("注册表已指向", text, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\me\Documents", text, StringComparison.Ordinal);
        Assert.NotEqual(UiText.TargetIsLink(@"D:\UserData"), text);
    }

    [Fact]
    public void UnexpectedErrorTexts_AreNonEmptyAndMentionExitCodeFour()
    {
        // 审计 A-08：未预期错误的告知文案也走 UiText（App 层不得出现中文字面量）。
        // 正文必须提到"再次出现会直接退出并返回退出码 4"，否则用户不知道该期待什么行为。
        Assert.False(string.IsNullOrWhiteSpace(UiText.DialogUnexpectedError));
        Assert.Contains("退出码 4", UiText.UnexpectedErrorBody, StringComparison.Ordinal);
        Assert.Contains("已写入启动错误文件", UiText.UnexpectedErrorBody, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureListTruncated_StatesTotalAndShown()
    {
        Assert.Equal("（共 5 项，以下仅列前 3 条）", UiText.FailureListTruncated(5, 3));
    }

    // ================================================================ 文案出口的完整性

    [Fact]
    public void AllPublicTextConstantsAreNonEmpty()
    {
        FieldInfo[] fields = typeof(UiText)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToArray();

        Assert.NotEmpty(fields);
        foreach (FieldInfo field in fields)
        {
            string value = (string)field.GetRawConstantValue()!;
            Assert.False(string.IsNullOrWhiteSpace(value), $"{field.Name} 不应为空");
        }
    }

    [Fact]
    public void ColumnHeadersLiveInUiText()
    {
        // 列标题同样属于面向用户的文案，必须来自 UiText（架构 §6.5）。
        Assert.Equal(UiText.ColumnName, DirMover.Core.Layout.ColumnFitCalculator.Columns[0].Header);
        Assert.Equal(UiText.ColumnCurrent, DirMover.Core.Layout.ColumnFitCalculator.Columns[1].Header);
        Assert.Equal(UiText.ColumnTarget, DirMover.Core.Layout.ColumnFitCalculator.Columns[2].Header);
        Assert.Equal(UiText.ColumnStatus, DirMover.Core.Layout.ColumnFitCalculator.Columns[3].Header);
    }
}
