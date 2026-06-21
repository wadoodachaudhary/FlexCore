namespace Fx.ControlKit.Dialogs;

public sealed class DialogService : IDialogService
{
    private Func<string, string, MessageBoxButtons, Task<MessageBoxResult>>? _showMessage;
    private Func<string, string, string, Task<string?>>? _showInput;

    public void RegisterHost(
        Func<string, string, MessageBoxButtons, Task<MessageBoxResult>> showMessage,
        Func<string, string, string, Task<string?>> showInput)
    {
        _showMessage = showMessage;
        _showInput = showInput;
    }

    public async Task<bool> ConfirmAsync(string message, string title = "Confirm")
    {
        if (_showMessage is null) return false;
        var result = await _showMessage(message, string.IsNullOrWhiteSpace(title) ? "Confirm" : title, MessageBoxButtons.YesNo);
        return result == MessageBoxResult.Yes;
    }

    public Task<MessageBoxResult> ConfirmCancelAsync(string message, string title = "Confirm")
        => _showMessage?.Invoke(message, string.IsNullOrWhiteSpace(title) ? "Confirm" : title, MessageBoxButtons.YesNoCancel)
           ?? Task.FromResult(MessageBoxResult.Cancel);

    public async Task AlertAsync(string message, string title = "")
    {
        if (_showMessage is not null)
            await _showMessage(message, string.IsNullOrWhiteSpace(title) ? "Message" : title, MessageBoxButtons.Ok);
    }

    public Task<string?> PromptAsync(string message, string title = "", string defaultValue = "")
        => _showInput?.Invoke(message, string.IsNullOrWhiteSpace(title) ? "Input" : title, defaultValue ?? "")
           ?? Task.FromResult<string?>(null);
}
