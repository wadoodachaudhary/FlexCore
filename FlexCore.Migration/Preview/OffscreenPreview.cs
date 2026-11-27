namespace Fx.ControlKit.Preview;

/// <summary>
/// Frame file and stdin events for a program that paints without a virtual display.
/// Java uses a headless <c>BufferedImage</c>. Qt uses <c>QT_QPA_PLATFORM=offscreen</c>.
/// Neither path asks for Screen Recording permission or an extra package.
/// </summary>
public static class OffscreenPreview
{
    public const string ModeVariable = "FLEXCORE_PREVIEW";
    public const string ModeValue = "offscreen";
    public const string FrameVariable = "FLEXCORE_PREVIEW_FRAME";
    public const string StateVariable = "FLEXCORE_PREVIEW_STATE";
    public const string WidthVariable = "FLEXCORE_PREVIEW_WIDTH";
    public const string HeightVariable = "FLEXCORE_PREVIEW_HEIGHT";

    /// <summary>Set to <c>1</c> to use offscreen painting even when a virtual display is installed.</summary>
    public const string ForceVariable = "FLEXCORE_PREVIEW_FORCE_OFFSCREEN";

    /// <summary>macOS always prefers this path. Other systems use it only when a virtual display is missing, unless <see cref="ForceVariable"/> is <c>1</c>.</summary>
    public static bool Use(ProgramLaunch launch, bool virtualDisplay, bool macOS)
    {
        if (!launch.Offscreen) return false;
        if (string.Equals(Environment.GetEnvironmentVariable(ForceVariable), "1", StringComparison.Ordinal))
            return true;
        return macOS || !virtualDisplay;
    }

    /// <summary>One stdin line. Empty when the event has nothing to deliver.</summary>
    public static string Format(ProgramPreviewInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Kind switch
        {
            ProgramPreviewInputKind.MouseMove => "move " + input.X + " " + input.Y,
            ProgramPreviewInputKind.MouseDown => "down " + input.X + " " + input.Y + " " + input.Button,
            ProgramPreviewInputKind.MouseUp => "up " + input.X + " " + input.Y + " " + input.Button,
            ProgramPreviewInputKind.Wheel => "wheel " + input.X + " " + input.Y + " " + input.WheelDelta,
            ProgramPreviewInputKind.KeyDown => Token(input.Key) is { } key ? "key " + key : "",
            ProgramPreviewInputKind.Text => input.Text is { Length: > 0 } text ? "text " + text.Replace('\r', ' ').Replace('\n', ' ') : "",
            _ => ""
        };
    }

    private static string? Token(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        foreach (var ch in key)
        {
            if (char.IsWhiteSpace(ch) || ch is '\0') return null;
        }
        return key;
    }
}

/// <summary>RGB frame file written by an offscreen program. Magic <c>FCFR</c>, then width, height, and tightly packed RGB24.</summary>
public static class OffscreenFrame
{
    public static ReadOnlySpan<byte> Magic => "FCFR"u8;

    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (rgb.Length < width * height * 3) throw new ArgumentException("RGB buffer is shorter than the image.", nameof(rgb));
        var file = new byte[12 + width * height * 3];
        Magic.CopyTo(file);
        WriteInt(file, 4, width);
        WriteInt(file, 8, height);
        rgb[..(width * height * 3)].CopyTo(file.AsSpan(12));
        return file;
    }

    public static (int Width, int Height, byte[] Rgb) Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Not an offscreen frame.");
        var width = ReadInt(file, 4);
        var height = ReadInt(file, 8);
        if (width < 1 || height < 1 || height > int.MaxValue / Math.Max(1, width) / 3)
            throw new InvalidDataException("Offscreen frame size is invalid.");
        var pixels = width * height * 3;
        if (file.Length < 12 + pixels) throw new InvalidDataException("Offscreen frame ended early.");
        return (width, height, file.Slice(12, pixels).ToArray());
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static int ReadInt(ReadOnlySpan<byte> buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
}
