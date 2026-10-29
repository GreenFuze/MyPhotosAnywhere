using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>One exact current dependency receipt, in the same transaction as
    /// current admission. Its local delta spends inventory and grants no later IO.</summary>
    public WorkspacePublicationRecord ConfirmCurrentCatalogDependencyReceipt(CurrentCatalogDependencyCloudInspection expected,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,
        string account,string folder,string file,string mime,long version,ReadOnlyMemory<byte> bytes,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(expected);using var transaction=_database.Connection.BeginTransaction();
        var actual=ReadCurrentDependencyCloudInputsIn(expected.Dependency.Id,expected.Inventory,signing,fenced,publications,transaction,token);
        var row=actual.Dependency;var prior=expected.Dependency;
        if(actual.Publication!=expected.Publication || actual.Inventory!=expected.Inventory || row.Id!=prior.Id || row.Scope!=prior.Scope ||
            row.Role!=prior.Role || row.MimeType!=prior.MimeType || row.Sha256!=prior.Sha256 || row.FileId!=prior.FileId ||
            row.VerifiedVersion!=prior.VerifiedVersion || !row.Payload.Span.SequenceEqual(prior.Payload.Span))
            throw new WorkspacePublicationConflictException();
        if(row.FileId is null || account!=row.Scope.AccountId || folder!=row.Scope.FolderId || file!=row.FileId || mime!=row.MimeType ||
            version<1 || row.VerifiedVersion is {} savedVersion && savedVersion!=version || bytes.Length!=row.Payload.Length ||
            !row.Payload.Span.SequenceEqual(bytes.Span) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.Span),Convert.FromHexString(row.Sha256)))
            throw new WorkspacePublicationConflictException();
        token.ThrowIfCancellationRequested();if(row.VerifiedVersion is not null){transaction.Commit();return row;}
        using var storage=_budget.Reserve(65536);storage.RecheckBeforeWrite(storage.Bytes);
        using(var update=Command(transaction,"UPDATE workspace_publication SET verified_version=$version WHERE id=$id AND file_id=$file AND verified_version IS NULL",
            ("$version",version),("$id",row.Id.ToString("D")),("$file",file)))
            if(update.ExecuteNonQuery()!=1) throw new WorkspacePublicationConflictException();
        var journal=new WorkspacePublicationRepository(_database,_budget);var saved=journal.Read(row.Id,transaction)??throw new WorkspacePublicationConflictException();
        if(saved.Id!=row.Id || saved.Scope!=row.Scope || saved.Role!=row.Role || saved.MimeType!=row.MimeType || saved.Sha256!=row.Sha256 ||
            saved.FileId!=file || saved.VerifiedVersion!=version || !saved.Payload.Span.SequenceEqual(row.Payload.Span)) throw new WorkspacePublicationConflictException();
        journal.AssertOnlyCurrentDependencyReceiptChangedIn(publications,row.Id,version,transaction);
        new WorkspaceObservationRepository(_database,_budget).AssertHeaderReceiptObservationHistoryUnchangedIn(expected.Inventory,signing,fenced,transaction,token);
        _=new CurrentControlTransitionJournal(_database,_budget).RequireSigningInventoryIn(signing,fenced,transaction,token);
        var authority=new LibraryAuthorityRepository(_database,_budget);authority.AssertLocalPublicationScope(row.Scope,transaction);
        var capture=RequiredCapture(row.Scope.GenerationId,transaction);
        if(capture!=actual.Publication.Capture || ReadPublication(capture,transaction)!=actual.Publication) throw new CatalogGenerationCaptureConflictException();
        var root=AssertCurrentHeaderPolicyInputsIn(signing,fenced,transaction,token).Head;
        var publisher=authority.CurrentPublisherPolicy(transaction)??throw new CatalogPublicationNotAuthorizedException();
        var sources=authority.CurrentSourceConfiguration(transaction)??throw new CatalogPublicationNotAuthorizedException();
        if(publisher.Sequence!=root.Publishers.Sequence || publisher.EnvelopeSha256!=root.Publishers.Reference.Sha256 ||
            sources.Sequence!=root.Sources.Sequence || sources.EnvelopeSha256!=root.Sources.Reference.Sha256) throw new StaleLibraryConfigurationException();
        AssertPreparedDependencyBasisIn(actual.Publication,root,new(sources,publisher.PublisherKeyId??throw new CatalogPublicationNotAuthorizedException()),transaction,token);
        token.ThrowIfCancellationRequested();storage.RecheckBeforeWrite(storage.Bytes);transaction.Commit();return saved;
    }
}

public sealed partial class WorkspacePublicationRepository
{
    // Exact LOCAL receipt delta only. No checkpoint or renewed grant is returned.
    internal void AssertOnlyCurrentDependencyReceiptChangedIn(WorkspacePublicationInventory before,Guid id,long version,SqliteTransaction transaction)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Dependency receipt belongs to another transaction.");
        if(InventoryDigest(before.Context,before.Maximum,before.Objects)!=before.Sha256) throw new WorkspacePublicationConflictException();
        if(before.Objects.Count(value=>value.Id==id && value.Role is("facts-chunk" or "facts-index" or "coverage-page" or "coverage-root") &&
            value.FileId is not null && value.VerifiedVersion is null)!=1) throw new WorkspacePublicationConflictException();
        var expected=before.Objects.Select(value=>value.Id==id?value with{VerifiedVersion=version}:value).ToArray();
        if(Inventory(before.Context,before.Maximum,transaction).Sha256!=InventoryDigest(before.Context,before.Maximum,expected))
            throw new WorkspacePublicationInventoryChangedException();
    }
}
