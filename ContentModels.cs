namespace Fx.ControlKit;
public enum FlexOrientation { Horizontal, Vertical }
public enum FlexAlignment { Start, Center, End, Stretch, SpaceBetween, SpaceAround, SpaceEvenly }
public enum ChoiceSelectionMode { Single, Multiple }
public enum AvatarShape { Circle, Rounded, Square }
public enum LoaderType { Spinner, Pulsing, Dots }
public enum AnimationEffect { Fade, Slide, Scale }
public sealed record SelectionItem(string Value, string Text, bool Disabled = false, string? IconCss = null);
public sealed record ChipItem(string Id, string Text, bool Disabled = false, string? IconCss = null);
