using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

internal sealed record CurrentOwnedCatalogRootRecovery(int WithdrawnPlans,string Sha256);

public sealed partial class CurrentControlTransitionJournal
{
    /// <summary>Original native metadata/terminal binding only, borrowing an already
    /// validated capture/plan to avoid phase -> full Root history -> phase recursion.
    /// It grants no live authority, phase change, file cleanup or successor admission.
    /// Normal ReadAll still performs the full captured-Catalog/phase checks.</summary>
    internal CurrentOwnedCatalogRootRecovery ReadOwnedCatalogRootRecoveryIn(CurrentOwnedCatalogSelection captured,
        SqliteTransaction transaction,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(captured);ArgumentNullException.ThrowIfNull(transaction);
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Original Root recovery requires its owning transaction.");
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
        var capture=captured.Publication.Capture;var scope=capture.Scope;var work=captured.Work;
        if(scope.LibraryId!=basis.Pin.LibraryId || scope.AccountId!=basis.Pin.Workspace.AccountId || scope.FolderId!=basis.Pin.Workspace.FolderId ||
            work.FileId is null || work.VerifiedVersion is null || work.Scope!=scope || work.Id!=capture.CommitIntentId ||
            captured.Intent.Envelope is null || captured.Intent.EnvelopeSha256!=work.Sha256 || !captured.Intent.Envelope.Value.Span.SequenceEqual(work.Payload.Span))
            throw new InvalidDataException("Original Root recovery lost its exact owned native catalog.");
        var reference=new CatalogObjectReference(scope.AccountId,work.FileId,scope.FolderId,work.Payload.Length,work.Sha256);
        var rows=ReadOriginalTransitionRowsIn(transaction,token);var ids=OriginalTransitionIds(rows).ToArray();var count=0;
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData("mpa-owned-catalog-root-recovery-v1\0"u8);
        foreach(var id in ids)
        {
            token.ThrowIfCancellationRequested();var original=ReadOriginalTransitionInputsIn(id,rows,basis,transaction,token);
            var plan=original.Plan;AssertOriginalTransitionEnvelope(original,basis.Pin);
            var terminal=ReadCompletion(plan,transaction);
            if(plan.GenerationId!=scope.GenerationId) continue;
            if(plan.Target.CatalogPublicationId!=capture.CommitIntentId || plan.Catalog!=reference || plan.ConfigurationRevision!=scope.ConfigurationRevision ||
                plan.Target.Sources.Reference.Sha256!=captured.Publication.Basis.SourcePolicySha256 || scope.AccountRevision>plan.AccountRevision ||
                original.Policies.Sources.Revision!=scope.ConfigurationRevision || original.Policies.Publisher.Publisher is not {} publisher ||
                publisher.ActorId!=scope.ActorId || publisher.Epoch!=scope.ActorEpoch || terminal?.Withdrawal is null)
                throw new InvalidDataException("Original catalog Root work is not an exact independently withdrawn native binding.");
            var verified=new SignedProtocolDocuments([CatalogPublicationReader.Capability,DriveCatalogFacts.Capability])
                .VerifyAndRead(captured.Intent.Envelope.Value.Span,publisher);
            _=new CatalogPublicationReader(basis.Pin.Workspace).ReadCommit(verified);
            count=checked(count+1);Append(new {plan,TerminalSha256=Hash(Encode(terminal))});
        }
        AssertOriginalRootCoverageIn(ids,basis,transaction,token);
        Append(new {scope,capture.SnapshotId,capture.CommitIntentId,captured.Intent.Sequence,captured.PlanSha256,WithdrawnPlans=count});
        token.ThrowIfCancellationRequested();return new(count,Convert.ToHexStringLower(hash.GetHashAndReset()));

        void Append<T>(T value)
        {
            var bytes=new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(value,Json));
            Span<byte> size=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(size,bytes.Length);hash.AppendData(size);hash.AppendData(bytes);
        }
    }
}
