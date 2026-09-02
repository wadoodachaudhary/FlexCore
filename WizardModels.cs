using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit;

public sealed class WizardStepDescriptor
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconCss { get; set; } = "";
    public string IconUrl { get; set; } = "";
}

public sealed class WizardStepContext
{
    public int StepIndex { get; init; }
    public WizardStepDescriptor Step { get; init; } = new();
    public bool IsFirst { get; init; }
    public bool IsLast { get; init; }
}

public sealed class WizardNavigationContext
{
    public bool CanMovePrevious { get; init; }
    public bool CanMoveNext { get; init; }
    public bool CanFinish { get; init; }
    public Func<Task> MovePreviousAsync { get; init; } = static () => Task.CompletedTask;
    public Func<Task> MoveNextAsync { get; init; } = static () => Task.CompletedTask;
    public Func<Task> FinishAsync { get; init; } = static () => Task.CompletedTask;
    public Func<Task> CancelAsync { get; init; } = static () => Task.CompletedTask;
    public Func<Task> HelpAsync { get; init; } = static () => Task.CompletedTask;
}
