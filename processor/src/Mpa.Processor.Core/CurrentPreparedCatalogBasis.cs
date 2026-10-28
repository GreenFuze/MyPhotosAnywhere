using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Immutable preparation progress, never a cloud permission or current
/// availability claim. The actual source configuration and parent are selected by
/// the current owner, rather than supplied by a caller.</summary>
public sealed record CurrentPreparedCatalogBasis(CapturedCatalogPublication Publication,AcceptedSourceConfiguration Sources);

public sealed partial class CatalogGenerationCaptureRepository
{
    public CurrentPreparedCatalogBasis PrepareCurrentPublicationBasis(Guid generationId,CurrentWorkspaceInventoryCheckpoint inventory,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        using var transaction=_database.Connection.BeginTransaction();
        var inputs=RequireCurrentBasisInputsIn(generationId,inventory,signing,fenced,publications,transaction,token);
        var capture=inputs.Capture;var scope=capture.Scope;var prior=ReadPublication(capture,transaction);
        var selected=ReadPublishedHead(transaction);
        if(inputs.Root.Catalog is null ? selected is not null : selected is null || selected.Commit!=inputs.Root.Catalog)
            throw new CatalogGenerationCaptureConflictException();
        var lineage=new OwnedCatalogLineageReader(this,scope,transaction,token);
        if(lineage.Publisher.KeyId!=inputs.Sources.PublisherKeyId) throw new SigningHistoryRecoveryRequiredException();
        var tip=lineage.ReadNativeTip(inputs.Root.Catalog);
        // Retain any already captured settings/history flags verbatim. New work
        // uses the same established defaults as the original preparation owner.
        var basis=new CapturedCatalogBasis(capture.Snapshot!,tip.Parent,tip.Sequence,inputs.Sources.Configuration.EnvelopeSha256,
            prior?.Basis.SettingsRevision ?? 0,prior?.Basis.MinimumHistoryGeneration ?? 1);
        using var mutation=CapturePublicationBasisIn(generationId,basis,null,transaction);
        var after=RequireCurrentBasisInputsIn(generationId,inventory,signing,fenced,publications,transaction,token);
        if(ReadPublication(after.Capture,transaction)!=mutation.Value || after.Sources.Configuration.Sequence!=inputs.Sources.Configuration.Sequence ||
            after.Sources.Configuration.EnvelopeSha256!=inputs.Sources.Configuration.EnvelopeSha256 ||
            after.Sources.PublisherKeyId!=inputs.Sources.PublisherKeyId)
            throw new InvalidDataException("Current publication basis changed during atomic preparation.");
        token.ThrowIfCancellationRequested();mutation.Recheck();transaction.Commit();return new(mutation.Value,inputs.Sources.Configuration);
    }

    private sealed record CurrentBasisSource(AcceptedSourceConfiguration Configuration,string PublisherKeyId);
    private sealed record CurrentBasisInputs(CatalogGenerationCapture Capture,LibraryCurrentControl Root,CurrentBasisSource Sources);

    private CurrentBasisInputs RequireCurrentBasisInputsIn(Guid generationId,CurrentWorkspaceInventoryCheckpoint inventory,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,
        SqliteTransaction transaction,CancellationToken token)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);token.ThrowIfCancellationRequested();
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        var actual=AssertCurrentHeaderPolicyInputsIn(signing,fenced,transaction,token);
        var capture=RequiredCapture(generationId,transaction);var scope=capture.Scope;
        if(capture.Phase is not("frozen" or "publication-planned") || capture.Snapshot is null) throw new CatalogGenerationCaptureConflictException();
        var authority=new LibraryAuthorityRepository(_database,_budget);authority.AssertLocalPublicationScope(scope,transaction);
        var publisher=authority.CurrentPublisherPolicy(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        var sources=authority.CurrentSourceConfiguration(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        if(publisher.Sequence!=actual.Head.Publishers.Sequence || publisher.EnvelopeSha256!=actual.Head.Publishers.Reference.Sha256 ||
            sources.Sequence!=actual.Head.Sources.Sequence || sources.EnvelopeSha256!=actual.Head.Sources.Reference.Sha256 ||
            scope.LibraryId!=inventory.Context.LibraryId || scope.AccountId!=inventory.Context.AccountId ||
            scope.FolderId!=inventory.Context.FolderId || scope.AccountRevision!=inventory.Context.AccountRevision)
            throw new StaleLibraryConfigurationException();
        var journal=new ProtocolSigningJournal(_database,_budget);var history=new LocalPublisherHistory();
        foreach(var id in actual.ObservedRoots)
        {
            token.ThrowIfCancellationRequested();var intent=journal.Read(id,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
            var root=LibraryCurrentControl.Read(intent.Envelope!.Value.Span,actual.Pin);
            history.Observe(root.ReadPublishers(journal.RetainedControllerReference(actual.Pin,root.Publishers,"library-publisher-authorization",transaction).Span,actual.Pin));
        }
        history.Observe(publisher);
        if(journal.Read(capture.CommitIntentId,transaction) is not null || ReadPublication(capture,transaction)?.Plan is not null)
            throw new CatalogGenerationCaptureConflictException();
        var roots=signing.Roots.Select(value=>value.Intent.Id).ToHashSet();
        if(signing.Signing.Intents.Any(value=>!roots.Contains(value.Id) && value.EnvelopeSha256 is null)) throw new SigningHistoryRecoveryRequiredException();
        var terminal=new Dictionary<Guid,bool>();
        foreach(var work in publications.Objects)
        {
            token.ThrowIfCancellationRequested();
            if(work.VerifiedVersion is null && work.Scope!=scope && !IsCurrentPartialPublicationDisposedIn(work.Id,transaction,terminal))
                throw new CatalogGenerationInProgressException();
        }
        var witnessed=signing.Signing.Intents.Where(value=>value.EnvelopeSha256 is not null)
            .Select(value=>(Sha256:value.EnvelopeSha256!,Bytes:(long)value.EnvelopeBytes!.Value)).ToHashSet();
        foreach(var work in publications.Objects.Where(value=>value.MimeType=="application/json")) witnessed.Add((work.Sha256,work.Bytes));
        foreach(var item in new WorkspaceObservationRepository(_database,_budget).CaptureCurrentHistoryIn(signing,fenced,transaction,token).ImmutableObservations)
        {
            token.ThrowIfCancellationRequested();
            if(item.MimeType is null && !witnessed.Contains((item.Sha256,item.Bytes))) throw new SigningHistoryRecoveryRequiredException();
        }
        token.ThrowIfCancellationRequested();return new(capture,actual.Head,new(sources,publisher.PublisherKeyId ?? throw new CatalogPublicationNotAuthorizedException()));
    }
}
