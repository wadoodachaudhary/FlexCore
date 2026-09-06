namespace Fx.ControlKit;
public enum PopupPlacement { BottomStart, BottomEnd, TopStart, TopEnd, Left, Right }
public sealed record PopupAnchorContext(bool IsOpen, string PopupId, Func<Task> OpenAsync, Func<Task> CloseAsync, Func<Task> ToggleAsync);
