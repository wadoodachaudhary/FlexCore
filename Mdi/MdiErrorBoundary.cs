using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Mdi;

/// <summary>
/// An <see cref="ErrorBoundary"/> that additionally raises <see cref="OnError"/> when
/// it catches an exception. The base behaviour is preserved — the exception is logged
/// via <c>IErrorBoundaryLogger</c> and the boundary switches to rendering its
/// <c>ErrorContent</c> in place of the failed subtree, which keeps the Blazor Server
/// circuit alive (the rest of the app stays usable).
///
/// The extra <see cref="OnError"/> hook lets the host react — e.g. clear a persisted
/// "re-open this page on next login" route so a screen that throws on render isn't
/// restored straight back into the same exception, locking the user out.
/// </summary>
public class MdiErrorBoundary : ErrorBoundary
{
    /// <summary>Raised after an exception is caught (and after the base logger runs).</summary>
    [Parameter] public EventCallback<Exception> OnError { get; set; }

    protected override async Task OnErrorAsync(Exception exception)
    {
        // Preserve the default logging behaviour.
        await base.OnErrorAsync(exception);

        if (OnError.HasDelegate)
            await OnError.InvokeAsync(exception);
    }
}
