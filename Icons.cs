namespace Fx.ControlKit;

/// <summary>Reusable, dependency-free paths. Custom paths can be supplied through SvgIconControl.Path.</summary>
public static class FlexIcons
{
    public static IReadOnlyDictionary<string, string> Paths { get; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["add"]="M12 5v14M5 12h14", ["close"]="m6 6 12 12M18 6 6 18", ["check"]="m4 12 5 5L20 6",
        ["chevron-left"]="m15 5-7 7 7 7", ["chevron-right"]="m9 5 7 7-7 7", ["chevron-up"]="m5 15 7-7 7 7", ["chevron-down"]="m5 9 7 7 7-7",
        ["search"]="M21 21l-6-6M17 10a7 7 0 1 1-14 0 7 7 0 0 1 14 0",
        ["calendar"]="M4 5h16v16H4ZM8 2v6M16 2v6M4 10h16", ["clock"]="M12 7v5l3 2M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0",
        ["save"]="M4 3h13l4 4v14H3V3ZM7 3v6h10V3M7 21v-8h10v8",
        ["download"]="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5", ["print"]="M7 8V3h10v5M7 17H3V8h18v9h-4M7 13h10v8H7Z",
        ["edit"]="m4 16-1 5 5-1L21 7l-4-4ZM14 6l4 4", ["delete"]="M3 6h18M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7M14 10v7",
        ["warning"]="M12 3 2 21h20ZM12 9v5M12 17v1", ["info"]="M12 10v7M12 6v1M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0",
        ["menu"]="M3 5h18M3 12h18M3 19h18", ["pin"]="M8 3h8l-1 6 4 4H5l4-4ZM12 13v8",
        ["undock"]="M9 3h12v12M21 3 10 14M6 3H3v18h18v-3", ["sparkles"]="m12 2 3 7 7 3-7 3-3 7-3-7-7-3 7-3Z",
        ["document"]="M6 2h9l5 5v15H6ZM14 2v6h6M9 13h8M9 17h8M9 9h3", ["video"]="M3 6h13v12H3ZM16 10l5-3v10l-5-3",
        ["presentation"]="M3 4h18v11H3ZM12 15v5M8 20h8", ["question"]="M9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.7.3-1 .9-1 1.7v.5M12 17v.5M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0",
        ["flask"]="M9 3h6M10 3v6l-5 9a2 2 0 0 0 2 3h10a2 2 0 0 0 2-3l-5-9V3M7.5 14h9", ["award"]="M12 15a6 6 0 1 0 0-12 6 6 0 0 0 0 12ZM8.5 14 7 22l5-3 5 3-1.5-8",
        ["lock"]="M5 11h14v10H5ZM8 11V7a4 4 0 0 1 8 0v4"
    });
}
