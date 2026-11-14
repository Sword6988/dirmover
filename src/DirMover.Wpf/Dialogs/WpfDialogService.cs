using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DirMover.App.ViewModels;
using DirMover.Core;
using DirMover.Core.Diagnostics;
using DirMover.Core.Engine;
using DirMover.Core.Shell;

namespace DirMover.Wpf.Dialogs;

/// <summary>
/// 全部弹窗的唯一出口（WPF 版）。
/// </summary>
/// <remarks>
/// 三条硬约束与 WinUI 版一致：
/// <list type="number">
/// <item><b>串行化</b>：同一时刻只允许一个模态弹窗，第二个排队等——弹窗是异步的，
/// "同时"很容易在进度回调里凑出来。</item>
/// <item><b>宿主不可用时返回而不是抛</b>：<c>--scan</c> 可能在窗口显示前就要弹窗，
/// 抛出去会把整个进程带崩，用户只看到闪退且没有日志。</item>
/// <item><b>零文案</b>：标题/正文/按钮一律取自 <see cref="UiText"/> 或调用方传入。</item>
/// </list>
/// 线程模型：所有 <c>ShowDialog</c> 都排到 UI 线程执行，调用方（可能在工作线程）直接 await；
/// 不用同步阻塞，避免把正在跑转移的后台流程卡死在弹窗上。
/// </remarks>
internal sealed class WpfDialogService : IDialogService
{
    /// <summary>失败清单最多逐项列几条（超过则只给计数）。</summary>
    private const int MaxFailureLines = 3;

    private readonly Window _owner;
    private readonly IAppPaths _paths;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>构造。</summary>
    /// <param name="owner">宿主窗口（弹窗的 Owner，决定居中位置与层级）。</param>
    /// <param name="paths">落点（「打开日志目录」需要）。</param>
    /// <param name="log">日志。</param>
    internal WpfDialogService(Window owner, IAppPaths paths, IAppLog log)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
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
        if (report is null)
        {
            throw new ArgumentNullException(nameof(report));
        }

        bool failed = report.Failed.Count > 0;
        bool anyOk = report.Succeeded.Count > 0;
        string title = UiText.ResultTitle(failed, report.Cancelled, anyOk);
        string body = report.Summary();

        if (failed)
        {
            body = body + Environment.NewLine + Environment.NewLine + BuildFailureList(report.Failed);
        }

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

        StackPanel host = DialogLayout.CreateBody(UiText.PendingIntentBody(lines));

        // 三选语义固定：是=回滚，否=忽略，关闭/ESC=取消。
        DialogButton result = await ShowAsync(
            UiText.DialogPendingIntent,
            host,
            primaryText: UiText.ButtonYes,
            secondaryText: UiText.ButtonNo,
            closeText: UiText.ButtonCancel,
            highlightLastParagraph: false);

        return result switch
        {
            DialogButton.Primary => PendingChoice.Rollback,
            DialogButton.Secondary => PendingChoice.Ignore,
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
        Border host = DialogLayout.CreateMonoBody(body, DialogLayout.PreferredWidth());

        DialogButton result = await ShowAsync(
            title,
            host,
            primaryText: showOpenLogButton ? UiText.ButtonOpenLog : null,
            secondaryText: null,
            closeText: UiText.ButtonOk,
            highlightLastParagraph: false);

        if (result == DialogButton.Primary && showOpenLogButton)
        {
            ShellOpResult opened = ShellIntegration.OpenDirectory(_paths.LogDirectory);
            if (!opened.Ok)
            {
                // 打开失败不再弹第二个弹窗（会把用户困在弹窗套弹窗里），只记日志。
                _log.Warn(opened.Message);
            }
        }
    }

    private Task ShowSingleAsync(string title, string message, string closeText, bool highlightLastParagraph = false)
    {
        StackPanel host = DialogLayout.CreateBody(message, highlightLastParagraph);
        return ShowAsync(title, host, null, null, closeText, highlightLastParagraph);
    }

    private async Task<bool> ShowYesNoAsync(string title, string body)
    {
        StackPanel host = DialogLayout.CreateBody(body);
        DialogButton result = await ShowAsync(title, host, UiText.ButtonYes, null, UiText.ButtonNo, false);
        return result == DialogButton.Primary;
    }

    /// <summary>串行化 + 排到 UI 线程 + 尺寸自适应的唯一入口。</summary>
    private async Task<DialogButton> ShowAsync(
        string title,
        UIElement body,
        string? primaryText,
        string? secondaryText,
        string? closeText,
        bool highlightLastParagraph)
    {
        if (!_owner.IsLoaded || _owner.Dispatcher.HasShutdownStarted)
        {
            // 窗口还没显示或已关闭：静默放弃（调用方按"用户未选择"处理），并留日志。
            _log.Error($"弹窗被跳过：宿主窗口未就绪（{title}）", null);
            return DialogButton.None;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            var completion = new TaskCompletionSource<DialogButton>();

            // 刻意的 fire-and-forget：真实完成信号走 completion（TaskCompletionSource），
            // 在下方 await。丢弃返回值消除 CS4014。
            _ = _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    DialogLayout.FitToWindow(body as FrameworkElement ?? new StackPanel(), _owner.Content is FrameworkElement content
                        ? new Size(content.ActualWidth, content.ActualHeight)
                        : new Size(_owner.Width, _owner.Height));

                    var dialog = new DialogWindow
                    {
                        Owner = _owner,
                    };

                    dialog.Configure(
                        title,
                        body,
                        primaryText,
                        secondaryText,
                        closeText,
                        DialogLayout.PreferredWidth());

                    dialog.ShowDialog();
                    completion.TrySetResult(dialog.Result);
                }
                catch (Exception ex)
                {
                    _log.Error("弹窗显示失败", ex);
                    completion.TrySetResult(DialogButton.None);
                }
            }));

            return await completion.Task;
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

            builder.Append(outcome.Display).Append(": ").Append(outcome.Message);
        }

        if (failed.Count > shown)
        {
            builder.Append(Environment.NewLine).Append(UiText.FailureListTruncated(failed.Count, shown));
        }

        return builder.ToString();
    }
}
