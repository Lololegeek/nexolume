using System.Runtime.InteropServices;
namespace OfficeBridge.Native;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct Buffer { public IntPtr Pointer; public nuint Length; }
    [DllImport("office_ffi", CallingConvention = CallingConvention.Cdecl)] internal static extern ulong office_create();
    [DllImport("office_ffi", CallingConvention = CallingConvention.Cdecl)] internal static extern void office_destroy(ulong handle);
    [DllImport("office_ffi", CallingConvention = CallingConvention.Cdecl)] internal static extern int office_execute(ulong handle, byte[] input, nuint length, out Buffer output);
    [DllImport("office_ffi", CallingConvention = CallingConvention.Cdecl)] internal static extern void office_free(IntPtr pointer);
}
