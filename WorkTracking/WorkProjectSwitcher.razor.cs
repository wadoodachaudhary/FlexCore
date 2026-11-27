using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.WorkTracking;

/// <summary>One row in <see cref="WorkProjectSwitcher"/>.</summary>
public sealed record WorkProjectChoice(string Key, string Label);

/// <summary>Primary switcher for the working applications on a work board.</summary>
public partial class WorkProjectSwitcher
{
    [Parameter] public IReadOnlyList<WorkProjectChoice> Choices { get; set; } = [];
    [Parameter] public string Value { get; set; } = "";
    [Parameter] public EventCallback<string> ValueChanged { get; set; }
    [Parameter] public string Label { get; set; } = "Application";
    [Parameter] public string AriaLabel { get; set; } = "Working application";
    [Parameter] public string? Id { get; set; }
    [Parameter] public string Placeholder { get; set; } = "Select an application";
    [Parameter] public string Width { get; set; } = "420px";
    [Parameter] public bool Enabled { get; set; } = true;

    private Task OnChanged(string? key) => ValueChanged.InvokeAsync(key ?? "");

    /// <summary>Labels each saved project as its key and name.</summary>
    public static IReadOnlyList<WorkProjectChoice> FromProjects(IEnumerable<WorkProject> projects)
        => projects.Select(project => new WorkProjectChoice(project.Key, project.Key + " · " + project.Name)).ToArray();
}
