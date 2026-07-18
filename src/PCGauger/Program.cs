using System.Windows.Forms;

namespace PCGauger;

internal static class Program
{
    [System.STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
