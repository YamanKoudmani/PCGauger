using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// AMD GPU temperature via the ADL2 / ADL (AMD Display Library) interface in
/// atiadlxx.dll. Ships with AMD GPU drivers. Returns temperature in Celsius.
/// Falls back to atiadlxy.dll (32-bit variant) when the 64-bit DLL is absent.
/// No admin required.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AdlxInterop : IDisposable
{
    private IntPtr _hModule;
    private TemperatureReader? _reader;

    public static AdlxInterop? TryLoad()
    {
        try
        {
            IntPtr h = NativeMethods.LoadLibrary("atiadlxx.dll");
            if (h == IntPtr.Zero)
            {
                h = NativeMethods.LoadLibrary("atiadlxy.dll");
                if (h == IntPtr.Zero) return null;
            }

            var result = new AdlxInterop { _hModule = h };

            if (PmLogReader.TryCreate(h, out var pmLog))
                result._reader = pmLog;
            else if (Adl2Reader.TryCreate(h, out var adl2))
                result._reader = adl2;
            else if (AdlReader.TryCreate(h, out var adl))
                result._reader = adl;

            if (result._reader == null)
            {
                NativeMethods.FreeLibrary(h);
                return null;
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    public float? GetGpuCoreTemp(DxgiFactory.LUID luid)
    {
        if (_reader == null) return null;
        try
        {
            return _reader.ReadTemperatureCelsius();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
        if (_hModule != IntPtr.Zero)
        {
            NativeMethods.FreeLibrary(_hModule);
            _hModule = IntPtr.Zero;
        }
    }

    // ── Abstract temperature reader ──

    private abstract class TemperatureReader : IDisposable
    {
        public abstract float? ReadTemperatureCelsius();
        public abstract void Dispose();
    }

    // ── PMLog reader (Overdrive >= 8, RDNA2+) ──

    private sealed class PmLogReader : TemperatureReader
    {
        private IntPtr _context;
        private readonly ADL2_Main_Control_DestroyDelegate _destroy;
        private readonly ADL2_New_QueryPMLogData_GetDelegate _getPmLog;
        private readonly int _adapterIndex;
        private const int PmLogStructSize = 4 + 256 * 24;

        private delegate int ADL2_Main_Control_CreateDelegate(AllocCallback callback, int enumConnected, out IntPtr context);
        private delegate int ADL2_Main_Control_DestroyDelegate(IntPtr context);
        private delegate int ADL2_New_QueryPMLogData_GetDelegate(IntPtr context, int adapterIndex, IntPtr output);
        private delegate int ADL2_Overdrive_CapsDelegate(IntPtr context, int adapterIndex, out int supported, out int enabled, out int version);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr AllocCallback(int size);

        private static readonly AllocCallback _alloc = Alloc;
        private static readonly GCHandle _allocHandle = GCHandle.Alloc(_alloc);
        private const int ADL_OK = 0;

        private PmLogReader(IntPtr context, ADL2_Main_Control_DestroyDelegate destroy, ADL2_New_QueryPMLogData_GetDelegate getPmLog, int adapterIndex)
        {
            _context = context;
            _destroy = destroy;
            _getPmLog = getPmLog;
            _adapterIndex = adapterIndex;
        }

        public static bool TryCreate(IntPtr hModule, out PmLogReader? reader)
        {
            reader = null;
            try
            {
                var create = GetProc<ADL2_Main_Control_CreateDelegate>(hModule, "ADL2_Main_Control_Create");
                var destroy = GetProc<ADL2_Main_Control_DestroyDelegate>(hModule, "ADL2_Main_Control_Destroy");
                var getPmLog = GetProc<ADL2_New_QueryPMLogData_GetDelegate>(hModule, "ADL2_New_QueryPMLogData_Get");
                var caps = GetProc<ADL2_Overdrive_CapsDelegate>(hModule, "ADL2_Overdrive_Caps");
                if (create == null || destroy == null || getPmLog == null || caps == null) return false;

                if (create(_alloc, 1, out IntPtr ctx) != ADL_OK || ctx == IntPtr.Zero) return false;

                int foundIndex = -1;
                for (int i = 0; i < 8; i++)
                {
                    if (caps(ctx, i, out int supported, out int enabled, out int version) == ADL_OK && supported != 0 && version >= 8)
                    {
                        foundIndex = i;
                        break;
                    }
                }

                if (foundIndex < 0)
                {
                    destroy(ctx);
                    return false;
                }

                reader = new PmLogReader(ctx, destroy, getPmLog, foundIndex);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override float? ReadTemperatureCelsius()
        {
            if (_context == IntPtr.Zero) return null;

            IntPtr buf = Marshal.AllocHGlobal(PmLogStructSize);
            try
            {
                Marshal.WriteInt32(buf, 0, PmLogStructSize);
                if (_getPmLog(_context, _adapterIndex, buf) != ADL_OK) return null;

                // Try EDGE (8), then HOTSPOT (9), then VR SOC (12).
                if (ReadSensor(buf, 8, out int temp) || ReadSensor(buf, 9, out temp) || ReadSensor(buf, 12, out temp))
                    return temp;
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private static bool ReadSensor(IntPtr buf, int index, out int value)
        {
            int off = 4 + index * 24;
            int supported = Marshal.ReadInt32(buf, off);
            value = Marshal.ReadInt32(buf, off + 4);
            return supported != 0 && value >= 0 && value < 200;
        }

        public override void Dispose()
        {
            if (_context != IntPtr.Zero)
            {
                try { _destroy(_context); } catch { }
                _context = IntPtr.Zero;
            }
        }

        private static IntPtr Alloc(int size) => Marshal.AllocHGlobal(size);
    }

    // ── ADL2 Overdrive5 (context-based, pre-RDNA2) ──

    private sealed class Adl2Reader : TemperatureReader
    {
        private IntPtr _context;
        private readonly ADL2_Overdrive5_Temperature_GetDelegate _getTemp;
        private readonly ADL2_Main_Control_DestroyDelegate _destroy;

        private delegate int ADL2_Main_Control_CreateDelegate(AllocCallback callback, int enumConnected, out IntPtr context);
        private delegate int ADL2_Main_Control_DestroyDelegate(IntPtr context);
        private delegate int ADL2_Overdrive5_Temperature_GetDelegate(IntPtr context, int adapterIndex, out int temperature);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr AllocCallback(int size);

        private static readonly AllocCallback _alloc = Alloc;
        private static readonly GCHandle _allocHandle = GCHandle.Alloc(_alloc);

        private const int ADL_OK = 0;

        private Adl2Reader(IntPtr context, ADL2_Overdrive5_Temperature_GetDelegate getTemp, ADL2_Main_Control_DestroyDelegate destroy)
        {
            _context = context;
            _getTemp = getTemp;
            _destroy = destroy;
        }

        public static bool TryCreate(IntPtr hModule, out Adl2Reader? reader)
        {
            reader = null;
            try
            {
                var create = GetProc<ADL2_Main_Control_CreateDelegate>(hModule, "ADL2_Main_Control_Create");
                var destroy = GetProc<ADL2_Main_Control_DestroyDelegate>(hModule, "ADL2_Main_Control_Destroy");
                var getTemp = GetProc<ADL2_Overdrive5_Temperature_GetDelegate>(hModule, "ADL2_Overdrive5_Temperature_Get");
                if (create == null || destroy == null || getTemp == null) return false;

                if (create(_alloc, 0, out IntPtr ctx) != ADL_OK || ctx == IntPtr.Zero) return false;

                reader = new Adl2Reader(ctx, getTemp, destroy);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override float? ReadTemperatureCelsius()
        {
            if (_context == IntPtr.Zero) return null;
            if (_getTemp(_context, 0, out int temp) == ADL_OK && temp > 0)
                return temp / 1000.0f;
            return null;
        }

        public override void Dispose()
        {
            if (_context != IntPtr.Zero)
            {
                try { _destroy(_context); } catch { }
                _context = IntPtr.Zero;
            }
        }

        private static IntPtr Alloc(int size) => Marshal.AllocHGlobal(size);
    }

    // ── ADL (legacy, no context) ──

    private sealed class AdlReader : TemperatureReader
    {
        private readonly ADL_Overdrive5_Temperature_GetDelegate _getTemp;
        private readonly ADL_Main_Control_DestroyDelegate _destroy;

        private delegate int ADL_Main_Control_CreateDelegate(AllocCallback callback, int enumConnected);
        private delegate int ADL_Main_Control_DestroyDelegate();
        private delegate int ADL_Overdrive5_Temperature_GetDelegate(int adapterIndex, out int temperature);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr AllocCallback(int size);

        private static readonly AllocCallback _alloc = Alloc;
        private static readonly GCHandle _allocHandle = GCHandle.Alloc(_alloc);

        private const int ADL_OK = 0;

        private AdlReader(ADL_Overdrive5_Temperature_GetDelegate getTemp, ADL_Main_Control_DestroyDelegate destroy)
        {
            _getTemp = getTemp;
            _destroy = destroy;
        }

        public static bool TryCreate(IntPtr hModule, out AdlReader? reader)
        {
            reader = null;
            try
            {
                var create = GetProc<ADL_Main_Control_CreateDelegate>(hModule, "ADL_Main_Control_Create");
                var destroy = GetProc<ADL_Main_Control_DestroyDelegate>(hModule, "ADL_Main_Control_Destroy");
                var getTemp = GetProc<ADL_Overdrive5_Temperature_GetDelegate>(hModule, "ADL_Overdrive5_Temperature_Get");
                if (create == null || destroy == null || getTemp == null) return false;

                if (create(_alloc, 0) != ADL_OK) return false;

                reader = new AdlReader(getTemp, destroy);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override float? ReadTemperatureCelsius()
        {
            if (_getTemp(0, out int temp) == ADL_OK && temp > 0)
                return temp / 1000.0f;
            return null;
        }

        public override void Dispose()
        {
            try { _destroy(); } catch { }
        }

        private static IntPtr Alloc(int size) => Marshal.AllocHGlobal(size);
    }

    private static T? GetProc<T>(IntPtr h, string name) where T : class
    {
        IntPtr p = NativeMethods.GetProcAddress(h, name);
        return p != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;
    }
}
