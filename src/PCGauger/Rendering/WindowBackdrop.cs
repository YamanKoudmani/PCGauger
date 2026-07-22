namespace PCGauger.Rendering;

/// <summary>
/// How the window composites with the desktop for a theme. Opaque: normal solid
/// window. Transparent: per-pixel see-through with no blur (crystal-clear gaps).
/// Frosted: DWM acrylic blur behind the content, tinted by the theme.
/// </summary>
public enum WindowBackdrop
{
    Opaque,
    Transparent,
    Frosted,
}
