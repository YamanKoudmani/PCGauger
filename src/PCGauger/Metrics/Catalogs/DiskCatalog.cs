using System.IO;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Catalogs;

/// <summary>
/// Enumerates fixed/removable volumes for multi-instance disk tiles.
/// Re-enumerates on every call (DriveInfo.GetDrives is a cheap OS query) so a
/// hot-plugged USB drive appears the next time a picker opens. Never throws —
/// returns an empty list on any failure.
/// </summary>
public sealed class DiskCatalog : IDeviceCatalog
{
    public MultiInstanceKind Kind => MultiInstanceKind.Disk;

    public string? DefaultDeviceId
    {
        get
        {
            try
            {
                // v1 implicitly displayed the system drive.
                string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                return root.TrimEnd('\\');
            }
            catch
            {
                return null;
            }
        }
    }

    public IReadOnlyList<DeviceDescriptor> GetDevices()
    {
        var result = new List<DeviceDescriptor>();
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!di.IsReady) continue;

                string id = di.Name.TrimEnd('\\'); // "C:" form
                string label = di.VolumeLabel.Trim();
                string display = string.IsNullOrEmpty(label)
                    ? $"Local Disk ({id})"
                    : $"{label} ({id})";

                double freeGb = di.AvailableFreeSpace / 1_073_741_824.0;
                double totalGb = di.TotalSize / 1_073_741_824.0;
                string detail = $"{freeGb:N0} GB free of {totalGb:N0} GB";

                result.Add(new DeviceDescriptor(id, display, detail));
            }
        }
        catch
        {
            // OS query failed: report nothing rather than crash the picker.
        }
        return result;
    }
}
