using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed class ManagedWorkspaceSourceException() : InvalidOperationException("The managed metadata workspace cannot be selected as a photo source.");

/// <summary>A bounded immutable read of the independently pinned workspace identity.
/// Names, MIME types and Drive discovery properties never define this boundary.
/// Capture again after network reads so a new setup cannot admit late metadata results.</summary>
public sealed class ManagedWorkspaceBoundary
{
    private readonly LibraryControllerPin? _controller;
    private ManagedWorkspaceBoundary(LibraryControllerPin? controller) => _controller = controller;
    public string? RootFolderId => _controller?.FolderId;
    public static ManagedWorkspaceBoundary Capture(ProcessorStateDatabase database) => Capture(database, null);
    internal static ManagedWorkspaceBoundary Capture(ProcessorStateDatabase database, SqliteTransaction? transaction) =>
        new(new LibraryAuthorityRepository(database).Controller(transaction));
    // A shared Google Drive folder retains the same ID through another authorized
    // account. Credential choice must not turn this reserved workspace into photos.
    public bool ContainsRoot(string folderId) => _controller is not null && string.Equals(folderId, _controller.FolderId, StringComparison.Ordinal);
    public void AssertSourceRoot(string folderId)
    { if (ContainsRoot(folderId)) throw new ManagedWorkspaceSourceException(); }
}
