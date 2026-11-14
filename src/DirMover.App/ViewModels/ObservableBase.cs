using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DirMover.App.ViewModels;

/// <summary>
/// 最小 MVVM 基类（不引 CommunityToolkit：本项目只要三个能力，多一个包就多一条供应链）。
/// </summary>
/// <remarks>
/// 绑定契约（架构 §7.3 硬前提 3）要求所有 <c>TwoWay</c> 源属性严格走"比较 → 赋值 → 通知"。
/// 之所以把这套顺序固化进 <see cref="SetProperty{T}"/> 而不是让每个属性的 setter 自己写：
/// 漏掉"比较"会在每次刷新时发一堆无变化通知，<c>GridLength</c> 这类值类型列宽会因此抖动；
/// 漏掉"赋值后再通知"会让绑定读到旧值，表现为"点了不回写"。
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>发出一条属性变更通知。</summary>
    /// <param name="propertyName">属性名（默认取调用方成员名）。</param>
    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>一次性发出多条通知（用于"一次改动影响多个派生属性"的场景）。</summary>
    /// <param name="propertyNames">属性名列表。</param>
    protected void RaiseProperties(params string[] propertyNames)
    {
        if (propertyNames is null)
        {
            return;
        }

        foreach (string name in propertyNames)
        {
            RaisePropertyChanged(name);
        }
    }

    /// <summary>
    /// 三步式赋值：比较 → 赋值 → 通知。值未变化时返回 <c>false</c> 且不发通知。
    /// </summary>
    /// <typeparam name="T">字段类型。</typeparam>
    /// <param name="field">字段引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名（默认取调用方成员名）。</param>
    /// <returns>是否真的发生了变化。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }
}

/// <summary>同步命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>构造。</summary>
    /// <param name="execute">执行体。</param>
    /// <param name="canExecute">可用性判定（为空表示恒可用）。</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute is null || _canExecute();

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute();

    /// <summary>手动触发可用性重算（WinUI 不会自动重查）。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>带参数的同步命令（行级命令用：空格/回车切换某一行）。</summary>
/// <typeparam name="T">参数类型。</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    /// <summary>构造。</summary>
    /// <param name="execute">执行体。</param>
    /// <param name="canExecute">可用性判定（为空表示恒可用）。</param>
    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) =>
        parameter is T typed ? _canExecute is null || _canExecute(typed) : false;

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (parameter is T typed)
        {
            _execute(typed);
        }
    }

    /// <summary>手动触发可用性重算。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 异步命令。
/// </summary>
/// <remarks>
/// <see cref="ICommand.Execute"/> 的签名是 <c>void</c>，所以 <c>async void</c> 无法避免；
/// 一旦执行体抛异常就会直达 <c>AppDomain</c> 未处理异常处理器并让进程静默消失。
/// 因此这里统一 <c>try/catch</c> 并交给 <c>onError</c>：异常必须变成一条日志 + 一个提示框，
/// 而不是"点一下程序没了"。
/// </remarks>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    /// <summary>构造。</summary>
    /// <param name="execute">执行体。</param>
    /// <param name="canExecute">可用性判定。</param>
    /// <param name="onError">异常处理（为空则吞掉，仅靠命令执行方自行记录）。</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>是否正在执行（用于防重入；<c>finally</c> 中复位）。</summary>
    public bool IsExecuting => _running;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && (_canExecute is null || _canExecute());

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _ = RunAsync();
    }

    /// <summary>手动触发可用性重算。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync()
    {
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }
}
