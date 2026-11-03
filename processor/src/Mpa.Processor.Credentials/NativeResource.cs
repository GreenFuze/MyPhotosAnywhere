using Microsoft.Win32.SafeHandles;

namespace Mpa.Processor.Credentials;

internal sealed class NativeResource : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Action<nint> _release;
    public NativeResource(nint value, Action<nint> release) : base(ownsHandle: true)
    {
        _release = release;
        if (value == 0) throw new CredentialVaultException("The OS credential store could not allocate a native resource.");
        SetHandle(value);
    }
    protected override bool ReleaseHandle() { _release(handle); return true; }
    public nint Pointer => !IsClosed ? DangerousGetHandle() : throw new ObjectDisposedException(nameof(NativeResource));
}
