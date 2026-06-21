using System;

namespace Fx.ControlKit;

public class ThemeService
{
    public string CurrentTheme { get; private set; } = "vb"; // VB is the base default theme

    public event Action? OnThemeChanged;

    public void SetTheme(string theme)
    {
        if (CurrentTheme != theme)
        {
            CurrentTheme = theme;
            OnThemeChanged?.Invoke();
        }
    }
}
