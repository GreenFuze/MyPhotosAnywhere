using System.Runtime.InteropServices;

namespace Mpa.Processor.Credentials;

internal sealed partial class MacKeychainBackend : ISecretBackend
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int NotFound = -25300;
    private const int Duplicate = -25299;
    private readonly nint _security = NativeLibrary.Load(Security);
    private readonly nint _foundation;

    public MacKeychainBackend()
    {
        try { _foundation = NativeLibrary.Load(Foundation); }
        catch { NativeLibrary.Free(_security); throw; }
    }

    public byte[]? Read(string address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var query = Query(address);
        Set(query, "kSecReturnData", ExportConstant(_foundation, "kCFBooleanTrue"));
        Set(query, "kSecMatchLimit", Constant("kSecMatchLimitOne"));
        var status = CopyMatching(query.Pointer, out var result);
        if (status == NotFound) return null;
        Check(status);
        using var data = new NativeResource(result, Release);
        if (GetTypeId(data.Pointer) != DataTypeId()) throw new InvalidDataException("Keychain returned an unexpected credential type.");
        var length = DataLength(data.Pointer);
        if (length is <= 0 or > NativeCredentialVault.MaximumSecretBytes) throw new InvalidDataException("Keychain credential exceeds supported bounds.");
        var bytes = new byte[(int)length];
        Marshal.Copy(DataBytes(data.Pointer), bytes, 0, bytes.Length);
        return bytes;
    }

    public void Write(string address, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var query = Query(address);
        using var data = new NativeResource(DataCreate(0, bytes, bytes.Length), Release);
        using var attributes = Dictionary();
        Set(attributes, "kSecValueData", data.Pointer);
        var status = Update(query.Pointer, attributes.Pointer);
        if (status == NotFound)
        {
            Set(query, "kSecValueData", data.Pointer);
            status = Add(query.Pointer, 0);
            // A different instance may have created the same deterministic item meanwhile.
            if (status == Duplicate)
            {
                using var retryQuery = Query(address);
                status = Update(retryQuery.Pointer, attributes.Pointer);
            }
        }
        Check(status);
    }

    public void Delete(string address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var query = Query(address);
        var status = Remove(query.Pointer);
        if (status != NotFound) Check(status);
    }

    private NativeResource Query(string address)
    {
        var query = Dictionary();
        try
        {
            using var service = Text("org.myphotosanywhere.processor.credentials.v1");
            using var account = Text(address);
            Set(query, "kSecClass", Constant("kSecClassGenericPassword"));
            Set(query, "kSecAttrService", service.Pointer);
            Set(query, "kSecAttrAccount", account.Pointer);
            Set(query, "kSecUseAuthenticationUI", Constant("kSecUseAuthenticationUIFail"));
            return query;
        }
        catch { query.Dispose(); throw; }
    }

    private NativeResource Dictionary() => new(DictionaryCreate(0, 0,
        NativeLibrary.GetExport(_foundation, "kCFTypeDictionaryKeyCallBacks"),
        NativeLibrary.GetExport(_foundation, "kCFTypeDictionaryValueCallBacks")), Release);
    private static NativeResource Text(string value) => new(StringCreate(0, value, 0x08000100), Release);
    private nint Constant(string name) => ExportConstant(_security, name);
    private static nint ExportConstant(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    private void Set(NativeResource dictionary, string key, nint value) => DictionarySet(dictionary.Pointer, Constant(key), value);
    private static void Check(int status)
    {
        if (status != 0) throw new CredentialVaultException("macOS Keychain could not access the credential (OSStatus " + status + "). Unlock the login keychain and retry.");
    }
    public void Dispose() { NativeLibrary.Free(_foundation); NativeLibrary.Free(_security); }

    [LibraryImport(Security, EntryPoint = "SecItemCopyMatching")] private static partial int CopyMatching(nint query, out nint result);
    [LibraryImport(Security, EntryPoint = "SecItemAdd")] private static partial int Add(nint attributes, nint result);
    [LibraryImport(Security, EntryPoint = "SecItemUpdate")] private static partial int Update(nint query, nint attributes);
    [LibraryImport(Security, EntryPoint = "SecItemDelete")] private static partial int Remove(nint query);
    [LibraryImport(Foundation, EntryPoint = "CFDictionaryCreateMutable")] private static partial nint DictionaryCreate(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
    [LibraryImport(Foundation, EntryPoint = "CFDictionarySetValue")] private static partial void DictionarySet(nint dictionary, nint key, nint value);
    [LibraryImport(Foundation, EntryPoint = "CFStringCreateWithCString", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint StringCreate(nint allocator, string text, uint encoding);
    [LibraryImport(Foundation, EntryPoint = "CFDataCreate")] private static partial nint DataCreate(nint allocator, byte[] bytes, nint length);
    [LibraryImport(Foundation, EntryPoint = "CFDataGetLength")] private static partial nint DataLength(nint data);
    [LibraryImport(Foundation, EntryPoint = "CFDataGetBytePtr")] private static partial nint DataBytes(nint data);
    [LibraryImport(Foundation, EntryPoint = "CFDataGetTypeID")] private static partial nuint DataTypeId();
    [LibraryImport(Foundation, EntryPoint = "CFGetTypeID")] private static partial nuint GetTypeId(nint value);
    [LibraryImport(Foundation, EntryPoint = "CFRelease")] private static partial void Release(nint value);
}
