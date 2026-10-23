using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Transcriptor.App.ViewModels;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public event EventHandler? CanExecuteChanged;
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; Refresh();
        try { await execute(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { onError(ex); }
        finally { _running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class Command(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public void Execute(object? parameter) => execute();
}
