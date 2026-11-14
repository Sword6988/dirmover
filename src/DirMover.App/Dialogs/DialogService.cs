using DirMover.App.ViewModels;
using DirMover.Core;
using DirMover.Core.Diagnostics;
using DirMover.Core.Engine;
using DirMover.Core.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DirMover.App.Dialogs;

/// <summary>
/// 全部弹窗的唯一出口（架构 §7.5）。
/// </summary>
/// <remarks>
/// 三条硬约束，缺一条都会在实机上出问题：
/// <list type="number">
/// <item><b>串行化</b>：WinUI 同一时刻只允许一个 <see cref="ContentDialog"/>，第二个直接抛
/// <c>Only a single ContentDialog can be open at any time</c>。用 <see cref="SemaphoreSlim"/> 排队，
/// 而不是靠调用方自觉——弹窗是异步的，"同时"很容易在进度回调里凑出来。</item>
/// <item><b><c>XamlRoot</c> 缺失时返回而不是抛</b>：<c>--scan</c> / <c>--version</c> 可能在窗口
/// <c>Loaded</c> 之前就要弹窗。抛出去会把整个进程带崩，用户只看到闪退且没有日志。</item>
/// <item><b>零文案</b>：本文件除注释外不出现中文字面量，标题/正文/按钮一律取自
/// <see cref="UiText"/> 或调用方传入。文案一旦在这里落字，改一句话就要改两个工程。</item>
/// </list>
/// </remarks>
internal sealed class DialogService : IDialogService
{
    /// <summary>失败清单最多逐项列几条（超过则只给计数，避免弹窗撑满屏幕）。</summary>
    private const int MaxFailureLines = 3;

    private readonly Window _window;
    private readonly IAppPaths _paths;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>构造。</summary>
    /// <param name="window">宿主窗口（用于取 <c>XamlRoot</c>）。</param>
    /// <param name="paths">落点（「打开日志目录」需要）。</param>
    /// <param name="log">日志。</param>
    internal DialogService(Window window, IAppPaths paths, IAppLog log)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public Task InfoAsync(string title, string message) =>
        ShowSingleAsync(title, message, UiText.ButtonOk);

    /// <inheritdoc />
    public Task SuccessAsync(string title, string message) =>
        ShowSingleAsync(title, message, UiText.ButtonOk);

    /// <inheritdoc />
    public Task WarningAsync(string title, string message) =>
        ShowSingleAsync(title, message, UiText.ButtonOk);

    /// <inheritdoc />
    public Task ErrorAsync(string title, string message) =>
        ShowSingleAsync(title, message, UiText.ButtonOk);

