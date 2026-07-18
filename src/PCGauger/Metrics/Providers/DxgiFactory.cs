using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCGauger.Metrics.Providers;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// Minimal COM interop for DXGI used by GpuProvider to read VRAM budget/usage.
/// IDXGIFactory1::EnumAdapters1 and IDXGIAdapter3::QueryVideoMemoryInfo are COM
/// methods (vtable slots), NOT dll exports, so they are invoked through the
/// interface vtable via delegate marshalling. Best-effort: failures are caught
/// by the caller so a missing/odd adapter never breaks the GPU tile.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DxgiFactory
{
    private static readonly Guid IID_IDXGIFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
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

    public static DxgiFactoryHandle Create()
    {
        IntPtr ptr;
        var iid = IID_IDXGIFactory1;
        int hr = CreateDXGIFactory1(ref iid, out ptr);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return new DxgiFactoryHandle(ptr);
    }

    public sealed class DxgiFactoryHandle : IDisposable
    {
        private readonly IntPtr _ptr;
        public DxgiFactoryHandle(IntPtr ptr) => _ptr = ptr;

        public DxgiAdapterHandle? EnumAdapter(uint index)
        {
            if (_ptr == IntPtr.Zero) throw new InvalidOperationException("factory ptr is zero");
            // vtable slot 12 (0=IUnknown, 1=AddRef, 2=Release, then IDXGIObject
            // 3-6, IDXGIFactory 7-11, IDXGIFactory1 12).
            IntPtr vtable = Marshal.ReadIntPtr(_ptr);
            IntPtr slot = Marshal.ReadIntPtr(vtable + 12 * IntPtr.Size);
            var del = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(slot);
            int hr = del(_ptr, index, out var adapterPtr);
            if (hr < 0) return null;
            return new DxgiAdapterHandle(adapterPtr);
        }

        public void Dispose() => Marshal.Release(_ptr);
    }

    public sealed class DxgiAdapterHandle : IDisposable
    {
        private readonly IntPtr _ptr;
        public DxgiAdapterHandle(IntPtr ptr) => _ptr = ptr;

        public NativeMethods.DXGI_QUERY_VIDEO_MEMORY_INFO QueryVideoMemoryInfo(NativeMethods.DXGI_MEMORY_SEGMENT_GROUP group)
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
        }

        public void Dispose() => Marshal.Release(_ptr);
    }
}
