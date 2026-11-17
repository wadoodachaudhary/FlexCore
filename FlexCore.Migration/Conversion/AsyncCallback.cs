namespace Fx.ControlKit.Conversion;

/// <summary>
/// Asynchronous operation implemented by the host. FlexCore awaits
/// <see cref="InvokeAsync"/> and applies the returned value.
/// </summary>
/// <typeparam name="TArgument">Value FlexCore passes in.</typeparam>
/// <typeparam name="TResult">Value the host returns.</typeparam>
public interface IAsyncCallback<in TArgument, TResult>
{
    /// <summary>Runs the host operation.</summary>
    Task<TResult> InvokeAsync(TArgument argument, CancellationToken cancellationToken);
}

/// <summary>Adapters that turn a delegate into an <see cref="IAsyncCallback{TArgument, TResult}"/>.</summary>
public static class HostCallback
{
    /// <summary>Wraps a callback that accepts a cancellation token.</summary>
    public static IAsyncCallback<TArgument, TResult> Create<TArgument, TResult>(
        Func<TArgument, CancellationToken, Task<TResult>> callback)
        => new DelegateAsyncCallback<TArgument, TResult>(callback ?? throw new ArgumentNullException(nameof(callback)));

    /// <summary>Wraps a callback that does not use the cancellation token.</summary>
    public static IAsyncCallback<TArgument, TResult> Create<TArgument, TResult>(
        Func<TArgument, Task<TResult>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new DelegateAsyncCallback<TArgument, TResult>((argument, _) => callback(argument));
    }
}

/// <summary>Delegate-backed <see cref="IAsyncCallback{TArgument, TResult}"/>.</summary>
public sealed class DelegateAsyncCallback<TArgument, TResult> : IAsyncCallback<TArgument, TResult>
{
    private readonly Func<TArgument, CancellationToken, Task<TResult>> _callback;

    public DelegateAsyncCallback(Func<TArgument, CancellationToken, Task<TResult>> callback)
        => _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public Task<TResult> InvokeAsync(TArgument argument, CancellationToken cancellationToken)
        => _callback(argument, cancellationToken);
}
