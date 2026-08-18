namespace Fx.ControlKit;

/// <summary>Controls where text mutations live while a TextBoxControl is being edited.</summary>
public enum TextBoxTypingBehavior
{
    /// <summary>Preserves the existing Blazor event path.</summary>
    ServerBacked,

    /// <summary>Keeps typing in the browser and sends the completed value on commit.</summary>
    ClientBuffered
}
