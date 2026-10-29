using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

internal sealed record LocalCurrentFencedSelection(LibraryConnectionDescriptor Pin,LibraryCurrentControl Head,
    CatalogPublisherAuthorization Publishers,CatalogSourceConfiguration Sources,ReadOnlyCollection<CatalogObjectReference> RequiredCatalogs);

public sealed partial class LocalCurrentControlObservations
{
    /// <summary>Borrow the atomic caller's transaction. Own Root policy signatures
    /// must exist in its retained journal, and observed revocations/epochs cannot be
    /// undone by choosing a newly signed policy for a previously retired identity.</summary>
    internal LocalCurrentFencedSelection FencedSelectionIn(LocalCurrentControlAttempt expected,ReadOnlySpan<byte> publisher,
        ReadOnlySpan<byte> sources,SqliteTransaction transaction,CancellationToken token)
    {
        var state=Require(expected,"fenced",transaction,token);var journal=new ProtocolSigningJournal(_database,_budget);
        var selected=state.Head.ReadPublishers(publisher,state.Connection);var source=state.Head.ReadSources(sources,state.Connection);
        if(journal.AssertRetainedCloudEnvelope(state.Connection.ControllerId,state.Connection.ControllerEpoch,publisher,transaction).Kind!="library-publisher-authorization" ||
            journal.AssertRetainedCloudEnvelope(state.Connection.ControllerId,state.Connection.ControllerEpoch,sources,transaction).Kind!="library-source-configuration") throw new ProtocolSigningConflictException();
        var history=new LocalPublisherHistory();var catalogs=new Dictionary<string,CatalogObjectReference>(StringComparer.Ordinal);
        foreach(var id in state.Saved!.Heads)
        {
            token.ThrowIfCancellationRequested();var rootIntent=journal.Read(id,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
            var root=LibraryCurrentControl.Read(rootIntent.Envelope!.Value.Span,state.Connection);
            var envelope=journal.RetainedControllerReference(state.Connection,root.Publishers,"library-publisher-authorization",transaction);
            var policy=root.ReadPublishers(envelope.Span,state.Connection);
            history.Observe(policy);
            if(policy.Publisher?.ActorId==selected.Publisher?.ActorId && policy.Publisher?.Epoch==selected.Publisher?.Epoch &&
                policy.PublisherKeyId==selected.PublisherKeyId && root.Catalog is { } reference)
            {
                if(catalogs.TryGetValue(reference.FileId,out var known) && known!=reference) throw new ProtocolSigningConflictException();
                catalogs[reference.FileId]=reference;
            }
        }
        return new(state.Connection,state.Head,selected,source,catalogs.Values.ToList().AsReadOnly());
    }
}
