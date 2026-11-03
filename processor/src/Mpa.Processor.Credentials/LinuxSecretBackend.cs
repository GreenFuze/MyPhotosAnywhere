using System.Runtime.InteropServices;

namespace Mpa.Processor.Credentials;

/// <summary>Binary Secret Service items, scoped by installation and hashed identity. Native calls
/// are joined on a background owner and canceled through GIO, never abandoned with WaitAsync.</summary>
internal sealed partial class LinuxSecretBackend : ISecretBackend
{
    private readonly nint _glib = NativeLibrary.Load("libglib-2.0.so.0");

    public byte[]? Read(string address, CancellationToken cancellationToken) => Execute(address, (service, schema, attributes, cancel) =>
    {
        using var item = FindItem(service, schema, attributes, cancel, cancellationToken);
        if (item is null) return null;
        var loaded = LoadSecret(item.Pointer, cancel, out var error);
        CheckError(error, cancellationToken);
        if (loaded == 0) throw LockedStore();
        using var value = new NativeResource(ItemSecret(item.Pointer), ValueUnref);
        var data = ValueGet(value.Pointer, out var length);
        if (length is 0 or > NativeCredentialVault.MaximumSecretBytes || data == 0) throw new InvalidDataException("Secret Service returned an invalid credential length.");
        var bytes = new byte[(int)length];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        return bytes;
    }, cancellationToken);

    public void Write(string address, byte[] bytes, CancellationToken cancellationToken) => Execute(address, (service, schema, attributes, cancel) =>
    {
        using var value = new NativeResource(ValueNew(bytes, bytes.Length, "application/octet-stream"), ValueUnref);
        using var item = FindItem(service, schema, attributes, cancel, cancellationToken);
        var stored = item is null
            ? Store(service, schema, attributes, "default", "My Photos Anywhere Processor credential", value.Pointer, cancel, out var error)
            : SetSecret(item.Pointer, value.Pointer, cancel, out error);
        CheckError(error, cancellationToken);
        if (stored == 0) throw new CredentialVaultException("Linux Secret Service did not store the credential. Unlock your login keyring and retry.");
        return true;
    }, cancellationToken);

    public void Delete(string address, CancellationToken cancellationToken) => Execute(address, (service, schema, attributes, cancel) =>
    {
        using var item = FindItem(service, schema, attributes, cancel, cancellationToken);
        if (item is null) return false;
        var removed = DeleteItem(item.Pointer, cancel, out var error);
        CheckError(error, cancellationToken);
        if (removed == 0) throw new CredentialVaultException("Linux Secret Service could not remove the credential. Unlock your login keyring and retry.");
        return true;
    }, cancellationToken);

