using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Explicit current-controlled completion. It pins the first actual
/// selected generation; it is not a legacy immutable-workspace checkpoint,
/// current cloud authority or permission to mutate an original.</summary>
public sealed record CurrentCatalogGenerationVerification(Guid GenerationId,WorkspacePublicationScope Scope,Guid SnapshotId,Guid CommitIntentId,
    long Sequence,long Assets,WorkspaceDocumentObservation Commit,string SelectionSha256)
{
    public string Format {get;init;}="mpa-current-generation-completion";
    public int Major {get;init;}=1;
}

public sealed partial class CatalogGenerationCaptureRepository
{
    private static readonly JsonSerializerOptions CurrentCompletionJson=new()
        {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};
    private sealed record CurrentPublishedHeadPointer(Guid GenerationId,string VerificationSha256)
    {
        public string Format {get;init;}="mpa-current-generation-published-head";
        public int Major {get;init;}=1;
    }

    public CurrentCatalogGenerationVerification? SelectedCurrentVerification(Guid generationId)
    {
        Id(generationId);using var transaction=_database.Connection.BeginTransaction();var capture=Read(generationId,transaction);
        var result=capture is null ? null : ReadCurrentGenerationVerification(capture,transaction);transaction.Commit();return result;
    }

    /// <summary>Acknowledge already verified actual selection in one transaction.
    /// No remote action, new policy selection or recapture is performed. Historical
    /// acknowledgement permits only the later joined private-metadata retirement.</summary>
    public PublishedCatalogHead CompleteSelectedCurrentGeneration(Guid generationId,CancellationToken token=default)
    {
        Id(generationId);using var transaction=_database.Connection.BeginTransaction();token.ThrowIfCancellationRequested();
        var capture=RequiredCapture(generationId,transaction);
        if(capture.Phase is not ("commit-sealed" or "complete") || ReadUnsignedCurrentDisposition(capture,transaction,token) is not null) throw new CatalogGenerationCaptureConflictException();
        var inputs=SelectionInputsIn(capture,transaction);
        var selected=new CurrentCatalogGenerationSelectionRepository(_database,_budget).ReadForCaptureIn(inputs,transaction,token)
            ?? throw new CatalogGenerationCaptureConflictException();
        var actual=new CurrentCatalogGenerationVerification(generationId,capture.Scope,capture.SnapshotId,capture.CommitIntentId,
            selected.Sequence,selected.Assets,selected.Commit,CurrentCatalogGenerationSelectionRepository.HistoricalSha256(selected));
        var retained=ReadCurrentGenerationVerification(capture,transaction);var previous=ReadPublishedHead(transaction);
        if(retained is not null)
        {
            if(capture.Phase!="complete" || retained!=actual || previous?.GenerationId!=generationId) throw new CatalogGenerationCaptureConflictException();
            token.ThrowIfCancellationRequested();transaction.Commit();return previous;
        }
        if(capture.Phase!="commit-sealed" || ReadVerification(capture,transaction) is not null) throw new CatalogGenerationCaptureConflictException();
        if(previous?.GenerationId==generationId) throw new CatalogGenerationCaptureConflictException();
        // Actual selection is already verified above. A retired owned predecessor
        // is signed ancestry, never a fabricated intermediate PublishedHead.
        new OwnedCatalogLineageReader(this,capture.Scope,transaction,token).RequirePath(inputs.Publication.Basis.Parent,
            inputs.Publication.Basis.ParentSequence,previous?.Commit);
        var encoded=EncodeCurrentVerification(actual);var hash=VerificationHash(capture,inputs.Publication,encoded);
        var pointer=EncodeCurrentHead(new(generationId,hash));
        using var reservation=_budget.Reserve(checked(Encoding.UTF8.GetByteCount(encoded)*3L+65536));reservation.RecheckBeforeWrite(reservation.Bytes);
        using(var insert=Command(transaction,"INSERT INTO catalog_generation_verification(id,document,document_hash) VALUES($id,$document,$hash)",
            ("$id",generationId.ToString("D")),("$document",encoded),("$hash",hash)))
            if(insert.ExecuteNonQuery()!=1) throw new InvalidOperationException("Current completion lost its exact original verification owner.");
        using(var update=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$key",PublishedHeadKey),("$value",pointer)))
            if(update.ExecuteNonQuery()!=1) throw new InvalidOperationException("Current completion did not install exactly one tagged head.");
        ChangePhase(capture,"complete",transaction);token.ThrowIfCancellationRequested();reservation.RecheckBeforeWrite(reservation.Bytes);
        transaction.Commit();return CurrentHead(actual);
    }

    private CurrentCatalogGenerationVerification? ReadCurrentGenerationVerification(CatalogGenerationCapture capture,SqliteTransaction? transaction)
    {
        string text;string hash;
        using(var query=Command(transaction,"SELECT length(CAST(document AS BLOB)),document,document_hash FROM catalog_generation_verification WHERE id=$id",("$id",capture.Scope.GenerationId.ToString("D"))))
        using(var reader=query.ExecuteReader())
        {
            if(!reader.Read()) return null;
            if(reader.GetInt64(0) is <1 or >49152) throw new InvalidDataException("Current generation verification exceeds its bound.");
            text=reader.GetString(1);hash=reader.GetString(2);
        }
        if(!CurrentVerificationTag(text)) return null;
        ArgumentNullException.ThrowIfNull(transaction);
        try
        {
            var value=JsonSerializer.Deserialize<CurrentCatalogGenerationVerification>(text,CurrentCompletionJson)
                ?? throw new InvalidDataException("Missing current generation verification.");
            var inputs=SelectionInputsIn(capture,transaction);
            var selected=new CurrentCatalogGenerationSelectionRepository(_database,_budget).ReadForCaptureIn(inputs,transaction,CancellationToken.None)
                ?? throw new InvalidDataException("Current completion lost its original actual selection.");
            if(value.Format!="mpa-current-generation-completion" || value.Major!=1 || value.GenerationId!=capture.Scope.GenerationId ||
                value.Scope!=capture.Scope || value.SnapshotId!=capture.SnapshotId || value.CommitIntentId!=capture.CommitIntentId ||
                value.Sequence!=selected.Sequence || value.Assets!=selected.Assets || value.Commit!=selected.Commit ||
                value.SelectionSha256!=CurrentCatalogGenerationSelectionRepository.HistoricalSha256(selected) || EncodeCurrentVerification(value)!=text ||
                VerificationHash(capture,inputs.Publication,text)!=hash)
                throw new InvalidDataException("Current completion changed its original native plan, scope or actual selection.");
            return value;
        }
        catch(Exception error) when(error is JsonException or ArgumentException or ProtocolFormatException or OverflowException)
        {throw new InvalidDataException("Current generation verification cannot be decoded safely.",error);}
    }

    private PublishedCatalogHead ReadCurrentPublishedHead(string text,SqliteTransaction transaction)
    {
        try
        {
            var pointer=JsonSerializer.Deserialize<CurrentPublishedHeadPointer>(text,CurrentCompletionJson)
                ?? throw new InvalidDataException("Missing current generation head.");
            Id(pointer.GenerationId);var capture=Read(pointer.GenerationId,transaction)
                ?? throw new InvalidDataException("Current generation head references a missing original capture.");
            var verification=ReadCurrentGenerationVerification(capture,transaction) ?? throw new InvalidDataException("Current generation head lost its typed verification.");
            if(pointer.Format!="mpa-current-generation-published-head" || pointer.Major!=1 || capture.Phase is not ("complete" or "snapshot-retiring" or "retired") ||
                EncodeCurrentHead(pointer)!=text || VerificationHash(capture,ReadPublication(capture,transaction)!,EncodeCurrentVerification(verification))!=pointer.VerificationSha256)
                throw new InvalidDataException("Current generation head changed its original completed selection.");
            return CurrentHead(verification);
        }
        catch(Exception error) when(error is JsonException or ArgumentException or ProtocolFormatException or OverflowException)
        {throw new InvalidDataException("Current generation head cannot be decoded safely.",error);}
    }

    private static bool CurrentVerificationTag(string text)=>CurrentTaggedDocument(text,"mpa-current-generation-completion");
    private static bool CurrentHeadTag(string text)=>CurrentTaggedDocument(text,"mpa-current-generation-published-head");
    private static bool CurrentTaggedDocument(string text,string expected)
    {
        try
        {
            using var document=JsonDocument.Parse(text);
            if(document.RootElement.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("Generation receipt must be an explicit object.");
            if(!document.RootElement.TryGetProperty("Format",out var format)) return false; // Exact unchanged legacy format.
            if(format.ValueKind!=JsonValueKind.String || format.GetString()!=expected) throw new InvalidDataException("Unknown generation receipt format.");
            return true;
        }
        catch(JsonException error) {throw new InvalidDataException("Generation receipt format cannot be decoded safely.",error);}
    }
    private static PublishedCatalogHead CurrentHead(CurrentCatalogGenerationVerification value)=>new(value.GenerationId,value.Sequence,value.Assets,
        new(value.Scope.AccountId,value.Commit.FileId,value.Scope.FolderId,value.Commit.Bytes,value.Commit.Sha256));
    private static string EncodeCurrentVerification(CurrentCatalogGenerationVerification value)=>Encoding.UTF8.GetString(new CanonicalJson(49152).Encode(JsonSerializer.SerializeToUtf8Bytes(value,CurrentCompletionJson)));
    private static string EncodeCurrentHead(CurrentPublishedHeadPointer value)=>Encoding.UTF8.GetString(new CanonicalJson(1024).Encode(JsonSerializer.SerializeToUtf8Bytes(value,CurrentCompletionJson)));
}
