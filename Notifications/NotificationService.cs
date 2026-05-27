namespace Fx.ControlKit.Notifications;

/// <summary>
/// Centralized notification service for displaying messages to users.
/// Replaces scattered JSRuntime.InvokeVoidAsync("alert", ...) calls.
/// Scoped per-circuit (each user session gets its own instance).
/// </summary>
public sealed class NotificationService
{
    private readonly List<Notification> _notifications = new();
    private readonly object _lock = new();

    /// <summary>Raised when a notification is added or dismissed.</summary>
    public event Action? OnChange;

    /// <summary>Current active notifications (newest first).</summary>
    public IReadOnlyList<Notification> Notifications
    {
        get
        {
            lock (_lock)
                return _notifications.ToList();
        }
    }

    /// <summary>Show an error notification (red).</summary>
    public void Error(string message, string? detail = null, int durationMs = 0)
        => Add(NotificationLevel.Error, message, detail, durationMs);

    /// <summary>Show a warning notification (yellow).</summary>
    public void Warning(string message, string? detail = null, int durationMs = 8000)
        => Add(NotificationLevel.Warning, message, detail, durationMs);

    /// <summary>Show a success notification (green).</summary>
    public void Success(string message, string? detail = null, int durationMs = 4000)
        => Add(NotificationLevel.Success, message, detail, durationMs);

    /// <summary>Show an info notification (blue).</summary>
    public void Info(string message, string? detail = null, int durationMs = 5000)
        => Add(NotificationLevel.Info, message, detail, durationMs);

    /// <summary>Dismiss a specific notification.</summary>
    public void Dismiss(Guid id)
    {
        lock (_lock)
            _notifications.RemoveAll(n => n.Id == id);
        OnChange?.Invoke();
    }

    /// <summary>Clear all notifications.</summary>
    public void Clear()
    {
        lock (_lock)
            _notifications.Clear();
        OnChange?.Invoke();
    }

    private void Add(NotificationLevel level, string message, string? detail, int durationMs)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Level = level,
            Message = message,
            Detail = detail,
            Timestamp = DateTime.Now,
            DurationMs = durationMs
        };

        lock (_lock)
        {
            _notifications.Insert(0, notification);
            if (_notifications.Count > 20)
                _notifications.RemoveRange(20, _notifications.Count - 20);
        }

        OnChange?.Invoke();

        if (durationMs > 0)
        {
            _ = Task.Delay(durationMs).ContinueWith(_ => Dismiss(notification.Id));
        }
    }
}

/// <summary>Notification severity level.</summary>
public enum NotificationLevel
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>A single notification message.</summary>
public sealed class Notification
{
    public Guid Id { get; init; }
    public NotificationLevel Level { get; init; }
    public required string Message { get; init; }
    public string? Detail { get; init; }
    public DateTime Timestamp { get; init; }
    public int DurationMs { get; init; }
}