    private T Execute<T>(string address, Func<nint, nint, nint, nint, T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = new NativeResource(Marshal.StringToCoTaskMemUTF8("identity"), Marshal.FreeCoTaskMem);
        using var text = new NativeResource(Marshal.StringToCoTaskMemUTF8(address), Marshal.FreeCoTaskMem);
        var hash = NativeLibrary.GetExport(_glib, "g_str_hash");
        var equal = NativeLibrary.GetExport(_glib, "g_str_equal");
        using var definitions = new NativeResource(TableNew(hash, equal), TableUnref);
        _ = TableInsert(definitions.Pointer, key.Pointer, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING = 0
        using var schema = new NativeResource(SchemaNew("org.myphotosanywhere.processor.Credentials.v1", 0, definitions.Pointer), SchemaUnref);
        using var attributes = new NativeResource(TableNew(hash, equal), TableUnref);
        _ = TableInsert(attributes.Pointer, key.Pointer, text.Pointer);
        using var cancel = new NativeResource(CancellableNew(), ObjectUnref);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var registration = deadline.Token.Register(() => CancellableCancel(cancel.Pointer));
        try
        {
            var pointer = GetService(0, cancel.Pointer, out var error);
            CheckError(error, cancellationToken);
            using var service = new NativeResource(pointer, ObjectUnref);
            // Use SecretService directly, rather than libsecret's configurable backend selection.
            // A missing desktop service never selects a file/plaintext fallback.
            return operation(service.Pointer, schema.Pointer, attributes.Pointer, cancel.Pointer);
        }
        catch (CredentialVaultException) when (deadline.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new CredentialVaultException("Linux Secret Service did not respond within 30 seconds. Unlock the login keyring or start its Secret Service, then retry.");
        }
    }

    private static NativeResource? FindItem(nint service, nint schema, nint attributes, nint cancel, CancellationToken cancellationToken)
    {
        // ALL matches, but no UNLOCK flag and no prompt. Locked is different from missing.
        var pointer = Search(service, schema, attributes, 2, cancel, out var error);
        using var items = pointer == 0 ? null : new NativeResource(pointer, ReleaseItems);
        CheckError(error, cancellationToken);
        if (items is null) return null;
        if (Marshal.ReadIntPtr(items.Pointer, IntPtr.Size) != 0) throw new InvalidDataException("Duplicate OS credential identities require repair; no record was chosen or deleted.");
        var item = Marshal.ReadIntPtr(items.Pointer);
        if (GetLocked(item) != 0) throw LockedStore();
        return new NativeResource(ObjectRef(item), ObjectUnref);
    }

    private static void ReleaseItems(nint list)
    {
        for (var current = list; current != 0; current = Marshal.ReadIntPtr(current, IntPtr.Size)) ObjectUnref(Marshal.ReadIntPtr(current));
        ListFree(list);
    }

    private static CredentialVaultException LockedStore() => new("Linux Secret Service credential is locked. Unlock your login keyring and retry.");

    private static void CheckError(nint error, CancellationToken cancellationToken)
    {
        if (error == 0) return;
        using var owned = new NativeResource(error, ErrorFree);
        cancellationToken.ThrowIfCancellationRequested();
        // GError.message can contain account-specific text; expose only the native numeric code.
        throw new CredentialVaultException("Linux Secret Service is unavailable or locked (native code " + Marshal.ReadInt32(error, 4) + "). Unlock your login keyring and retry.");
    }

    public void Dispose() => NativeLibrary.Free(_glib);

    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_schema_newv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SchemaNew(string name, int flags, nint definitions);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_schema_unref")] private static partial void SchemaUnref(nint value);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_value_new", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ValueNew(byte[] bytes, nint length, string contentType);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_value_get")] private static partial nint ValueGet(nint value, out nuint length);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_value_unref")] private static partial void ValueUnref(nint value);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_service_get_sync")] private static partial nint GetService(int flags, nint cancel, out nint error);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_service_search_sync")] private static partial nint Search(nint service, nint schema, nint attributes, int flags, nint cancel, out nint error);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_service_store_sync", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Store(nint service, nint schema, nint attributes, string collection, string label, nint value, nint cancel, out nint error);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_item_get_locked")] private static partial int GetLocked(nint item);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_item_load_secret_sync")] private static partial int LoadSecret(nint item, nint cancel, out nint error);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_item_get_secret")] private static partial nint ItemSecret(nint item);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_item_set_secret_sync")] private static partial int SetSecret(nint item, nint value, nint cancel, out nint error);
    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_item_delete_sync")] private static partial int DeleteItem(nint item, nint cancel, out nint error);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_new")] private static partial nint TableNew(nint hash, nint equal);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_insert")] private static partial int TableInsert(nint table, nint key, nint value);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_unref")] private static partial void TableUnref(nint table);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_list_free")] private static partial void ListFree(nint list);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] private static partial void ErrorFree(nint error);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_new")] private static partial nint CancellableNew();
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_cancel")] private static partial void CancellableCancel(nint cancel);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] private static partial void ObjectUnref(nint value);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_ref")] private static partial nint ObjectRef(nint value);
}
