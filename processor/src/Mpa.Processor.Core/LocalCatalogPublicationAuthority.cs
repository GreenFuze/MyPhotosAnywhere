using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed class CatalogPublicationNotAuthorizedException() : InvalidOperationException("Catalog publishing needs a current authorized processor and approved source configuration.");
public sealed record LocalCatalogPublicationAuthority(Guid LibraryId, Guid ActorId, long ActorEpoch, string AccountId, long AccountRevision,
    string FolderId, long ConfigurationRevision, string PublisherKeyId, string AuthoritySha256);

public sealed partial class LibraryAuthorityRepository
{
    /// <summary>Capture current durable local policies in one transaction. This is only
    /// the local policy fence: it grants no cloud reconciliation, predecessor, native
    /// key possession, failed-observation barrier or single-head publication admission.</summary>
    public LocalCatalogPublicationAuthority CaptureLocalPublicationAuthority()
    {
        using var transaction = _database.Connection.BeginTransaction(); var result = ReadLocalPublicationAuthority(transaction);
        transaction.Commit(); return result;
    }

    public void AssertLocalPublicationScope(WorkspacePublicationScope scope, CatalogGenerationLocalAccess? localAccess = null)
    {
        ArgumentNullException.ThrowIfNull(scope); using var transaction = _database.Connection.BeginTransaction();
        AssertLocalPublicationScope(scope, transaction, localAccess); transaction.Commit();
    }

    internal void AssertLocalPublicationScope(WorkspacePublicationScope scope, SqliteTransaction transaction, CatalogGenerationLocalAccess? localAccess = null)
    {
        if (localAccess is not null) { localAccess.AssertCurrent(_database, scope, transaction); return; }
        var current = ReadLocalPublicationAuthority(transaction);
        if (scope.LibraryId != current.LibraryId || scope.ActorId != current.ActorId || scope.ActorEpoch != current.ActorEpoch ||
            scope.AccountId != current.AccountId || scope.AccountRevision != current.AccountRevision || scope.FolderId != current.FolderId ||
            scope.ConfigurationRevision != current.ConfigurationRevision || scope.AuthoritySha256 != current.AuthoritySha256 || scope.GenerationId == Guid.Empty)
            throw new StaleLibraryConfigurationException();
    }

    internal LocalCatalogPublicationAuthority ReadLocalPublicationAuthority(SqliteTransaction transaction)
    {
        var pin = ReadPin(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        var publisherSequence = SelectedSequence(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        var publisher = ReadPolicy(transaction, pin, Counter(publisherSequence));
        var actor = publisher.Publisher ?? throw new CatalogPublicationNotAuthorizedException();
        var source = ReadCurrentSources(transaction, pin) ?? throw new CatalogPublicationNotAuthorizedException();
        var configurationRevision = AssertHumanSources(source.Configuration,transaction);
        using var account = Command(transaction, "SELECT revision FROM drive_account WHERE permission_id=$account AND connected=1", ("$account", pin.AccountId));
        if (account.ExecuteScalar() is not long accountRevision || accountRevision < 1) throw new StaleLibraryConfigurationException();
        // Credential revision stays in the captured access scope. Reconnection cannot
        // change human source authority or the signed publisher/source-policy digest.
        var descriptor=new LocalPublicationAuthorityDescriptor(pin.LibraryId,pin.ActorId,pin.Epoch,pin.KeyId,
            actor.ActorId,actor.Epoch,publisher.PublisherKeyId ?? throw new InvalidDataException("Active publisher lost its public key identity."),
            Counter(publisherSequence),publisher.EnvelopeSha256,source.Sequence,source.EnvelopeSha256,pin.AccountId,pin.FolderId,configurationRevision);
        return new(pin.LibraryId, actor.ActorId, actor.Epoch, pin.AccountId, accountRevision, pin.FolderId, configurationRevision,
            publisher.PublisherKeyId ?? throw new InvalidDataException("Active publisher lost its public key identity."), descriptor.Sha256());
    }

    /// <summary>Local human source approval only. Unlike publication authority this
    /// remains available when the selected publisher has been revoked.</summary>
    internal long AssertHumanSources(Mpa.Processor.Protocol.CatalogSourceConfiguration approved,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(approved);
        var settings = new LibrarySourceRepository(_database).Snapshot(transaction);
        if (approved.Revision != settings.Revision || approved.Sources.Count != settings.Sources.Count) throw new StaleLibraryConfigurationException();
        var sources = approved.Sources.ToDictionary(item => item.Id);
        foreach (var row in settings.Sources)
            if (!sources.TryGetValue(row.Id, out var policy) || policy.AccountId != row.AccountId || policy.FolderId != row.FolderId ||
                policy.Revision != row.Revision || policy.IncludeDescendants != row.IncludeDescendants || policy.Enabled != row.Enabled) throw new StaleLibraryConfigurationException();
        return settings.Revision;
    }
}
