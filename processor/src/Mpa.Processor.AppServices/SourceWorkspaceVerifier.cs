using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed class SourceWorkspaceLimitException() : InvalidOperationException("MPA could not verify this folder's ancestry within its supported limits. Choose another source folder.");

/// <summary>Read-only source admission through the captured account owner. Google Drive
/// folder IDs are global; another credential cannot authorize scanning our metadata descendants.
/// No network runs inside a state transaction. The caller must recheck the captured
/// boundary before committing, since first setup may finish while these reads await.</summary>
public sealed class SourceWorkspaceVerifier(IGoogleLibraryConnection reader, ManagedWorkspaceBoundary boundary)
{
    private readonly IGoogleLibraryConnection _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly ManagedWorkspaceBoundary _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
    public async Task AssertOutsideAsync(DriveFolder source, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        token.ThrowIfCancellationRequested();
        if (_boundary.RootFolderId is null) return;
        var active = new HashSet<string>(StringComparer.Ordinal); var complete = new HashSet<string>(StringComparer.Ordinal); var visited = 0;
        await VisitAsync(source, 0).ConfigureAwait(false);

        async Task VisitAsync(DriveFolder folder, int depth)
        {
            token.ThrowIfCancellationRequested(); _boundary.AssertSourceRoot(folder.Id);
            if (active.Contains(folder.Id)) throw new InvalidDataException("Source folder ancestry contains a cycle.");
            if (complete.Contains(folder.Id)) return;
            if (depth > 256 || ++visited > 512) throw new SourceWorkspaceLimitException();
            active.Add(folder.Id);
            foreach (var id in folder.Parents)
            {
                _boundary.AssertSourceRoot(id);
                if (active.Contains(id)) throw new InvalidDataException("Source folder ancestry contains a cycle.");
                if (complete.Contains(id)) continue;
                var parent = await _reader.ReadFolderAsync(id, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                if (parent.Id != id) throw new InvalidDataException("Source ancestry returned a different folder identity.");
                await VisitAsync(parent, depth + 1).ConfigureAwait(false);
            }
            active.Remove(folder.Id); complete.Add(folder.Id);
        }
    }
}
