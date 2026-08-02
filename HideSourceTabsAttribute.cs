namespace Fx.ControlKit;

/// <summary>
/// Marks a Blazor page so that the <c>HFComponent</c> layout suppresses the
/// "VB6 Source" and ".NET Source" tabs (and their content) when that page is
/// active. Used for pages whose source listing should not be exposed in the
/// running app — most notably <c>FLogin</c>, where source must not be visible
/// to anyone who hasn't authenticated yet.
///
/// Apply via the Razor directive at the top of the page:
/// <code>
/// @attribute [HideSourceTabs]
/// </code>
///
/// Pages without the attribute behave as before — all four tabs render.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class HideSourceTabsAttribute : Attribute
{
}
