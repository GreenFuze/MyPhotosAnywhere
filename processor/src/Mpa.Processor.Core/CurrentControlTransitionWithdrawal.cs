namespace Mpa.Processor.Core;

public enum CurrentControlTransitionWithdrawalReason { ConfigurationChanged, Cancelled }
public sealed class CurrentControlTransitionWithdrawnException() : InvalidOperationException("This fixed Root transition was withdrawn. Preserve its original work and capture a separate new transition.");

/// <summary>Historical private terminal receipt only. It does not undo an already
/// applied cloud Root, activate an index, complete a generation or release a snapshot.</summary>
public sealed record CurrentControlTransitionWithdrawal(Guid TransitionId,string ConnectionCode,string BodySha256,Guid FencedAttempt,
    string RootSha256,long RootSequence,long AccountRevision,long DriveVersion,CurrentControlTransitionWithdrawalReason Reason);

public sealed partial class CurrentControlTransitionJournal
{
    public IReadOnlyList<CurrentControlTransitionPlan> Pending(CancellationToken token=default)
    {
        using var transaction=_database.Connection.BeginTransaction();
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
        var result=new List<CurrentControlTransitionPlan>();
        foreach(var plan in ReadAllIn(basis,transaction,token))
        {token.ThrowIfCancellationRequested();if(ReadCompletion(plan,transaction) is null) result.Add(plan);}
        token.ThrowIfCancellationRequested();transaction.Commit();return result.AsReadOnly();
    }

    public CurrentControlTransitionWithdrawal? ReadWithdrawal(Guid id,CancellationToken token=default)
    {
        if(id==Guid.Empty) throw new ArgumentException("A fixed original transition identity is required.",nameof(id));
        using var transaction=_database.Connection.BeginTransaction();
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
        var work=ReadIntentIn(id,basis,transaction,token);var completion=ReadCompletion(work.Plan,transaction);
        var result=completion?.Withdrawal is null ? null : WithdrawalResult(work.Plan,completion);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    /// <summary>The live joined reader supplies an actual final Root fence. Obsolete
    /// target policies need not still match human settings. Only the original actual
    /// predecessor or exact desired native Root may terminate this work. No network,
    /// original or private-snapshot cleanup occurs, and the plan/body never change.</summary>
    public CurrentControlTransitionWithdrawal Withdraw(Guid id,LocalCurrentControlAttempt fenced,
        CurrentControlTransitionWithdrawalReason reason,CancellationToken token=default)
    {
        var name=WithdrawalReasonName(reason);
        if(id==Guid.Empty) throw new ArgumentException("A fixed original transition identity is required.",nameof(id));
        ArgumentNullException.ThrowIfNull(fenced);
        using var transaction=_database.Connection.BeginTransaction();var observations=new LocalCurrentControlObservations(_database,_budget);
        var historical=observations.TransitionBasisIn(null,transaction,token);var (plan,intent)=ReadIntentIn(id,historical,transaction,token);
        var old=ReadCompletion(plan,transaction);
        if(old is not null)
        {
            if(old.Withdrawal is null) throw new CurrentControlObservationConflictException();
            if(old.Withdrawal.Reason!=name) throw new CurrentControlObservationConflictException();
            var retained=WithdrawalResult(plan,old);token.ThrowIfCancellationRequested();transaction.Commit();return retained;
        }
        var basis=observations.TransitionBasisIn(fenced,transaction,token);
        if(fenced.AccountRevision<plan.AccountRevision || fenced.Version<plan.PreviousVersion ||
            basis.Head.EnvelopeSha256!=plan.PreviousSha256 && basis.Head.EnvelopeSha256!=intent.EnvelopeSha256 ||
            basis.Head.EnvelopeSha256==intent.EnvelopeSha256 && fenced.Version<=plan.PreviousVersion)
            throw new CurrentControlObservationConflictException();
        if(reason==CurrentControlTransitionWithdrawalReason.ConfigurationChanged)
        {
            var changed=false;
            try {AssertHumanTargetCurrent(Policies(basis.Pin,plan.Target,transaction),plan.Target,transaction);}
            catch(StaleLibraryConfigurationException) {changed=true;}
            if(!changed) throw new InvalidOperationException("Configuration withdrawal requires an actually superseded human configuration or policy.");
        }
        var receipt=new Activated(plan.Id,basis.Head.EnvelopeSha256,Guid.Empty,fenced.AccountRevision,fenced.Version!.Value)
            {Withdrawal=new(fenced.Id,plan.BodySha256,basis.Head.Sequence,name)};
        var encoded=Encode(receipt);using var reservation=_budget.Reserve(65536);reservation.RecheckBeforeWrite(reservation.Bytes);
        Put(transaction,Key(plan.Id)+":activated",encoded);Put(transaction,Key(plan.Id)+":activated:sha256",Hash(encoded));
        token.ThrowIfCancellationRequested();reservation.RecheckBeforeWrite(reservation.Bytes);transaction.Commit();return WithdrawalResult(plan,receipt);
    }

    private static CurrentControlTransitionWithdrawal WithdrawalResult(CurrentControlTransitionPlan plan,Activated value)
    {
        var evidence=value.Withdrawal ?? throw new InvalidDataException("A withdrawal result requires its exact terminal evidence.");
        var reason=evidence.Reason switch {"configuration-changed"=>CurrentControlTransitionWithdrawalReason.ConfigurationChanged,
            "cancelled"=>CurrentControlTransitionWithdrawalReason.Cancelled,_=>throw new InvalidDataException("Unknown terminal withdrawal reason.")};
        return new(plan.Id,plan.ConnectionCode,evidence.BodySha256,evidence.AttemptId,value.RootSha256,evidence.RootSequence,value.AccountRevision,value.DriveVersion,reason);
    }
    private static string WithdrawalReasonName(CurrentControlTransitionWithdrawalReason reason)=>reason switch
    {
        CurrentControlTransitionWithdrawalReason.ConfigurationChanged=>"configuration-changed",
        CurrentControlTransitionWithdrawalReason.Cancelled=>"cancelled",
        _=>throw new ArgumentOutOfRangeException(nameof(reason))
    };
    private static bool WithdrawalReasonKnown(string reason)=>reason is "configuration-changed" or "cancelled";
}
