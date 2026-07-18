using System.Windows.Forms;
using SkiaSharp.Views.Desktop;

namespace PCGauger;

/// <summary>
/// SKControl subclass that is transparent to hit-testing: on WM_NCHITTEST it
/// returns HTTRANSPARENT so the message falls through to the parent form, which
/// owns the borderless drag/resize logic. Without this, the control answers
/// HTCLIENT for the whole client area and the form's WM_NCHITTEST handler is
/// never reached, so a borderless window can't be moved or resized.
/// </summary>
internal sealed class HitTestSurface : SKControl
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }
}
