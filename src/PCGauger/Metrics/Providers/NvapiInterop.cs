using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PCGauger.Metrics.Providers;

[SupportedOSPlatform("windows")]
internal sealed class NvapiInterop : IDisposable
{
    private IntPtr _hModule;
    private readonly NvApiQueryInterfaceDelegate _queryInterface;
    private NvApiInitializeDelegate? _initialize;
    private NvApiUnloadDelegate? _unload;
    private NvApiGetPhysicalGPUFromLUIDDelegate? _getGpuFromLuid;
    private NvApiGPUGetThermalSettingsDelegate? _getThermalSettings;
    private bool _initialized;

    private NvapiInterop(IntPtr hModule, NvApiQueryInterfaceDelegate queryInterface)
    {
        _hModule = hModule;
        _queryInterface = queryInterface;
    }

    public static NvapiInterop? TryLoad()
    {
        try
        {
            IntPtr h = NativeMethods.LoadLibrary("nvapi64.dll");
            if (h == IntPtr.Zero) return null;

            IntPtr qiPtr = NativeMethods.GetProcAddress(h, "nvapi_QueryInterface");
            if (qiPtr == IntPtr.Zero) { NativeMethods.FreeLibrary(h); return null; }

            var qi = Marshal.GetDelegateForFunctionPointer<NvApiQueryInterfaceDelegate>(qiPtr);
            var result = new NvapiInterop(h, qi);
            if (!result.Initialize()) { result.Dispose(); return null; }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private bool Initialize()
    {
        IntPtr fn = _queryInterface(Ordinals.Initialize);
        if (fn == IntPtr.Zero) return false;
        _initialize = Marshal.GetDelegateForFunctionPointer<NvApiInitializeDelegate>(fn);

        fn = _queryInterface(Ordinals.Unload);
        if (fn != IntPtr.Zero)
            _unload = Marshal.GetDelegateForFunctionPointer<NvApiUnloadDelegate>(fn);

        fn = _queryInterface(Ordinals.GetPhysicalGPUFromLUID);
        if (fn != IntPtr.Zero)
            _getGpuFromLuid = Marshal.GetDelegateForFunctionPointer<NvApiGetPhysicalGPUFromLUIDDelegate>(fn);

        fn = _queryInterface(Ordinals.GPUGetThermalSettings);
        if (fn != IntPtr.Zero)
            _getThermalSettings = Marshal.GetDelegateForFunctionPointer<NvApiGPUGetThermalSettingsDelegate>(fn);

        if (_initialize == null) return false;
        int status = _initialize();
        if (status != 0) return false;
        _initialized = true;
        return true;
    }

    public float? GetGpuCoreTemp(DxgiFactory.LUID luid)
    {
        if (!_initialized || _getGpuFromLuid == null || _getThermalSettings == null)
            return null;

        try
        {
            int status = _getGpuFromLuid(ref luid, out IntPtr gpuHandle);
            if (status != 0 || gpuHandle == IntPtr.Zero) return null;

            var settings = new NV_GPU_THERMAL_SETTINGS
            {
                Version = (uint)(Marshal.SizeOf<NV_GPU_THERMAL_SETTINGS>() | (1 << 16)),
                Count = MaxSensorsPerGpu,
            };
            status = _getThermalSettings(gpuHandle, 0, ref settings);
            if (status != 0) return null;

            for (int i = 0; i < settings.Count && i < MaxSensorsPerGpu; i++)
            {
                if (settings.Sensors[i].Target == NvThermalTarget.GPU)
                    return settings.Sensors[i].Temperature;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try { _unload?.Invoke(); } catch { }
            _initialized = false;
        }
        if (_hModule != IntPtr.Zero)
        {
            NativeMethods.FreeLibrary(_hModule);
            _hModule = IntPtr.Zero;
        }
    }

    private const uint MaxSensorsPerGpu = 3;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NvApiQueryInterfaceDelegate(uint ordinal);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiInitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiUnloadDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiGetPhysicalGPUFromLUIDDelegate(ref DxgiFactory.LUID luid, out IntPtr gpuHandle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiGPUGetThermalSettingsDelegate(IntPtr gpuHandle, int sensorIndex, ref NV_GPU_THERMAL_SETTINGS settings);

    private static class Ordinals
    {
        public const uint Initialize = 0x0150E828;
        public const uint Unload = 0xD22BDD7E;
        public const uint GetPhysicalGPUFromLUID = 0x2B2E886A;
        public const uint GPUGetThermalSettings = 0xE3640F56;
    }

    private enum NvThermalTarget : uint
    {
        None = 0,
        GPU = 1,
        Memory = 2,
        PowerSupply = 3,
        Board = 4,
        VRegulator = 5,
        VRAM = 6,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NV_GPU_THERMAL_SENSOR
    {
        public int Index;
        public NvThermalTarget Target;
        public int Temperature;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NV_GPU_THERMAL_SETTINGS
    {
        public uint Version;
        public uint Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public NV_GPU_THERMAL_SENSOR[] Sensors;
    }
}
