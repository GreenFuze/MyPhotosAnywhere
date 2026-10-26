using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

internal sealed record LocalCurrentTransitionBasis(LibraryConnectionDescriptor Pin,LibraryCurrentControl Head,ReadOnlyCollection<Guid> ObservedRoots,
    string? SharedDriveId,long MinimumVersion,long MinimumAccountRevision);

public sealed partial class LocalCurrentControlObservations
{
    /// <summary>Historical Root history, or this exact live fenced nonce when supplied.
    /// It borrows one transaction and is never a cloud condition or original grant.</summary>
    internal LocalCurrentTransitionBasis TransitionBasisIn(LocalCurrentControlAttempt? expected,SqliteTransaction transaction,CancellationToken token,string phase="fenced")
    {
        var state=expected is null ? Load(transaction,token) : Require(expected,phase,transaction,token);
        return new(state.Connection,state.Head,Array.AsReadOnly(state.Saved?.Heads ?? [state.Slot.Id]),state.InitialDriveId,state.Receipt.DriveVersion,state.Receipt.AccountRevision);
    }
}
