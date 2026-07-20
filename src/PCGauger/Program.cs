using System.Windows.Forms;

namespace PCGauger;

internal static class Program
{
    [System.STAThread]
    private static void Main()
    {
        // Surface ANY startup failure instead of leaving a frozen splash.
        // A constructor throw/hang on a user's machine must never be silent.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal(e.ExceptionObject as Exception ?? new Exception("Unknown fatal error"));


        // Show the loading splash on the UI thread. The main form is built
        // DIRECTLY on the UI thread (no worker Task.Run) so WinForms/SkiaSharp
        // controls are constructed on the thread that owns them. Construction
        // does ZERO blocking device I/O — all DXGI/DriveInfo/NIC enumeration is
        // deferred and resolved asynchronously after the form is shown, so the
        // splash dismisses immediately and the app never hangs at startup.
        using var splash = new SplashForm();
        splash.Show();

        MainForm? built = null;
        Exception? err = null;
        try
        {
            built = new MainForm(splash);
        }
        catch (Exception ex)
        {
            err = ex;
        }

        // Belt-and-suspenders: the Shown handler also signals ready, but make
        // sure the splash is closed before we hand off to the main loop.
        try { splash.Close(); } catch { }

        if (err != null) { Fatal(err); return; }
        if (built != null) Application.Run(built);
    }

    private static void Fatal(Exception ex, SplashForm? splash = null)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "PCGauger");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "crash.log"),
                $"[{System.DateTime.Now:u}] {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n\n");
        }
        catch { /* logging must never throw */ }

        try { splash?.Close(); } catch { }

        MessageBox.Show(
            $"PCGauger failed to start.\n\n{ex?.GetType().Name}: {ex?.Message}\n\nDetails written to PCGauger\\crash.log",
            "PCGauger", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
