using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>An actual response names an already owned control, metadata or source
/// object instead of a new allocation. It must not become a retry checkpoint.</summary>
public sealed class WorkspaceAllocationAliasException() : InvalidOperationException("The returned allocation aliases an existing protected object.");

internal interface IReturnedOriginalDependencyAllocationContext
{
    (CapturedCatalogPublication Publication,WorkspacePublicationRecord Expected,string LocalStateSha256,string FileId)
        Resolve(ProcessorStateDatabase database,SqliteTransaction transaction);
}

public sealed partial class WorkspacePublicationRepository
{
    internal WorkspacePublicationWrite RetainOriginalDependencyAllocationIn(IReturnedOriginalDependencyAllocationContext context,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);ArgumentNullException.ThrowIfNull(transaction);
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Dependency checkpoint requires its owning transaction.");
        var original=context.Resolve(_database,transaction);var expected=original.Expected;var file=original.FileId;var scope=original.Publication.Capture.Scope;
        if(!Identity(file) || file is "root" || file==scope.FolderId || expected.Scope!=scope) throw new WorkspacePublicationConflictException();
        var actual=Read(expected.Id,transaction) ?? throw new WorkspacePublicationConflictException();
        if(actual.Scope!=scope || actual.Role!=expected.Role || actual.MimeType!=expected.MimeType || actual.Sha256!=expected.Sha256 ||
            !actual.Payload.Span.SequenceEqual(expected.Payload.Span) || actual.FileId is not null && actual.FileId!=file)
            throw new WorkspacePublicationConflictException();
        var slot=new LibraryCurrentControlSlotRepository(_database,_budget).ReadIn(transaction);
        if(slot?.FileId==file) throw new WorkspaceAllocationAliasException();
        using(var alias=Command(transaction,"""
            SELECT 1 WHERE EXISTS(SELECT 1 FROM workspace_publication WHERE account_id=$account AND file_id=$file AND id<>$id)
                OR ($unallocated=1 AND EXISTS(SELECT 1 FROM workspace_document_observation WHERE id=$file))
                OR EXISTS(SELECT 1 FROM discovered_asset WHERE account_id IN(SELECT permission_id FROM drive_account) AND file_id=$file)
            """,("$account",scope.AccountId),("$file",file),("$id",actual.Id.ToString("D")),("$unallocated",actual.FileId is null?1:0)))
            if(alias.ExecuteScalar() is not null) throw new WorkspaceAllocationAliasException();
        if(actual.FileId is not null) return new(actual,null);
        if(actual.VerifiedVersion is not null) throw new WorkspacePublicationConflictException();
        var beforeOutbox=OriginalDependencyAllocationOutboxShaIn(actual.Id,transaction);
        StorageReservation? storage=_budget.Reserve(65536);
        try
        {
            storage.RecheckBeforeWrite(storage.Bytes);
            using(var update=Command(transaction,"UPDATE workspace_publication SET file_id=$file WHERE id=$id AND file_id IS NULL AND verified_version IS NULL",
                ("$file",file),("$id",actual.Id.ToString("D"))))
                if(update.ExecuteNonQuery()!=1) throw new InvalidOperationException("Known dependency allocation lost its original pending row.");
            var after=Read(actual.Id,transaction) ?? throw new WorkspacePublicationConflictException();
            if(after.Scope!=actual.Scope || after.Role!=actual.Role || after.MimeType!=actual.MimeType || after.Sha256!=actual.Sha256 ||
                after.FileId!=file || after.VerifiedVersion is not null || !after.Payload.Span.SequenceEqual(actual.Payload.Span))
                throw new WorkspacePublicationConflictException();
            var checkedState=context.Resolve(_database,transaction);
            if(checkedState.Publication!=original.Publication || checkedState.LocalStateSha256!=original.LocalStateSha256)
                throw new WorkspacePublicationConflictException();
            if(OriginalDependencyAllocationOutboxShaIn(actual.Id,transaction)!=beforeOutbox)
                throw new WorkspacePublicationInventoryChangedException();
            var result=new WorkspacePublicationWrite(after,storage);storage=null;return result;
        }
        finally {storage?.Dispose();}
    }
    // LOCAL before/after mutation fingerprint, never an inventory or provider
    // capability. It deliberately needs no current access/credential revision:
    // a returned ID must survive disconnection. Only this target's FileId may
    // differ; every other bounded outbox identity, byte hash and receipt remains.
    private string OriginalDependencyAllocationOutboxShaIn(Guid target,SqliteTransaction transaction)
    {
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData("mpa-original-dependency-allocation-local-outbox-v1\0"u8);
        Span<byte> frame=stackalloc byte[4];
        foreach(var id in Identities(4096,transaction))
        {
            var row=Read(id,transaction) ?? throw new WorkspacePublicationConflictException();
            var value=new WorkspacePublicationExpectation(row.Id,row.Scope,row.Role,row.MimeType,row.Payload.Length,
                row.Sha256,row.Id==target?null:row.FileId,row.VerifiedVersion);
            var bytes=new CanonicalJson(16384).Encode(JsonSerializer.SerializeToUtf8Bytes(value));
            BinaryPrimitives.WriteInt32BigEndian(frame,bytes.Length);digest.AppendData(frame);digest.AppendData(bytes);
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
}

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Local evidence of the already returned GenerateIds result. No new
    /// provider action, current-policy substitution or refreshed inventory occurs.</summary>
    public WorkspacePublicationRecord RetainReturnedOriginalDependencyAllocation(CurrentCatalogDependencyCloudInspection original,string fileId)
    {
        ArgumentNullException.ThrowIfNull(original);using var transaction=_database.Connection.BeginTransaction();
        var context=new ReturnedOriginalDependencyAllocationContext(this,original,fileId);
        using var write=new WorkspacePublicationRepository(_database,_budget).RetainOriginalDependencyAllocationIn(context,transaction);
        write.Recheck();transaction.Commit();return write.Value;
    }

    private sealed class ReturnedOriginalDependencyAllocationContext(CatalogGenerationCaptureRepository owner,
        CurrentCatalogDependencyCloudInspection original,string fileId) : IReturnedOriginalDependencyAllocationContext
    {
        public (CapturedCatalogPublication Publication,WorkspacePublicationRecord Expected,string LocalStateSha256,string FileId)
            Resolve(ProcessorStateDatabase database,SqliteTransaction transaction)
        {
            if(!ReferenceEquals(database,owner._database)) throw new InvalidOperationException("Dependency allocation cannot cross original journal owners.");
            var expected=original.Dependency;var capture=owner.RequiredCapture(expected.Scope.GenerationId,transaction);
            var publication=owner.ReadPublication(capture,transaction) ?? throw new CatalogGenerationCaptureConflictException();
            var previous=original.Publication;
            if(capture.Scope!=expected.Scope || capture.Scope!=previous.Capture.Scope || capture.SnapshotId!=previous.Capture.SnapshotId ||
                capture.CommitIntentId!=previous.Capture.CommitIntentId || capture.Snapshot!=previous.Capture.Snapshot ||
                capture.Phase is not("publication-planned" or "dependencies-ready" or "commit-sealed") || publication.Basis!=previous.Basis ||
                CatalogDependencyPayload.Capture(expected.Role,expected.MimeType,expected.Payload.Span).IdFor(expected.Scope)!=expected.Id)
                throw new CatalogGenerationCaptureConflictException();
            // Reverify the original policy witness independently of the current
            // read nonce or live access: already returned IDs survive cancellation,
            // disconnection and source changes without authorizing obsolete facts.
            var pin=new LibraryCurrentControlSlotRepository(database,owner._budget).ReadIn(transaction)?.ConnectionDescriptor()
                ?? throw new InvalidDataException("Original allocation lost its pinned control identity.");
            var inventory=original.Inventory;var scope=capture.Scope;
            var journal=new ProtocolSigningJournal(database,owner._budget);var rootIntent=journal.Read(inventory.RootIntentId,transaction)
                ?? throw new SigningHistoryRecoveryRequiredException();
            if(pin.DocumentSha256!=inventory.ConnectionSha256 || scope.LibraryId!=pin.LibraryId || scope.AccountId!=pin.Workspace.AccountId ||
                scope.FolderId!=pin.Workspace.FolderId || rootIntent.Kind!=LibraryCurrentControl.Kind || rootIntent.Envelope is null ||
                rootIntent.Sequence!=inventory.RootSequence || rootIntent.EnvelopeSha256!=inventory.RootSha256)
                throw new WorkspacePublicationConflictException();
            var root=LibraryCurrentControl.Read(rootIntent.Envelope.Value.Span,pin);
            var publisher=root.ReadPublishers(journal.RetainedControllerReference(pin,root.Publishers,"library-publisher-authorization",transaction).Span,pin);
            var sources=root.ReadSources(journal.RetainedControllerReference(pin,root.Sources,"library-source-configuration",transaction).Span,pin);
            if(publisher.Publisher is not {} actor || actor.ActorId!=scope.ActorId || actor.Epoch!=scope.ActorEpoch ||
                publisher.PublisherKeyId is not {} key || sources.Revision!=scope.ConfigurationRevision || root.Sources.Reference.Sha256!=publication.Basis.SourcePolicySha256)
                throw new WorkspacePublicationConflictException();
            var descriptor=new LocalPublicationAuthorityDescriptor(pin.LibraryId,pin.ControllerId,pin.ControllerEpoch,pin.ControllerKeyId,
                actor.ActorId,actor.Epoch,key,publisher.Sequence,publisher.EnvelopeSha256,root.Sources.Sequence,root.Sources.Reference.Sha256,
                scope.AccountId,scope.FolderId,scope.ConfigurationRevision);
            var signing=journal.Inventory(pin.LibraryId,transaction);
            if(descriptor.Sha256()!=scope.AuthoritySha256 || !signing.Actors.Any(value=>value.ActorId==scope.ActorId && value.Epoch==scope.ActorEpoch && value.KeyId==key))
                throw new WorkspacePublicationConflictException();
            var authority=new LibraryAuthorityRepository(database,owner._budget);var human=new LibrarySourceRepository(database).Snapshot(transaction);
            var currentPublisher=authority.CurrentPublisherPolicy(transaction);var currentSources=authority.CurrentSourceConfiguration(transaction);
            var state=Hash(new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {signing=signing.Sha256,human.Revision,humanSha=HashPredecessorHumanSources(human),
                publisherSequence=currentPublisher?.Sequence,publisherSha=currentPublisher?.EnvelopeSha256,
                sourcesSequence=currentSources?.Sequence,sourcesSha=currentSources?.EnvelopeSha256,head=owner.ReadPublishedHead(transaction)})));
            return(publication,expected,state,fileId);
        }
    }
}
