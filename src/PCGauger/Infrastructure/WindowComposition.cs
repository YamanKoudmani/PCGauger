using System.Runtime.InteropServices;
using PCGauger.Rendering;
using SkiaSharp;

namespace PCGauger.Infrastructure;

/// <summary>
/// Manages DWM window-composition attributes for transparent/frosted backdrops.
/// Uses SetWindowCompositionAttribute (non-layered) so per-pixel alpha from our
/// custom present path is honoured by DWM without WS_EX_LAYERED.
/// All P/Invoke paths are wrapped in try/catch so failures (unsupported OS, etc.)
/// degrade silently — the window remains opaque.
/// </summary>
internal static class WindowComposition
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private enum ACCENT_STATE
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public ACCENT_STATE AccentState;
        public uint AccentFlags;       // 2 = use GradientColor
        public uint GradientColor;     // 0xAABBGGRR
        public int AnimationId;
    }

    private enum WINDOWCOMPOSITIONATTRIB
    {
        WCA_ACCENT_POLICY = 19,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public WINDOWCOMPOSITIONATTRIB Attrib;
        public IntPtr pvData;
        public int cbData;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(
        IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// Applies the correct DWM composition attribute for <paramref name="theme"/>
    /// on the given top-level window handle.
    /// Opaque   → ACCENT_DISABLED (standard behaviour, no per-pixel alpha).
    /// Transparent → ACCENT_ENABLE_TRANSPARENTGRADIENT with transparent gradient
    ///               so alpha-0 pixels show the desktop.
    /// Frosted  → ACCENT_ENABLE_ACRYLICBLURBEHIND with the theme's background
    ///            colour as the acrylic tint.
    /// </summary>
    public static void Apply(IntPtr hwnd, Theme theme)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            ApplyAccentPolicy(hwnd, theme);
        }
        catch
        {
            // Failures (e.g. pre-Win8, locked-down policy) never crash the app.
        }

        // Best-effort rounded corners — fine if it no-ops on older Windows.
        try
        {
            int cornerPref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPref, sizeof(int));
        }
        catch
        {
            // Ignored: older Windows / DWM doesn't support this attribute.
        }
    }

    private static void ApplyAccentPolicy(IntPtr hwnd, Theme theme)
    {
        var state = ACCENT_STATE.ACCENT_DISABLED;
        uint flags = 0;
        uint gradient = 0;

        switch (theme.Backdrop)
        {
            case WindowBackdrop.Opaque:
                state = ACCENT_STATE.ACCENT_DISABLED;
                flags = 0;
                gradient = 0;
                break;

            case WindowBackdrop.Transparent:
                state = ACCENT_STATE.ACCENT_ENABLE_TRANSPARENTGRADIENT;
                flags = 2; // use gradient colour
                gradient = 0; // fully transparent pixels show desktop
                break;

            case WindowBackdrop.Frosted:
                state = ACCENT_STATE.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                flags = 2; // use gradient colour as tint
                var bg = theme.Background;
                gradient = (uint)(bg.Alpha << 24 | bg.Blue << 16 | bg.Green << 8 | bg.Red);
                break;
        }

        var policy = new ACCENT_POLICY
        {
            AccentState = state,
            AccentFlags = flags,
            GradientColor = gradient,
            AnimationId = 0,
        };

        int cb = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr pPolicy = Marshal.AllocHGlobal(cb);
        try
        {
            Marshal.StructureToPtr(policy, pPolicy, false);

            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attrib = WINDOWCOMPOSITIONATTRIB.WCA_ACCENT_POLICY,
                pvData = pPolicy,
                cbData = cb,
            };

            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pPolicy);
        }
    }
}
