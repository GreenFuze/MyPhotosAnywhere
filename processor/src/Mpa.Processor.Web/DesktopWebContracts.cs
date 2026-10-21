namespace Mpa.Processor.Web;

public sealed record DesktopAccount(string Id, string Name, bool Connected, long Revision);
public sealed record DesktopPendingAccount(Guid Id, string? ExpectedAccountId);
public sealed record DesktopSnapshot(Guid LibraryId, string State, IReadOnlyList<DesktopAccount> Accounts,
    IReadOnlyList<DesktopPendingAccount>? Pending = null, int CredentialCleanupCount = 0, Mpa.Processor.Core.StorageBudgetStatus? StorageAttention = null);
public enum DesktopAccountOperation { Connect, Check, Disconnect, Resume, Abandon, Cleanup }
public sealed record DesktopAccountCommand(DesktopAccountOperation Operation, string? AccountId, Guid? PendingId, long? ExpectedRevision);
public sealed record DesktopPhoto(string Id, string Name, string MimeType, int? Width, int? Height, string? CapturedTime, string? ModifiedTime);
public sealed record DesktopPhotoPage(IReadOnlyList<DesktopPhoto> Photos, string? NextCursor, bool IncompleteSearch);
public sealed record DesktopPreview(byte[] Bytes, string MimeType);
public sealed record DesktopFolder(string Id, string Name, bool CanListChildren);
public sealed record DesktopFolderLocation(DesktopFolder Current, IReadOnlyList<DesktopFolder> Children, string? NextCursor, bool IncompleteSearch);
public sealed record DesktopAddSource(string AccountId, string FolderId, bool IncludeDescendants, long ExpectedRevision);
public sealed record DesktopUpdateSource(Guid Id, bool Enabled, bool IncludeDescendants, long ExpectedSourceRevision, long ExpectedRevision);

/// <summary>UI-independent bounded operations. Requests may cancel reads; admitted durable
/// jobs are owned by the engine and expose captured IDs/status through separate operations.</summary>
public interface IDesktopWebApplication
{
    Task<DesktopSnapshot> SnapshotAsync(CancellationToken cancellationToken);
    Task<DesktopPhotoPage> PhotosAsync(string accountId, string? cursor, CancellationToken cancellationToken);
    Task<DesktopPreview?> PreviewAsync(string accountId, string fileId, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task AccountAsync(DesktopAccountCommand command, CancellationToken cancellationToken);
    Task<Mpa.Processor.Core.LibrarySourceSnapshot> SourcesAsync(CancellationToken cancellationToken);
    Task<DesktopFolderLocation> FoldersAsync(string accountId, string folderId, string? cursor, CancellationToken cancellationToken);
    Task<Mpa.Processor.Core.LibrarySourceSnapshot> AddSourceAsync(DesktopAddSource request, CancellationToken cancellationToken);
    Task<Mpa.Processor.Core.LibrarySourceSnapshot> UpdateSourceAsync(DesktopUpdateSource request, CancellationToken cancellationToken);
    Task<Mpa.Processor.Core.SourceDiscoveryActivityPage> ActivityAsync(Guid? cursor, CancellationToken cancellationToken);
    Task<Mpa.Processor.Core.SourceDiscoverySchedule> RequestScanAsync(Mpa.Processor.Core.SourceScanRequest request, CancellationToken cancellationToken);
    Task<int> RetrySourceAsync(Mpa.Processor.Core.SourceRetryRequest request, CancellationToken cancellationToken);
}
