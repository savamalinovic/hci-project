using System.Windows.Input;

namespace PaperlessDesktop.ViewModels;

internal sealed class AsyncCommand(Func<bool> canExecute, Func<Task> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    public async void Execute(object? parameter)
    {
        if (canExecute())
        {
            await execute();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
