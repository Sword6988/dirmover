using System;
using System.Windows;
using System.Windows.Threading;

// 这个命名空间是刻意"冒名"的：MainViewModel 是 WinUI 与 WPF 两版共用的源文件，
// 它对 UI 线程的全部诉求只有"把回调排到 UI 线程"与"一个可重启的一次性定时器"。
// 与其为了 net48 复制一份 1400 行的 ViewModel，不如在这里提供同形的最小实现——
// 两版共享同一份业务代码，行为差异只会出现在这两处薄封装上。
// 注意：这里不引用任何 Microsoft.UI 程序集，纯 WPF 实现。
namespace Microsoft.UI.Dispatching;

/// <summary>UI 线程调度器（WPF 实现）。</summary>
public sealed class DispatcherQueue
{
    private readonly Dispatcher _dispatcher;

    private DispatcherQueue(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>取当前线程的调度器；当前线程不是 UI 线程时返回 null（与 WinUI 语义一致）。</summary>
    /// <remarks>
    /// 不能直接返回 <c>new DispatcherQueue(Dispatcher.CurrentDispatcher)</c>：
    /// WPF 的 <c>CurrentDispatcher</c> 在**任何**线程上都会凭空造一个 Dispatcher，
    /// 而那个线程没人泵消息——排进去的回调永远不会执行，且判空分支（<c>_dispatcher is null</c>）
    /// 永远走不到。WinUI 里工作线程拿到的就是 null，这里必须照做，
    /// 否则"后台线程改 UI"会从"静默跳过"退化成"静默丢失回调、界面卡在半路"。
    /// </remarks>
    public static DispatcherQueue? GetForCurrentThread()
    {
        Dispatcher? current = Dispatcher.FromThread(System.Threading.Thread.CurrentThread);
        if (current is null)
        {
            return null;
        }

        Dispatcher? ui = Application.Current?.Dispatcher;
        if (ui is null)
        {
            // 还没有 Application（单测 / 极早期）：沿用旧行为，交给调用方自己保证线程正确。
            return new DispatcherQueue(current);
        }

        return ReferenceEquals(current, ui) ? new DispatcherQueue(current) : null;
    }

    /// <summary>把回调排到 UI 线程；线程已关闭时返回 false（与 WinUI 语义一致）。</summary>
    public bool TryEnqueue(Action callback)
    {
        if (callback is null)
        {
            return false;
        }

        try
        {
            if (_dispatcher.HasShutdownFinished || _dispatcher.HasShutdownStarted)
            {
                return false;
            }

            _dispatcher.BeginInvoke(callback, DispatcherPriority.Normal);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.Threading.Tasks.TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>创建一个由本调度器驱动的定时器。</summary>
    public DispatcherQueueTimer CreateTimer() => new DispatcherQueueTimer(_dispatcher);
}

/// <summary>UI 线程定时器（WPF <see cref="DispatcherTimer"/> 的薄封装）。</summary>
public sealed class DispatcherQueueTimer
{
    private readonly DispatcherTimer _timer;
    private bool _repeating = true;

    internal DispatcherQueueTimer(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _timer.Tick += (_, _) =>
        {
            if (!_repeating)
            {
                _timer.Stop();
            }

            Tick?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>触发间隔。</summary>
    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    /// <summary>是否重复触发（false 时每次 <see cref="Start"/> 只触发一次）。</summary>
    public bool IsRepeating
    {
        get => _repeating;
        set => _repeating = value;
    }

    /// <summary>到时回调。</summary>
    public event Action<DispatcherQueueTimer, object?>? Tick;

    /// <summary>开始计时（每次调用都从零计时，与 WinUI 的 Start 一致）。</summary>
    public void Start() => _timer.Start();

    /// <summary>停止计时。</summary>
    public void Stop() => _timer.Stop();
}