    /// <inheritdoc />
    public async Task<bool> ConfirmTransferAsync(int count, string? scaleSummary, string targetRoot)
    {
        string body = UiText.ConfirmTransferBody(count, scaleSummary, targetRoot);
        return await ShowYesNoAsync(UiText.DialogConfirmTransfer, body);
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmRestoreAsync(IReadOnlyList<string> displays)
    {
        string body = UiText.ConfirmRestoreBody(displays ?? Array.Empty<string>());
        return await ShowYesNoAsync(UiText.DialogConfirmRestore, body);
    }

    /// <inheritdoc />
    public async Task<bool> AskRestartShellAsync() =>
        await ShowYesNoAsync(UiText.DialogRestartShell, UiText.AskRestartShellBody);

    /// <inheritdoc />
    public Task InfoNoMoveAsync() => ShowSingleAsync(UiText.DialogNoMove, UiText.NoMoveBody, UiText.ButtonOk);

    /// <inheritdoc />
    public async Task ResultAsync(TransferReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        bool failed = report.Failed.Count > 0;
        bool anyOk = report.Succeeded.Count > 0;
        string title = UiText.ResultTitle(failed, report.Cancelled, anyOk);
        string body = report.Summary();

        if (failed)
        {
            body = body + Environment.NewLine + Environment.NewLine + BuildFailureList(report.Failed);
        }

        // 失败清单单独成段并压危险色：摘要与原因在视觉上分层，用户一眼看到"哪里错了"。
        await ShowSingleAsync(title, body, UiText.ButtonOk, highlightLastParagraph: failed);
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmExitWhileBusyAsync(bool cancelable) =>
        await ShowYesNoAsync(UiText.DialogBusy, UiText.ConfirmExitBody(cancelable));

    /// <inheritdoc />
    public async Task<PendingChoice> AskPendingIntentAsync(IReadOnlyList<PendingIntent> items)
    {
        var lines = new List<string>();
        if (items is not null)
        {
            foreach (PendingIntent item in items)
            {
                lines.Add(UiText.PendingIntentLine(item.Display, item.Stage, item.Source, item.Target));
            }
        }

        var host = DialogLayout.CreateBody(UiText.PendingIntentBody(lines));
        var dialog = new ContentDialog
        {
            Title = UiText.DialogPendingIntent,
            Content = host,

            // 三选语义固定：是=回滚，否=忽略，关闭键/ESC=取消（PRD §4.3）。
            PrimaryButtonText = UiText.ButtonYes,
            SecondaryButtonText = UiText.ButtonNo,
            CloseButtonText = UiText.ButtonCancel,
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult? result = await ShowAsync(dialog, host);
        return result switch
        {
            ContentDialogResult.Primary => PendingChoice.Rollback,
            ContentDialogResult.Secondary => PendingChoice.Ignore,
            _ => PendingChoice.Keep,
        };
    }

    /// <inheritdoc />
    public Task WarningPendingUnreadableAsync(string reason) =>
        ShowSingleAsync(UiText.DialogPendingUnreadable, reason, UiText.ButtonOk);

    /// <inheritdoc />
    public Task InfoElevationAsync(string message) =>
        ShowSingleAsync(UiText.DialogElevation, message, UiText.ButtonOk);

    /// <inheritdoc />
    public Task WarningAlreadyRunningAsync() =>
        ShowSingleAsync(UiText.DialogAlreadyRunning, UiText.AlreadyRunningBody, UiText.ButtonOk);

    /// <inheritdoc />
    public async Task ShowRollbackResultAsync(string message, bool ok)
    {
        string title = ok ? UiText.DialogRollbackOk : UiText.DialogRollbackFailed;
        await ShowSingleAsync(title, message, UiText.ButtonOk);
    }

    /// <inheritdoc />
    public async Task ShowTextAsync(string title, string body, bool showOpenLogButton)
    {
        // 只读文本弹窗：--scan / --version / --help 共用（裁决 Q1）。
        // 报告是列对齐文本，必须等宽 + 不折行（折行即毁对齐），纵横两个方向都能滚动；
        // 外层浅灰卡片把它与"人写的正文"在视觉上分开（见 DialogLayout.CreateMonoBody）。
        var host = DialogLayout.CreateMonoBody(body);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = host,

            // 只读弹窗不放 SecondaryButton：多一个"否"会让人以为这是个需要决策的问题。
            CloseButtonText = UiText.ButtonOk,
            DefaultButton = ContentDialogButton.Close,
        };

        if (showOpenLogButton)
        {
            dialog.PrimaryButtonText = UiText.ButtonOpenLog;
        }

        ContentDialogResult? result = await ShowAsync(dialog, host);
        if (result == ContentDialogResult.Primary && showOpenLogButton)
        {
            ShellOpResult opened = ShellIntegration.OpenDirectory(_paths.LogDirectory);
            if (!opened.Ok)
            {
                // 打开日志目录失败不该再弹一个弹窗（会把用户困在弹窗套弹窗里），只记日志。
                _log.Warn(opened.Message);
            }
        }
    }

    private async Task ShowSingleAsync(string title, string message, string closeText, bool highlightLastParagraph = false)
    {
        var host = DialogLayout.CreateBody(message, highlightLastParagraph);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = host,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
        };

        await ShowAsync(dialog, host);
    }

    private async Task<bool> ShowYesNoAsync(string title, string body)
    {
        var host = DialogLayout.CreateBody(body);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = host,
            PrimaryButtonText = UiText.ButtonYes,
            CloseButtonText = UiText.ButtonNo,
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult? result = await ShowAsync(dialog, host);
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 串行化 + <c>XamlRoot</c> 注入 + 尺寸自适应 的唯一入口。
    /// </summary>
    /// <param name="dialog">待显示的弹窗。</param>
    /// <param name="host">正文容器（用于按窗口尺寸收紧上限，避免小窗口下顶破弹窗）。</param>
    /// <returns>用户的选择；<c>null</c> 表示无法显示（已记日志）。</returns>
    private async Task<ContentDialogResult?> ShowAsync(ContentDialog dialog, FrameworkElement host)
    {
        XamlRoot? root = _window.Content?.XamlRoot;
        if (root is null)
        {
            // 窗口还没 Loaded。这里必须"静默放弃"而不是抛：--scan 会在窗口显示前就要弹窗，
            // 抛出去等于闪退，而且用户连日志都拿不到一份完整说明。
            _log.Error("弹窗被跳过：XamlRoot 尚未就绪", null);
            return null;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            dialog.XamlRoot = root;

            // 尺寸自适应：窗口被拉小（最小 780×640）时压低正文上限，让弹窗始终完整可见。
            DialogLayout.FitToWindow(host, root.Size);

            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // 弹窗失败（例如另一个 ContentDialog 抢占了 XamlRoot）不能连累业务流。
            _log.Error("弹窗显示失败", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string BuildFailureList(IReadOnlyList<ItemOutcome> failed)
    {
        if (failed.Count == 0)
        {
            return UiText.FailureListEmpty;
        }

        var builder = new System.Text.StringBuilder();
        int shown = Math.Min(failed.Count, MaxFailureLines);
        for (int i = 0; i < shown; i++)
        {
            ItemOutcome outcome = failed[i];
            if (i > 0)
            {
                builder.Append(Environment.NewLine);
            }

            // 分隔符用 ASCII 冒号：本文件不得出现中文字面量，中文只允许来自 UiText。
            builder.Append(outcome.Display).Append(": ").Append(outcome.Message);
        }

        if (failed.Count > shown)
        {
            builder.Append(Environment.NewLine).Append(UiText.FailureListTruncated(failed.Count, shown));
        }

        return builder.ToString();
    }
}
