using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public enum CurrentRootSigningDisposition { InitialPublished, Pending, Activated, Withdrawn }

/// <summary>Original native Root reservation and independently retained recovery
/// evidence. Observed and terminal status are historical, never current authority.</summary>
public sealed record CurrentRootSigningEvidence(ProtocolSigningIntentExpectation Intent,
    CurrentControlTransitionPlan? Transition, CurrentRootSigningDisposition Disposition,
    bool Observed, string? TerminalSha256);

/// <summary>Complete bounded native signing description captured under an actual
/// current nonce. It separates mutable Root work from immutable outbox expectations;
/// it grants no policy selection, publication, activation or generation retirement.</summary>
public sealed class CurrentControlSigningInventory
{
    public string ConnectionCode { get; }
    public Guid AttemptId { get; }
    public long AccountRevision { get; }
    public string RootSha256 { get; }
    public ProtocolSigningInventory Signing { get; }
    public ReadOnlyCollection<CurrentRootSigningEvidence> Roots { get; }
    public string Sha256 { get; }

    internal CurrentControlSigningInventory(string connectionCode, Guid attemptId, long accountRevision,
        string rootSha256, ProtocolSigningInventory signing, ReadOnlyCollection<CurrentRootSigningEvidence> roots, string sha256)
    {
        ConnectionCode=connectionCode;AttemptId=attemptId;AccountRevision=accountRevision;
        RootSha256=rootSha256;Signing=signing;Roots=roots;Sha256=sha256;
    }
}

public sealed class CurrentControlSigningInventoryChangedException() : InvalidOperationException(
    "Owned Root recovery or signing work changed during reconciliation. Preserve its original identities and capture it again.");

public sealed partial class CurrentControlTransitionJournal
{
    /// <summary>Require this exact actual captured/fenced nonce, then independently
    /// validate the initial slot/receipt, every Root plan/terminal and the COMPLETE
    /// native signing inventory in one read transaction. No Root kind is skipped.
    /// Unsigned, unobserved, withdrawn and activated reservations all remain visible.</summary>
    public CurrentControlSigningInventory CaptureSigningInventory(LocalCurrentControlAttempt expected, CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using var transaction=_database.Connection.BeginTransaction();
        var result=CaptureSigningInventoryIn(expected,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    /// <summary>A final fence of the SAME nonce/Root may recheck this description.
    /// Another nonce, signing mutation or terminal-only mutation invalidates it.
    /// This does not acknowledge an immutable scan or authorize any cloud operation.</summary>
    public void AssertSigningInventoryUnchanged(CurrentControlSigningInventory inventory,
        LocalCurrentControlAttempt expected, CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(expected);
        using var transaction=_database.Connection.BeginTransaction();
        var actual=CaptureSigningInventoryIn(expected,transaction,token);
        if(actual.Sha256!=inventory.Sha256) throw new CurrentControlSigningInventoryChangedException();
        token.ThrowIfCancellationRequested();transaction.Commit();
    }

    private CurrentControlSigningInventory CaptureSigningInventoryIn(LocalCurrentControlAttempt expected,
        SqliteTransaction transaction, CancellationToken token)
    {
        if(expected.Phase is not ("captured" or "fenced")) throw new CurrentControlObservationConflictException();
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(expected,transaction,token,expected.Phase);
        var plans=ReadAllIn(basis,transaction,token).ToDictionary(plan=>plan.Id);
        var signing=new ProtocolSigningJournal(_database,_budget).Inventory(basis.Pin.LibraryId,transaction);
        var observed=basis.ObservedRoots.ToHashSet();var initial=basis.ObservedRoots[0];
        if(initial!=expected.SlotId) throw new InvalidDataException("Owned Root inventory lost its independently retained initial slot.");
        var roots=new List<CurrentRootSigningEvidence>();
        foreach(var intent in signing.Intents.Where(intent=>intent.Kind==LibraryCurrentControl.Kind))
        {
            token.ThrowIfCancellationRequested();
            if(intent.ActorId!=basis.Pin.ControllerId || intent.Epoch!=basis.Pin.ControllerEpoch)
                throw new SigningHistoryRecoveryRequiredException();
            if(intent.Id==initial)
            {
                // TransitionBasisIn has independently verified the actual original
                // publication receipt, fixed allocated file and exact initial bytes.
                if(intent.EnvelopeSha256 is null || !observed.Contains(initial)) throw new SigningHistoryRecoveryRequiredException();
                roots.Add(new(intent,null,CurrentRootSigningDisposition.InitialPublished,true,null));continue;
            }
            if(!plans.TryGetValue(intent.Id,out var plan)) throw new SigningHistoryRecoveryRequiredException();
            var terminal=ReadCompletion(plan,transaction);
            var disposition=terminal is null ? CurrentRootSigningDisposition.Pending : terminal.Withdrawal is null ?
                CurrentRootSigningDisposition.Activated : CurrentRootSigningDisposition.Withdrawn;
            roots.Add(new(intent,plan,disposition,observed.Contains(intent.Id),terminal is null ? null : Hash(Encode(terminal))));
        }
        var retainedRoots=roots.Select(root=>root.Intent.Id).ToHashSet();
        if(roots.Count!=plans.Count+1 || !observed.IsSubsetOf(retainedRoots))
            throw new SigningHistoryRecoveryRequiredException();
        roots.Sort((left,right)=>left.Intent.Sequence.CompareTo(right.Intent.Sequence));
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("mpa-owned-current-signing-inventory-v1\0"u8);
        Append(new {ConnectionCode=basis.Pin.Encode(),expected.Id,expected.Revision,expected.AccountRevision,expected.Version,
            expected.ParentsSha256,expected.SharedDriveId,basis.MinimumVersion,basis.MinimumAccountRevision,
            RootSha256=basis.Head.EnvelopeSha256,SigningSha256=signing.Sha256});
        // Independently frame bounded entries; the complete inventory may exceed
        // CanonicalJson's array cap, which must remain unchanged.
        foreach(var root in roots) {token.ThrowIfCancellationRequested();Append(root);}
        return new(basis.Pin.Encode(),expected.Id,expected.AccountRevision,basis.Head.EnvelopeSha256,
            signing,roots.AsReadOnly(),Convert.ToHexStringLower(hash.GetHashAndReset()));

        void Append<T>(T value)
        {
            var bytes=new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(value,Json));
            Span<byte> length=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(length,bytes.Length);
            hash.AppendData(length);hash.AppendData(bytes);
        }
    }
}
