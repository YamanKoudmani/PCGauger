using System.Windows.Forms;

namespace PCGauger;

internal static class Program
{
    [System.STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        // Show a loading splash the instant the runtime is up, so a
        // single-file launch (which extracts the bundled runtime on first
        // run) still gives immediate feedback instead of a dead-looking
        // pause. The main form closes it once it is ready to paint.
        using var splash = new SplashForm();
        splash.Show();
        Application.Run(new MainForm(splash));
    }
}
