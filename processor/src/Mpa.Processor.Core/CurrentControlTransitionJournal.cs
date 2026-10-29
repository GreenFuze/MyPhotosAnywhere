using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record CurrentControlTransitionTarget(LibraryPolicyReference Publishers,LibraryPolicyReference Sources,Guid? CatalogPublicationId);
/// <summary>Immutable outgoing work, not a transport condition or write grant. Its
/// original predecessor, account revision, target and native signing ID never retarget.</summary>
public sealed record CurrentControlTransitionPlan(Guid Id,string ConnectionCode,Guid CapturedAttempt,long AccountRevision,long PreviousVersion,
    string? SharedDriveId,Guid PreviousIntentId,long PreviousSequence,string PreviousSha256,CurrentControlTransitionTarget Target,
    CatalogObjectReference? Catalog,Guid? GenerationId,long ConfigurationRevision,long Sequence,string BodySha256)
{
    public string Format {get;init;}="mpa-current-control-transition";
    public int Major {get;init;}=1;
}
public sealed class CurrentControlTransitionInProgressException() : InvalidOperationException("Recover the existing fixed Root transition before preparing another one.");

/// <summary>One local transaction reserves both fixed Root bytes/counter and their
/// captured transition. No native signing or network occurs. Retained outbox receipts
/// are historical only; complete fresh target reconciliation and same-ID conditional
/// update are still required by the outgoing owner. Never change original media.</summary>
public sealed partial class CurrentControlTransitionJournal(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    private readonly ProcessorStateDatabase _database=database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private const string Prefix="current_control_transition_v1:";
    private const int Maximum=LocalCurrentControlHistoryPages.MaximumHeads;
    private static readonly JsonSerializerOptions Json=new() {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};

    public IReadOnlyList<CurrentControlTransitionPlan> ReadAll(CancellationToken token=default)
    {
        using var transaction=_database.Connection.BeginTransaction();var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
        var plans=ReadAllIn(basis,transaction,token);token.ThrowIfCancellationRequested();transaction.Commit();return plans.AsReadOnly();
    }

    public CurrentControlTransitionPlan Prepare(Guid id,LocalCurrentControlAttempt fenced,CurrentControlTransitionTarget target,CancellationToken token=default)=>
        PrepareCore(id,fenced,target,null,token);

    public CurrentControlTransitionPlan PrepareCurrent(Guid id,LocalCurrentControlAttempt fenced,CurrentControlTransitionTarget target,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);ArgumentNullException.ThrowIfNull(publications);
        return PrepareCore(id,fenced,target,new(inventory,signing,publications),token);
    }
    private sealed record CurrentTransitionCatalogInventory(CurrentWorkspaceInventoryCheckpoint Inventory,CurrentControlSigningInventory Signing,WorkspacePublicationInventory Publications);

    private CurrentControlTransitionPlan PrepareCore(Guid id,LocalCurrentControlAttempt fenced,CurrentControlTransitionTarget target,
        CurrentTransitionCatalogInventory? current,CancellationToken token)
    {
        if(id==Guid.Empty) throw new ArgumentException("A fixed transition/signing identity is required.",nameof(id));ArgumentNullException.ThrowIfNull(target);
        using var transaction=_database.Connection.BeginTransaction();var observations=new LocalCurrentControlObservations(_database,_budget);
        var historical=observations.TransitionBasisIn(null,transaction,token);var plans=ReadAllIn(historical,transaction,token);
        var old=plans.SingleOrDefault(plan=>plan.Id==id);
        if(old is not null)
        {
            if(old.Target!=target) throw new ProtocolSigningConflictException();
            // Recovery read only, including after credentials/configuration change.
            transaction.Commit();return old;
        }
        if(plans.Any(plan=>ReadCompletion(plan,transaction) is null)) throw new CurrentControlTransitionInProgressException();
        if(plans.Count>=Maximum-1) throw new CatalogTraversalLimitException();
        if(current is not null) new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(
            current.Inventory,current.Signing,fenced,current.Publications,transaction,token);
        var basis=observations.TransitionBasisIn(fenced,transaction,token);var journal=new ProtocolSigningJournal(_database,_budget);
        var policies=Policies(basis.Pin,target,transaction);
        AssertHumanTargetCurrent(policies,target,transaction);
        var history=new LocalPublisherHistory();CatalogPublisherAuthorization? previousPublisher=null;
        foreach(var rootId in basis.ObservedRoots)
        {
            token.ThrowIfCancellationRequested();var intent=journal.Read(rootId,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
            var root=LibraryCurrentControl.Read(intent.Envelope!.Value.Span,basis.Pin);
            previousPublisher=root.ReadPublishers(journal.RetainedControllerReference(basis.Pin,root.Publishers,"library-publisher-authorization",transaction).Span,basis.Pin);
            history.Observe(previousPublisher);
        }
        history.Observe(policies.Publisher);
        var catalog=Catalog(basis.Pin,target,policies.Publisher,policies.Sources,transaction);
        if(catalog.AccountRevision>fenced.AccountRevision) throw new StaleDriveConnectionException();
        if(catalog.Reference is null && basis.Head.Catalog is not null && policies.Publisher.Publisher is not null) throw new CatalogGenerationCaptureConflictException();
        if(catalog.Reference is not null && catalog.Reference!=basis.Head.Catalog)
        {
            var samePublisher=previousPublisher!.Publisher?.ActorId==policies.Publisher.Publisher?.ActorId &&
                previousPublisher.Publisher?.Epoch==policies.Publisher.Publisher?.Epoch && previousPublisher.PublisherKeyId==policies.Publisher.PublisherKeyId;
            if(current is not null && samePublisher && catalog.Generation is {} generation)
                new CatalogGenerationCaptureRepository(_database,_budget).AssertCurrentCandidateLineageIn(generation,basis.Head.Catalog,transaction,token);
            else if(catalog.Parent!=(samePublisher ? basis.Head.Catalog : null)) throw new CatalogGenerationCaptureConflictException();
        }
        if(target.Publishers!=basis.Head.Publishers && target.Publishers.Sequence<=basis.Head.Sequence ||
            target.Sources!=basis.Head.Sources && target.Sources.Sequence<=basis.Head.Sequence) throw new ProtocolSigningConflictException();
        using var signing=journal.ReserveIn(id,basis.Pin.ControllerId,basis.Pin.ControllerEpoch,LibraryCurrentControl.Kind,
            sequence=>new LibraryCurrentControlWriter(basis.Pin).EncodeDocument(sequence,target.Publishers,target.Sources,catalog.Reference),transaction);
        if(signing.Value.Sequence<=basis.Head.Sequence) throw new ProtocolSigningConflictException();
        var result=new CurrentControlTransitionPlan(id,basis.Pin.Encode(),fenced.Id,fenced.AccountRevision,fenced.Version!.Value,fenced.SharedDriveId,
            basis.ObservedRoots[^1],basis.Head.Sequence,basis.Head.EnvelopeSha256,target,catalog.Reference,catalog.Generation,policies.Sources.Revision,
            signing.Value.Sequence,signing.Value.BodySha256);
        var encoded=Encode(result);using var reservation=_budget.Reserve(checked(Encoding.UTF8.GetByteCount(encoded)*3L+65536));reservation.RecheckBeforeWrite(reservation.Bytes);
        var key=Key(id);Put(transaction,key,encoded);Put(transaction,key+":sha256",Hash(encoded));Put(transaction,key+":claim",Claim(result));
        token.ThrowIfCancellationRequested();signing.Recheck();reservation.RecheckBeforeWrite(reservation.Bytes);transaction.Commit();return result;
    }

    /// <summary>Called only inside complete fenced canonical admission. It does not
    /// advance generation PublishedHead, release snapshots or authorize a cloud write.</summary>
    internal StorageReservation? RecordActivatedIn(CurrentCatalogActivation activation,SqliteTransaction transaction,CancellationToken token)
    {
        using(var any=Command(transaction,"SELECT 1 FROM processor_meta WHERE key GLOB $prefix LIMIT 1",("$prefix",Prefix+"*")))
            if(any.ExecuteScalar() is null) return null;
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
        var plans=ReadAllIn(basis,transaction,token);var plan=plans.SingleOrDefault(value=>value.Sequence==activation.RootSequence);
        if(plan is null) return null;
        var signed=new ProtocolSigningJournal(_database,_budget).Read(plan.Id,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
        if(signed.EnvelopeSha256!=activation.RootSha256 || plan.Catalog!=activation.Catalog || plan.ConfigurationRevision!=activation.ConfigurationRevision ||
            activation.AccountRevision<plan.AccountRevision || activation.DriveVersion<=plan.PreviousVersion || activation.ConnectionCode!=plan.ConnectionCode)
            throw new CurrentControlObservationConflictException();
        var receipt=new Activated(plan.Id,activation.RootSha256,activation.StageId,activation.AccountRevision,activation.DriveVersion);
        var old=ReadCompletion(plan,transaction);
        if(old is not null)
        {
            if(old.Withdrawal is not null) throw new CurrentControlTransitionWithdrawnException();
            if(old.RootSha256!=receipt.RootSha256 || old.AccountRevision>receipt.AccountRevision || old.DriveVersion>receipt.DriveVersion) throw new CurrentControlObservationConflictException();
            return null; // Preserve the first historical completion, never relabel it.
        }
        var encoded=Encode(receipt);StorageReservation? reservation=_budget.Reserve(65536);
        try
        {
            reservation.RecheckBeforeWrite(reservation.Bytes);Put(transaction,Key(plan.Id)+":activated",encoded);Put(transaction,Key(plan.Id)+":activated:sha256",Hash(encoded));
            var owned=reservation;reservation=null;return owned;
        }
        finally {reservation?.Dispose();}
    }

    private List<CurrentControlTransitionPlan> ReadAllIn(LocalCurrentTransitionBasis basis,SqliteTransaction transaction,CancellationToken token)
    {
        var rows=ReadOriginalTransitionRowsIn(transaction,token);var plans=new List<CurrentControlTransitionPlan>();
        foreach(var id in OriginalTransitionIds(rows))
        {
            var original=ReadOriginalTransitionInputsIn(id,rows,basis,transaction,token);
            var plan=original.Plan;var policies=original.Policies;
            // Keep full captured generation/phase validation on the ordinary path.
            var catalog=Catalog(basis.Pin,plan.Target,policies.Publisher,policies.Sources,transaction);
            if(catalog.Reference!=plan.Catalog || catalog.Generation!=plan.GenerationId || policies.Sources.Revision!=plan.ConfigurationRevision ||
                catalog.AccountRevision>plan.AccountRevision) throw new InvalidDataException("Root transition retargeted captured generation or policy work.");
            AssertOriginalTransitionEnvelope(original,basis.Pin);
            _=ReadCompletion(plan,transaction);plans.Add(plan);
        }
        AssertOriginalRootCoverageIn(plans.Select(plan=>plan.Id),basis,transaction,token);return plans;
    }

    private Dictionary<string,string> ReadOriginalTransitionRowsIn(SqliteTransaction transaction,CancellationToken token)
    {
        var rows=new Dictionary<string,string>(StringComparer.Ordinal);
        using(var command=Command(transaction,"SELECT key,length(CAST(value AS BLOB)),value FROM processor_meta WHERE key GLOB $prefix ORDER BY key LIMIT $limit",("$prefix",Prefix+"*"),("$limit",Maximum*5+1)))
        using(var reader=command.ExecuteReader())
            while(reader.Read())
            {
                token.ThrowIfCancellationRequested();var key=reader.GetString(0);var tail=key[Prefix.Length..];
                if(rows.Count==Maximum*5 || tail.Length<36 || !Guid.TryParseExact(tail[..36],"D",out var id) || id==Guid.Empty ||
                    tail[36..] is not ("" or ":sha256" or ":claim" or ":activated" or ":activated:sha256") || reader.GetInt64(1) is <1 or >16384)
                    throw new InvalidDataException("Root transition inventory has an unknown key or exceeds its bounds.");
                rows.Add(key,reader.GetString(2));
            }
        return rows;
    }

    private static IEnumerable<Guid> OriginalTransitionIds(Dictionary<string,string> rows)=>rows.Keys
        .Select(key=>Guid.ParseExact(key.Substring(Prefix.Length,36),"D")).Distinct().OrderBy(id=>id.ToString("D"),StringComparer.Ordinal);

    private OriginalTransitionInputs ReadOriginalTransitionInputsIn(Guid id,Dictionary<string,string> rows,
        LocalCurrentTransitionBasis basis,SqliteTransaction transaction,CancellationToken token)
    {
        var journal=new ProtocolSigningJournal(_database,_budget);
        token.ThrowIfCancellationRequested();var key=Key(id);
        if(!rows.TryGetValue(key,out var body) || !rows.TryGetValue(key+":sha256",out var hash) || Hash(body)!=hash || !rows.TryGetValue(key+":claim",out var claim))
            throw new InvalidDataException("Root transition lost its complete immutable checkpoint.");
        CurrentControlTransitionPlan plan;
        try {plan=JsonSerializer.Deserialize<CurrentControlTransitionPlan>(body,Json) ?? throw new InvalidDataException("Missing Root transition.");}
        catch(JsonException error){throw new InvalidDataException("Root transition cannot be decoded.",error);}
        if(plan.Format!="mpa-current-control-transition" || plan.Major!=1 || plan.Id!=id || plan.Id==plan.PreviousIntentId ||
            plan.CapturedAttempt==Guid.Empty || plan.AccountRevision<basis.MinimumAccountRevision || plan.PreviousVersion<basis.MinimumVersion ||
            plan.SharedDriveId!=basis.SharedDriveId || plan.PreviousSequence<1 || plan.Sequence<=plan.PreviousSequence ||
            plan.ConfigurationRevision<1 || plan.Target is null || plan.Target.Publishers is null || plan.Target.Sources is null ||
            plan.ConnectionCode!=basis.Pin.Encode() || Encode(plan)!=body || claim!=Claim(plan) ||
            (plan.Catalog is null)!=(plan.GenerationId is null) || (plan.Catalog is null)!=(plan.Target.CatalogPublicationId is null) || plan.GenerationId==Guid.Empty)
            throw new InvalidDataException("Root transition changed its captured scope, format or target.");
        var previous=journal.Read(plan.PreviousIntentId,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
        if(previous.Envelope is null || previous.Sequence!=plan.PreviousSequence || previous.EnvelopeSha256!=plan.PreviousSha256) throw new InvalidDataException("Root transition lost its exact predecessor signature.");
        var predecessor=LibraryCurrentControl.Read(previous.Envelope.Value.Span,basis.Pin);
        var intent=journal.Read(id,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
        var encoded=new LibraryCurrentControlWriter(basis.Pin).EncodeDocument(plan.Sequence,plan.Target.Publishers,plan.Target.Sources,plan.Catalog);
        if(intent.Kind!=LibraryCurrentControl.Kind || intent.Sequence!=plan.Sequence || intent.BodySha256!=plan.BodySha256 || !intent.Body.Span.SequenceEqual(encoded) ||
            plan.Target.Publishers!=predecessor.Publishers && plan.Target.Publishers.Sequence<=predecessor.Sequence ||
            plan.Target.Sources!=predecessor.Sources && plan.Target.Sources.Sequence<=predecessor.Sequence) throw new InvalidDataException("Root transition lost its fixed native signing intent or successor.");
        var policies=Policies(basis.Pin,plan.Target,transaction);
        return new(plan,intent,predecessor,policies);
    }

    private static void AssertOriginalTransitionEnvelope(OriginalTransitionInputs original,LibraryConnectionDescriptor pin)
    {
        var intent=original.Intent;var predecessor=original.Predecessor;var policies=original.Policies;
        if(intent.Envelope is { } envelope)
        {
            var root=LibraryCurrentControl.Read(envelope.Span,pin);root.AssertSuccessorOf(predecessor);
            _=root.ReadPublishers(policies.PublisherBytes.Span,pin);_=root.ReadSources(policies.SourceBytes.Span,pin);
        }
    }

    private void AssertOriginalRootCoverageIn(IEnumerable<Guid> ids,LocalCurrentTransitionBasis basis,SqliteTransaction transaction,CancellationToken token)
    {
        // Every post-bootstrap Root signature/reservation must retain its plan,
        // even if it was subsequently observed. Never adopt orphan signing work.
        var known=ids.ToHashSet();known.Add(basis.ObservedRoots[0]);var count=0;
        using(var roots=Command(transaction,"SELECT id FROM signing_intent WHERE actor_id=$actor AND epoch=$epoch AND kind=$kind",("$actor",basis.Pin.ControllerId.ToString("D")),("$epoch",basis.Pin.ControllerEpoch),("$kind",LibraryCurrentControl.Kind)))
        using(var reader=roots.ExecuteReader())
            while(reader.Read())
            {token.ThrowIfCancellationRequested();if(++count>Maximum) throw new CatalogTraversalLimitException();if(!Guid.TryParseExact(reader.GetString(0),"D",out var id) || !known.Contains(id)) throw new SigningHistoryRecoveryRequiredException();}
    }

    private sealed record OriginalTransitionInputs(CurrentControlTransitionPlan Plan,ProtocolSigningIntent Intent,
        LibraryCurrentControl Predecessor,PolicyEvidence Policies);

    private PolicyEvidence Policies(LibraryConnectionDescriptor pin,CurrentControlTransitionTarget target,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(target.Publishers);ArgumentNullException.ThrowIfNull(target.Sources);
        var journal=new ProtocolSigningJournal(_database,_budget);var publisher=journal.RetainedControllerReference(pin,target.Publishers,"library-publisher-authorization",transaction);
        var sources=journal.RetainedControllerReference(pin,target.Sources,"library-source-configuration",transaction);
        var reader=new CatalogPublicationReader(pin.Workspace);var key=Convert.FromBase64String(pin.ControllerPublicKeySpki);
        var trusted=new TrustedProtocolActor(pin.LibraryId,pin.ControllerId,pin.ControllerEpoch,key,["library-publisher-authorization","library-source-configuration"]);
        var pub=reader.ReadPublisherAuthorization(publisher.Span,trusted);
        var verified=new SignedProtocolDocuments([CatalogSourceConfiguration.Capability]).VerifyAndRead(sources.Span,trusted);
        if(pub.Sequence!=target.Publishers.Sequence || verified.Sequence!=target.Sources.Sequence) throw new ProtocolSigningConflictException();
        return new(pub,reader.ReadSourceConfiguration(verified,pin.LibraryId),publisher,sources);
    }

    private (CatalogObjectReference? Reference,Guid? Generation,CatalogObjectReference? Parent,long? AccountRevision) Catalog(LibraryConnectionDescriptor pin,CurrentControlTransitionTarget target,
        CatalogPublisherAuthorization publisher,CatalogSourceConfiguration sources,SqliteTransaction transaction)
    {
        if(target.CatalogPublicationId is not { } id) return(null,null,null,null);
        var actor=publisher.Publisher ?? throw new CatalogPublicationNotAuthorizedException();
        var row=new WorkspacePublicationRepository(_database,_budget).Read(id,transaction) ?? throw new CatalogGenerationCaptureConflictException();
        if(row.Role!="catalog-commit" || row.MimeType!="application/json" || row.FileId is null || row.VerifiedVersion is null ||
            row.Scope.LibraryId!=pin.LibraryId || row.Scope.AccountId!=pin.Workspace.AccountId || row.Scope.FolderId!=pin.Workspace.FolderId ||
            row.Scope.ActorId!=actor.ActorId || row.Scope.ActorEpoch!=actor.Epoch || row.Scope.ConfigurationRevision!=sources.Revision) throw new CatalogGenerationCaptureConflictException();
        var publication=new CatalogGenerationCaptureRepository(_database,_budget).PublicationIn(row.Scope.GenerationId,transaction);
        if(publication.Capture.CommitIntentId!=id || publication.Capture.Scope!=row.Scope || publication.Plan is null ||
            publication.Capture.Phase is not ("commit-sealed" or "commit-verified" or "complete" or "snapshot-retiring" or "retired") ||
            publication.Basis.SourcePolicySha256!=target.Sources.Reference.Sha256) throw new CatalogGenerationCaptureConflictException();
        var intent=new ProtocolSigningJournal(_database,_budget).Read(id,transaction);
        if(intent?.Envelope is null || intent.EnvelopeSha256!=row.Sha256 || !intent.Envelope.Value.Span.SequenceEqual(row.Payload.Span)) throw new CatalogGenerationCaptureConflictException();
        return(new(pin.Workspace.AccountId,row.FileId,pin.Workspace.FolderId,row.Payload.Length,row.Sha256),row.Scope.GenerationId,publication.Basis.Parent,row.Scope.AccountRevision);
    }

    private Activated? ReadCompletion(CurrentControlTransitionPlan plan,SqliteTransaction transaction)
    {
        var key=Key(plan.Id)+":activated";var body=ReadValue(transaction,key,4096);var hash=ReadValue(transaction,key+":sha256",64);
        if(body is null && hash is null) return null;
        if(body is null || Hash(body)!=hash) throw new InvalidDataException("Root activation receipt lost its complete integrity record.");
        Activated value;try {value=JsonSerializer.Deserialize<Activated>(body,Json) ?? throw new InvalidDataException("Missing Root activation receipt.");}
        catch(JsonException error){throw new InvalidDataException("Root activation receipt cannot be decoded.",error);}
        var intent=new ProtocolSigningJournal(_database,_budget).Read(plan.Id,transaction);
        if(value.Id!=plan.Id || intent is null || value.AccountRevision<plan.AccountRevision || value.DriveVersion<plan.PreviousVersion || Encode(value)!=body)
            throw new InvalidDataException("Root terminal receipt changed its fixed scope or encoding.");
        if(value.Withdrawal is not { } withdrawn)
        {
            if(value.StageId==Guid.Empty || intent.Envelope is null || value.RootSha256!=intent.EnvelopeSha256 || value.DriveVersion<=plan.PreviousVersion)
                throw new InvalidDataException("Root activation receipt changed its fixed signed selection.");
        }
        else if(value.StageId!=Guid.Empty || withdrawn.AttemptId==Guid.Empty || withdrawn.BodySha256!=plan.BodySha256 || !WithdrawalReasonKnown(withdrawn.Reason) ||
            !(value.RootSha256==plan.PreviousSha256 && withdrawn.RootSequence==plan.PreviousSequence ||
                intent.Envelope is not null && value.RootSha256==intent.EnvelopeSha256 && withdrawn.RootSequence==plan.Sequence && value.DriveVersion>plan.PreviousVersion))
            throw new InvalidDataException("Root withdrawal lost its original body or actual native predecessor/desired observation.");
        return value;
    }
    private string? ReadValue(SqliteTransaction transaction,string key,int maximum)
    {
        using var command=Command(transaction,"SELECT length(CAST(value AS BLOB)),value FROM processor_meta WHERE key=$key",("$key",key));using var row=command.ExecuteReader();
        if(!row.Read()) return null;if(row.GetInt64(0) is <1 || row.GetInt64(0)>maximum) throw new InvalidDataException("Root transition value exceeds its byte bound.");return row.GetString(1);
    }
    private void Put(SqliteTransaction transaction,string key,string value)
    {using var command=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value)",("$key",key),("$value",value));if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("Root transition did not retain exactly one immutable row.");}
    private SqliteCommand Command(SqliteTransaction transaction,string sql,params (string Key,object Value)[] values)
    {var command=_database.Connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;foreach(var pair in values) command.Parameters.AddWithValue(pair.Key,pair.Value);return command;}
    private static string Key(Guid id)=>Prefix+id.ToString("D");
    private static string Encode<T>(T value)=>Encoding.UTF8.GetString(new CanonicalJson(16384).Encode(JsonSerializer.SerializeToUtf8Bytes(value,Json)));
    private static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Claim(CurrentControlTransitionPlan plan)=>Hash("mpa-fixed-root-transition-v1\n"+Encode(plan));
    private sealed record PolicyEvidence(CatalogPublisherAuthorization Publisher,CatalogSourceConfiguration Sources,ReadOnlyMemory<byte> PublisherBytes,ReadOnlyMemory<byte> SourceBytes);
    // The legacy key and activation JSON remain byte-identical. A terminal union
    // uses the SAME two result rows, keeping the existing five-row-per-plan bound.
    private sealed record Activated(Guid Id,string RootSha256,Guid StageId,long AccountRevision,long DriveVersion)
    {
        [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public Withdrawn? Withdrawal {get;init;}
    }
    private sealed record Withdrawn(Guid AttemptId,string BodySha256,long RootSequence,string Reason);
}
