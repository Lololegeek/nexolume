using System.Runtime.InteropServices;
namespace OfficeBridge.Native;

internal sealed class EngineHandle : SafeHandle
{
    public EngineHandle() : base(IntPtr.Zero, true) { SetHandle((IntPtr)NativeMethods.office_create()); if (IsInvalid) throw new InvalidOperationException("Unable to create native engine"); }
    public override bool IsInvalid => handle == IntPtr.Zero;
    protected override bool ReleaseHandle() { NativeMethods.office_destroy((ulong)handle); return true; }
}
