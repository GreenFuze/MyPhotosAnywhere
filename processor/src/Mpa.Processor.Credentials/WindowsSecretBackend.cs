using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Mpa.Processor.Credentials;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSecretBackend : ISecretBackend
{
    private readonly string _root;
    private readonly byte[] _entropy;

    public WindowsSecretBackend(string root, Guid installationId)
    {
        _root = Path.GetFullPath(root);
        if (Path.TrimEndingDirectorySeparator(_root) == Path.GetPathRoot(_root)) throw new ArgumentException("A dedicated protected directory is required.", nameof(root));
        Directory.CreateDirectory(_root);
        CheckPath(_root);
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes("MPA protected credentials v1:" + installationId.ToString("N")));
    }

    public byte[]? Read(string address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Address(address);
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        using (input)
        {
            if (input.Length is <= 0 or > NativeCredentialVault.MaximumSecretBytes + 4096) throw new InvalidDataException("Protected credential has invalid length.");
            var encrypted = new byte[checked((int)input.Length)];
            input.ReadExactly(encrypted);
            cancellationToken.ThrowIfCancellationRequested();
            try { return ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { throw new CredentialVaultException("Windows could not unlock this credential. Sign in to its original Windows account or reconnect Google."); }
        }
    }

    public void Write(string address, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Address(address);
        var encrypted = ProtectedData.Protect(bytes, _entropy, DataProtectionScope.CurrentUser);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        var ownsPending = false;
        try
        {
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsPending = true;
                output.Write(encrypted);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _ = Address(address);
            File.Move(pending, path, overwrite: true);
        }
        finally { if (ownsPending) File.Delete(pending); }
    }

    public void Delete(string address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Address(address));
    }

    private string Address(string address)
    {
        CheckPath(_root);
        var path = Path.Combine(_root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address))) + ".dpapi");
        CheckPath(path);
        return path;
    }

    private static void CheckPath(string path)
    {
        // Walk existing ancestors too: a normal leaf inside a linked parent is not a private store.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Protected storage cannot use a link/reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_entropy);
}
