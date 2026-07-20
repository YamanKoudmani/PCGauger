using PCGauger.Metrics.Providers;

namespace PCGauger.Metrics.Catalogs;

/// <summary>
/// Enumerates DXGI adapters for multi-instance GPU tiles. Loops
/// <see cref="DxgiFactory"/>.EnumAdapter(0,1,2…) until exhaustion. Re-enumerates
/// on every call (DXGI enumeration is a cheap COM call) so a hot-plugged eGPU
/// appears next time a picker opens. Never throws — returns an empty list on
/// failure.
/// </summary>
public sealed class GpuCatalog : IDeviceCatalog
{
    public MultiInstanceKind Kind => MultiInstanceKind.Gpu;

    public string? DefaultDeviceId
    {
        get
        {
            // v1 implicitly displayed adapter 0.
            try
            {
                using var factory = DxgiFactory.Create();
                using var adapter = factory.EnumAdapter(0);
                return adapter != null ? "0" : null;
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
            using var factory = DxgiFactory.Create();
            uint index = 0;
            while (true)
            {
                using var adapter = factory.EnumAdapter(index);
                if (adapter == null) break;

                var desc = adapter.GetDesc1();
                string display = desc?.Description ?? $"Adapter {index}";
                // Dedicated VRAM in GB when cheaply available.
                string detail = desc.HasValue && desc.Value.DedicatedVideoMemory.ToUInt64() > 0
                    ? $"{desc.Value.DedicatedVideoMemory.ToUInt64() / 1_073_741_824.0:N1} GB VRAM"
                    : string.Empty;

                result.Add(new DeviceDescriptor(index.ToString(), display, detail));
                index++;
            }
        }
        catch
        {
            // DXGI unavailable: report nothing rather than crash the picker.
        }
        return result;
    }
}
