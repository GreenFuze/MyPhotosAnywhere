using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CurrentControlTransitionJournal
{
    public CurrentControlTransitionPlan AssertCapturedCurrent(Guid id,LocalCurrentControlAttempt capture,CancellationToken token=default)
    {
        using var transaction=_database.Connection.BeginTransaction();
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(capture,transaction,token,"captured");
        var (plan,intent)=ReadIntentIn(id,basis,transaction,token);AssertCurrentIn(plan,intent,basis,capture,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return plan;
    }

    /// <summary>Historical fixed work only. A sealed native signature is not a
    /// current transport condition, complete target graph or permission to write.</summary>
    public (CurrentControlTransitionPlan Plan,ProtocolSigningIntent Intent) ReadForSigning(Guid id,CancellationToken token=default)
    {
        using var transaction=_database.Connection.BeginTransaction();
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
        var result=ReadIntentIn(id,basis,transaction,token);
        if(ReadCompletion(result.Plan,transaction)?.Withdrawal is not null) throw new CurrentControlTransitionWithdrawnException();
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    /// <summary>Local pre-update checks. Only the live joined semantic owner can
    /// supply the complete target traversal and an actual Google-owned condition.
    /// No state is promoted, snapshot released or original authorized here.</summary>
    public CurrentControlTransitionPlan AssertPublicationCurrent(Guid id,LocalCurrentControlAttempt fenced,
        IReadOnlyList<LocalCurrentGraphObservation> targetObservations,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(targetObservations);
        if(targetObservations.Count>WorkspaceObservationRepository.MaximumDocuments) throw new CatalogTraversalLimitException();
        using var transaction=_database.Connection.BeginTransaction();
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(fenced,transaction,token);
        var (plan,intent)=ReadIntentIn(id,basis,transaction,token);AssertCurrentIn(plan,intent,basis,fenced,transaction,token);
        var actuals=new LocalCurrentGraphObservations(_database,_budget);var identities=new HashSet<string>(StringComparer.Ordinal);
        foreach(var expected in targetObservations)
        {
            token.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(expected);
            if(!identities.Add(expected.Reference.FileId) || actuals.RequireCompleteIn(fenced,basis.Pin,expected.Reference,transaction)!=expected)
                throw new CurrentControlObservationConflictException();
        }
        _=actuals.RequireCompleteIn(fenced,basis.Pin,plan.Target.Publishers.Reference,transaction);
        _=actuals.RequireCompleteIn(fenced,basis.Pin,plan.Target.Sources.Reference,transaction);
        if(plan.Catalog is { } catalog && !identities.Contains(catalog.FileId) || plan.Catalog is null && identities.Count!=0)
            throw new CurrentControlObservationConflictException();
        token.ThrowIfCancellationRequested();transaction.Commit();return plan;
    }

    internal LibraryConnectionDescriptor AssertCapturedTargetPolicyIn(Guid id,LocalCurrentControlAttempt capture,CatalogObjectReference reference,
        SqliteTransaction transaction,CancellationToken token)
    {
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(capture,transaction,token,"captured");
        var (plan,intent)=ReadIntentIn(id,basis,transaction,token);AssertCurrentIn(plan,intent,basis,capture,transaction,token);
        if(reference!=plan.Target.Publishers.Reference && reference!=plan.Target.Sources.Reference) throw new CurrentControlObservationConflictException();
        return basis.Pin;
    }

    private (CurrentControlTransitionPlan Plan,ProtocolSigningIntent Intent) ReadIntentIn(Guid id,LocalCurrentTransitionBasis basis,
        SqliteTransaction transaction,CancellationToken token)
    {
        var plan=ReadAllIn(basis,transaction,token).SingleOrDefault(value=>value.Id==id) ?? throw new SigningHistoryRecoveryRequiredException();
        return(plan,new ProtocolSigningJournal(_database,_budget).Read(id,transaction) ?? throw new SigningHistoryRecoveryRequiredException());
    }

    private void AssertCurrentIn(CurrentControlTransitionPlan plan,ProtocolSigningIntent intent,LocalCurrentTransitionBasis basis,
        LocalCurrentControlAttempt attempt,SqliteTransaction transaction,CancellationToken token)
    {
        if(ReadCompletion(plan,transaction)?.Withdrawal is not null) throw new CurrentControlTransitionWithdrawnException();
        if(intent.Envelope is null || attempt.AccountRevision<plan.AccountRevision || attempt.Version<plan.PreviousVersion ||
            basis.Head.EnvelopeSha256!=plan.PreviousSha256 && basis.Head.EnvelopeSha256!=intent.EnvelopeSha256 ||
            basis.Head.EnvelopeSha256==intent.EnvelopeSha256 && attempt.Version<=plan.PreviousVersion)
            throw new CurrentControlObservationConflictException();
        var policies=Policies(basis.Pin,plan.Target,transaction);
        AssertHumanTargetCurrent(policies,plan.Target,transaction);
        var history=new LocalPublisherHistory();var journal=new ProtocolSigningJournal(_database,_budget);
        foreach(var rootId in basis.ObservedRoots)
        {
            token.ThrowIfCancellationRequested();var rootIntent=journal.Read(rootId,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
            var root=LibraryCurrentControl.Read(rootIntent.Envelope!.Value.Span,basis.Pin);
            history.Observe(root.ReadPublishers(journal.RetainedControllerReference(basis.Pin,root.Publishers,"library-publisher-authorization",transaction).Span,basis.Pin));
        }
        history.Observe(policies.Publisher);
    }

    private void AssertHumanTargetCurrent(PolicyEvidence policies,CurrentControlTransitionTarget target,SqliteTransaction transaction)
    {
        var authority=new LibraryAuthorityRepository(_database,_budget);
        var localPublisher=authority.CurrentPublisherPolicy(transaction);var localSources=authority.CurrentSourceConfiguration(transaction);
        // A local human policy can supersede pending work before its own Root is
        // published, even when source rows/revision have not changed. Never revive
        // the older policy merely because the actual cloud Root is still its parent.
        if(localPublisher is not null && (localPublisher.Sequence>target.Publishers.Sequence ||
                localPublisher.Sequence==target.Publishers.Sequence && localPublisher.EnvelopeSha256!=target.Publishers.Reference.Sha256) ||
            localSources is not null && (localSources.Sequence>target.Sources.Sequence ||
                localSources.Sequence==target.Sources.Sequence && localSources.EnvelopeSha256!=target.Sources.Reference.Sha256))
            throw new StaleLibraryConfigurationException();
        authority.AssertHumanSources(policies.Sources,transaction);
    }
}
