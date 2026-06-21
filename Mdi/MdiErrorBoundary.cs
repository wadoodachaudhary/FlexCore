using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Mdi;

public class MdiErrorBoundary : ErrorBoundary
{
    [Parameter] public EventCallback<Exception> OnError { get; set; }

    protected override async Task OnErrorAsync(Exception exception)
    {
        await base.OnErrorAsync(exception);

        if (OnError.HasDelegate)
            await OnError.InvokeAsync(exception);
    }
}
