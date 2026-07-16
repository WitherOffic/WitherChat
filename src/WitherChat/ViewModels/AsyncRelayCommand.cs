using System.Windows.Input;
using WitherChat.Services;
using WitherChat.Views;

namespace WitherChat.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private Task _executionTask = Task.CompletedTask;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public Task ExecutionTask => Volatile.Read(ref _executionTask);

    public bool CanExecute(object? parameter)
    {
        return !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        var execution = ExecuteCoreAsync(parameter);
        _ = Interlocked.Exchange(ref _executionTask, execution);
        try
        {
            await execution.ConfigureAwait(true);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _executionTask, Task.CompletedTask, execution);
        }
    }

    private async Task ExecuteCoreAsync(object? parameter)
    {
        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected part of closing the application or
            // replacing an in-flight operation. It must not surface as an error.
        }
        catch (Exception ex)
        {
            new FileLogger().Error("Async command failed", ex);
            var language = new SettingsService().Load().Language;
            SilentDialog.ShowMessage(
                LocalizationService.Get(language, "Error"),
                LocalizationService.Get(language, "UnexpectedError"));
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
