using System.Runtime.InteropServices;
using System.Windows.Forms;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace PCGauger;

/// <summary>
/// SKControl that is transparent to hit-testing by default: on WM_NCHITTEST it
/// returns HTTRANSPARENT so the message falls through to the parent form, which
/// owns the borderless drag/resize logic. Without this, the control answers
/// HTCLIENT for the whole client area and the form's WM_NCHITTEST handler is
/// never reached, so a borderless window can't be moved or resized.
///
/// A HitTestOverride lets the host claim a sub-region (e.g. a tile's grab
/// handle): when it returns a non-zero hit code the control uses it, so that
/// region receives normal mouse messages (MouseDown) instead of being dragged
/// by the form.
///
/// OnPaint override uses an alpha-preserving GDI present path
/// (SetDIBitsToDevice) instead of the base SKControl rendering, which
/// flattens alpha through GDI+ DrawImage. The DWM accent policy honours the
/// alpha channel when ACCENT_ENABLE_TRANSPARENTGRADIENT or
/// ACCENT_ENABLE_ACRYLICBLURBEHIND is active, enabling see-through themes.
/// </summary>
internal sealed class HitTestSurface : SKControl
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    /// <summary>
    /// Optional per-point hit-test. Receives the point in client coordinates
    /// and should return a Win32 hit constant (e.g. HTCLIENT) to claim the
    /// point, or 0 to let the control stay transparent to hits.
    /// </summary>
    public System.Func<System.Drawing.Point, int>? HitTestOverride { get; set; }

    // Cached backing store for alpha-preserving presentation.
    private SKBitmap? _backingStore;

    public HitTestSurface()
    {
        // WinForms must never erase or paint BackColor — we own every pixel
        // (including transparent ones) via OnPaint + SetDIBitsToDevice.
        SetStyle(ControlStyles.Opaque |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int w = Width;
        int h = Height;
        if (w <= 0 || h <= 0) return;

        // (Re)create backing store only when size changes.
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (_backingStore == null || _backingStore.Width != w || _backingStore.Height != h)
        {
            _backingStore?.Dispose();
            _backingStore = new SKBitmap(info);
        }

        // Create a surface that renders directly into the bitmap's pixel buffer.
        using var surface = SKSurface.Create(_backingStore.Info, _backingStore.GetPixels());
        if (surface == null) return;

        // Raise PaintSurface — this is what fires the host's OnPaintSurface handler
        // (MainForm / DetachedTileForm), which does all the SkiaSharp rendering.
        OnPaintSurface(new SKPaintSurfaceEventArgs(surface, info));

        // Present pixels to the window DC preserving the alpha channel.
        // GDI+ DrawImage (used by base.OnPaint) flattens alpha; SetDIBitsToDevice
        // does not, so DWM can honour per-pixel transparency/acrylic tint.
        IntPtr hdc = e.Graphics.GetHdc();
        try
        {
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // negative = top-down DIB (matches Skia layout)
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                }
            };

            SetDIBitsToDevice(
                hdc,
                0, 0,                   // destination origin
                (uint)w, (uint)h,       // width, height
                0, 0,                   // source origin
                0,                      // first scan line
                (uint)h,                // number of scan lines
                _backingStore.GetPixels(),
                ref bmi,
                0);                     // DIB_RGB_COLORS
        }
        finally
        {
            e.Graphics.ReleaseHdc(hdc);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            var hit = HitTestOverride;
            if (hit != null)
            {
                // Sign-extend: screen coords can be negative on multi-monitor
                // setups (e.g. the mini panel left of/above the primary).
                int lp = m.LParam.ToInt32();
                var pt = PointToClient(new System.Drawing.Point(
                    (short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int code = hit(pt);
                if (code != 0) { m.Result = (IntPtr)code; return; }
            }
            m.Result = (IntPtr)HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _backingStore?.Dispose();
            _backingStore = null;
        }
        base.Dispose(disposing);
    }

    // --- GDI present helpers ---

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // No color table for 32bpp BI_RGB
    }

    [DllImport("gdi32.dll")]
    private static extern int SetDIBitsToDevice(
        IntPtr hdc,
        int xDest,
        int yDest,
        uint dwWidth,
        uint dwHeight,
        int xSrc,
        int ySrc,
        uint uStartScan,
        uint cScanLines,
        IntPtr lpvBits,
        ref BITMAPINFO lpbmi,
        uint fuColorUse);
}
