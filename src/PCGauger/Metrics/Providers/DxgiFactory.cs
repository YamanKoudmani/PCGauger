using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using PCGauger.Metrics.Providers;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// Minimal COM interop for DXGI used by GpuProvider to read VRAM budget/usage.
/// IDXGIFactory1::EnumAdapters1 and IDXGIAdapter3::QueryVideoMemoryInfo are COM
/// methods (vtable slots), NOT dll exports, so they are invoked through the
/// interface vtable via delegate marshalling. Best-effort: failures are caught
/// by the caller so a missing/odd adapter never breaks the GPU tile.
///
/// Every native entry point below runs OFF the calling thread with a hard
/// timeout. On headless / RDP / wedged-display-driver machines
/// CreateDXGIFactory1 and EnumAdapters1 can block INDEFINITELY and freeze the
/// UI thread at startup, so a call that doesn't finish in time is abandoned and
/// the method returns null/empty — the caller must never wait on DXGI.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DxgiFactory
{
    // Hard cap on any single native DXGI call. A hang past this is treated as
    // "no adapter" rather than blocking the caller (typically the UI thread).
    private const int DxgiTimeoutMs = 1500;
    private static readonly Guid IID_IDXGIFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    private static readonly Guid IID_IDXGIAdapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    // IDXGIFactory1::EnumAdapters1 (vtable slot 12)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factory, uint adapter, out IntPtr adapterPtr);

    // IDXGIAdapter3::QueryVideoMemoryInfo (vtable slot 14)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemoryInfoDelegate(
        IntPtr adapter,
        uint nodeIndex,
        NativeMethods.DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup,
        out NativeMethods.DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);

    // IDXGIAdapter1::GetDesc1 (vtable slot 10) — gives the adapter description
    // and LUID used to correlate this adapter with its PDH GPU Engine instances.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 pDesc);

    /// <summary>
    /// DXGI_ADAPTER_DESC1 (d3dcommon.h). Description is a fixed 128-char WCHAR
    /// buffer; Luid is the adapter's locally-unique identifier that PDH embeds
    /// in GPU Engine instance names as "luid_0x{HighPart:X8}_0x{LowPart:X8}".
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID Luid;
        public uint Flags;
    }

    /// <summary>Win32 LUID (locally unique identifier).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>
    /// Creates the DXGI factory OFF the calling thread with a hard timeout.
    /// Returns null if the native call times out or fails — never blocks the
    /// caller beyond <see cref="DxgiTimeoutMs"/>.
    /// </summary>
    public static DxgiFactoryHandle? Create()
    {
        try
        {
            var task = Task.Run(() =>
            {
                IntPtr ptr;
                var iid = IID_IDXGIFactory1;
                int hr = CreateDXGIFactory1(ref iid, out ptr);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                return new DxgiFactoryHandle(ptr);
            });
            return task.Wait(DxgiTimeoutMs) ? task.Result : null;
        }
        catch
        {
            // Native failure or timeout: no factory rather than a hang.
            return null;
        }
    }

    public sealed class DxgiFactoryHandle : IDisposable
    {
        private readonly IntPtr _ptr;
        public DxgiFactoryHandle(IntPtr ptr) => _ptr = ptr;

        public DxgiAdapterHandle? EnumAdapter(uint index)
        {
            if (_ptr == IntPtr.Zero) return null;
            try
            {
                var task = Task.Run(() =>
                {
                    // vtable slot 12 (0=IUnknown, 1=AddRef, 2=Release, then IDXGIObject
                    // 3-6, IDXGIFactory 7-11, IDXGIFactory1 12).
                    IntPtr vtable = Marshal.ReadIntPtr(_ptr);
                    IntPtr slot = Marshal.ReadIntPtr(vtable + 12 * IntPtr.Size);
                    var del = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(slot);
                    int hr = del(_ptr, index, out var adapterPtr);
                    return hr < 0 ? null : new DxgiAdapterHandle(adapterPtr);
                });
                return task.Wait(DxgiTimeoutMs) ? task.Result : null;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose() => Marshal.Release(_ptr);
    }

    public sealed class DxgiAdapterHandle : IDisposable
    {
        private readonly IntPtr _ptr;
        public DxgiAdapterHandle(IntPtr ptr) => _ptr = ptr;

        public NativeMethods.DXGI_QUERY_VIDEO_MEMORY_INFO QueryVideoMemoryInfo(NativeMethods.DXGI_MEMORY_SEGMENT_GROUP group)
        {
            if (_ptr == IntPtr.Zero) return default;
            try
            {
                var task = Task.Run(() =>
                {
                    // Query interface for IDXGIAdapter3.
                    var iid = IID_IDXGIAdapter3;
                    int hr = Marshal.QueryInterface(_ptr, ref iid, out var adapter3);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                        // IDXGIAdapter3 vtable slot 14.
                        IntPtr vtable3 = Marshal.ReadIntPtr(adapter3);
                        IntPtr slot = Marshal.ReadIntPtr(vtable3 + 14 * IntPtr.Size);
                        var del = Marshal.GetDelegateForFunctionPointer<QueryVideoMemoryInfoDelegate>(slot);
                        hr = del(adapter3, 0, group, out var info);
                        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                        return info;
                    }
                    finally
                    {
                        Marshal.Release(adapter3);
                    }
                });
                return task.Wait(DxgiTimeoutMs) ? task.Result : default;
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// Read IDXGIAdapter1::GetDesc1. Returns null on failure OR timeout.
        /// Exposes the adapter description (DisplayName) and LUID (PDH instance
        /// correlation). Runs off-thread with a hard timeout so a wedged driver
        /// can't freeze the caller.
        /// </summary>
        public DXGI_ADAPTER_DESC1? GetDesc1()
        {
            if (_ptr == IntPtr.Zero) return null;
            try
            {
                var task = Task.Run(() =>
                {
                    // Query interface for IDXGIAdapter1 (same object already supports
                    // IDXGIAdapter3; GetDesc1 lives on the base IDXGIAdapter1).
                    var iid = IID_IDXGIAdapter1;
                    int hr = Marshal.QueryInterface(_ptr, ref iid, out var adapter1);
                    if (hr < 0) return (DXGI_ADAPTER_DESC1?)null;
                    try
                    {
                        // IDXGIAdapter1 vtable slot 10.
                        IntPtr vtable1 = Marshal.ReadIntPtr(adapter1);
                        IntPtr slot = Marshal.ReadIntPtr(vtable1 + 10 * IntPtr.Size);
                        var del = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(slot);
                        hr = del(adapter1, out var desc);
                        return hr < 0 ? (DXGI_ADAPTER_DESC1?)null : desc;
                    }
                    finally
                    {
                        Marshal.Release(adapter1);
                    }
                });
                return task.Wait(DxgiTimeoutMs) ? task.Result : null;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose() => Marshal.Release(_ptr);
    }
}
