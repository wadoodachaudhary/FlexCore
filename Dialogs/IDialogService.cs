namespace Fx.ControlKit.Dialogs;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string message, string title = "Confirm");

    Task<MessageBoxResult> ConfirmCancelAsync(string message, string title = "Confirm");

    Task AlertAsync(string message, string title = "");

    Task<string?> PromptAsync(string message, string title = "", string defaultValue = "");
}
