namespace Fx.ControlKit.Dialogs;

/// <summary>
/// App-wide modal dialog service — the FlexKit replacement for the browser's
/// native <c>window.confirm</c> / <c>alert</c> / <c>prompt</c> (JS interop).
/// A single <see cref="DialogHostControl"/>, hosted once (e.g. in MainLayout),
/// renders the underlying <see cref="MessageBoxControl"/> + <see cref="InputDialogControl"/>
/// and registers them with the service, so any component can inject
/// <see cref="IDialogService"/> and <c>await</c> a result. Scoped per-circuit,
/// mirroring <c>NotificationService</c>.
/// </summary>
public interface IDialogService
{
    /// <summary>Yes/No confirmation (VB6 MsgBox vbYesNo). Returns true for Yes.</summary>
    Task<bool> ConfirmAsync(string message, string title = "Confirm");

    /// <summary>Yes/No/Cancel confirmation (VB6 MsgBox vbYesNoCancel).</summary>
    Task<MessageBoxResult> ConfirmCancelAsync(string message, string title = "Confirm");

    /// <summary>Single-OK informational alert (VB6 MsgBox vbInformation/vbOKOnly).</summary>
    Task AlertAsync(string message, string title = "");

    /// <summary>Single-line text input (VB6 InputBox). Returns null when cancelled.</summary>
    Task<string?> PromptAsync(string message, string title = "", string defaultValue = "");
}
