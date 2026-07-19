namespace PCGauger.Infrastructure;

/// <summary>
/// Creates (once) a per-user Start Menu shortcut pointing at the running exe,
/// so the app is discoverable after being unzipped anywhere. No admin needed —
/// the shortcut lands in the user's own Start Menu\Programs folder.
/// Failure-tolerant: shell COM or filesystem errors never crash the app.
/// </summary>
public static class StartMenuShortcut
{
    public static bool EnsureCreated()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");
            var lnk = Path.Combine(dir, "PCGauger.lnk");
            if (File.Exists(lnk)) return true;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = exe;
            sc.WorkingDirectory = Path.GetDirectoryName(exe);
            sc.IconLocation = exe + ",0";
            sc.Description = "PCGauger system metrics dashboard";
            sc.Save();
            return File.Exists(lnk);
        }
        catch
        {
            return false;
        }
    }
}
