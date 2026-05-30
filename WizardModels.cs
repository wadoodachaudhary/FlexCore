using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit;

public sealed class WizardStepDescriptor
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconCss { get; set; } = "";
}

public sealed class WizardStepContext
{
    public int StepIndex { get; init; }
    public WizardStepDescriptor Step { get; init; } = new();
    public bool IsFirst { get; init; }
    public bool IsLast { get; init; }
}
