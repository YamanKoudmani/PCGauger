using System.Windows.Forms;
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
}
