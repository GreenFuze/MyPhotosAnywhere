namespace Mpa.Processor.Web;

public sealed record DesktopAccount(string Id, string Name, bool Connected, long Revision);
public sealed record DesktopPendingAccount(Guid Id, string? ExpectedAccountId);
public sealed record DesktopSnapshot(Guid LibraryId, string State, IReadOnlyList<DesktopAccount> Accounts,
    IReadOnlyList<DesktopPendingAccount>? Pending = null, int CredentialCleanupCount = 0);
public enum DesktopAccountOperation { Connect, Check, Disconnect, Resume, Abandon, Cleanup }
public sealed record DesktopAccountCommand(DesktopAccountOperation Operation, string? AccountId, Guid? PendingId, long? ExpectedRevision);
public sealed record DesktopPhoto(string Id, string Name, string MimeType, int? Width, int? Height, string? CapturedTime, string? ModifiedTime);
public sealed record DesktopPhotoPage(IReadOnlyList<DesktopPhoto> Photos, string? NextCursor, bool IncompleteSearch);
public sealed record DesktopPreview(byte[] Bytes, string MimeType);

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
}
