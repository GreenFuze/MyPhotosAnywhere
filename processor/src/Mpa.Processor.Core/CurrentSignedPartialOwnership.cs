using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    private sealed record OwnedDispositionAnchor(CatalogObjectReference? SelectedAnchor);

    private void AssertExclusiveCurrentDisposition(CatalogGenerationCapture capture,SqliteTransaction? transaction)
    {
        var id=capture.Scope.GenerationId.ToString("D");
        using var query=Command(transaction,"SELECT count(*) FROM processor_meta WHERE key=$full OR key=$partial OR key=$unsigned",
            ("$full",SupersessionPrefix+id),("$partial",SignedPartialDispositionPrefix+id),("$unsigned",UnsignedDispositionPrefix+id));
        if((long)query.ExecuteScalar()!>1) throw new InvalidDataException("A generation cannot have conflicting terminal dispositions.");
    }

    // Only this iterative ancestry owner consumes original typed records without
    // recursively reentering public capture/lineage validation.
    private OwnedDispositionAnchor ReadOriginalOwnedDisposition(CatalogGenerationCapture capture,SqliteTransaction transaction,CancellationToken token)
    {
        AssertExclusiveCurrentDisposition(capture,transaction);
        var full=ReadOriginalCurrentSupersession(capture,transaction,token);
        var partial=ReadOriginalSignedPartialDisposition(capture,transaction,token);
        if((full is null)==(partial is null)) throw new CatalogGenerationCaptureConflictException();
        return new(full is not null ? full.SelectedAnchor : partial!.SelectedAnchor);
    }

    // Locate terminal work using opaque stored scope equality, never JSON parsing
    // or payload decoding. Damaged ordinary work must remain identifiable for retry.
    // Only a complete typed disposition can exclude an identity from scheduling.
    internal bool IsSignedPartialPublicationDisposedIn(Guid publicationId,SqliteTransaction transaction,
        IDictionary<Guid,bool> terminal)
    {
        var candidates=new List<Guid>();
        using(var query=Command(transaction,"""
            SELECT c.id FROM workspace_publication w
            JOIN catalog_generation_capture c ON c.scope=w.scope
            WHERE w.id=$id AND EXISTS(
              SELECT 1 FROM processor_meta m
              WHERE m.key=$prefix||c.id OR m.key=$prefix||c.id||':sha256')
            LIMIT 2
            """,("$id",publicationId.ToString("D")),("$prefix",SignedPartialDispositionPrefix)))
        using(var reader=query.ExecuteReader())
            while(reader.Read()) candidates.Add(ParseId(reader.GetString(0)));
        if(candidates.Count==0) return false;
        if(candidates.Count!=1) throw new InvalidDataException("Pending work matches conflicting terminal captures.");
        var generation=candidates[0];
        if(terminal.TryGetValue(generation,out var disposed)) return disposed;
        var capture=ReadOriginalCaptureFields(generation,transaction)
            ?? throw new InvalidDataException("Terminal partial work lost its exact capture.");
        disposed=IsSignedPartialDisposedIn(capture.Scope,transaction);
        if(!disposed) throw new InvalidDataException("Terminal partial work lost its disposition during enumeration.");
        terminal.Add(generation,disposed);return disposed;
    }

    // Historical state only. The account lease owner must separately join all
    // entered IO before recording a partial disposition. No network grant escapes.
    internal bool IsSignedPartialDisposedIn(WorkspacePublicationScope scope,SqliteTransaction transaction)
    {
        var key=SignedPartialDispositionPrefix+scope.GenerationId.ToString("D");
        using(var query=Command(transaction,"SELECT 1 FROM processor_meta WHERE key=$key OR key=$hash",("$key",key),("$hash",key+":sha256")))
            if(query.ExecuteScalar() is null) return false;
        var capture=ReadOriginalCaptureFields(scope.GenerationId,transaction)
            ?? throw new InvalidDataException("Terminal partial work lost its exact capture.");
        if(capture.Scope!=scope || ReadSignedPartialDisposition(capture,transaction,CancellationToken.None) is null)
            throw new InvalidDataException("Terminal partial work lost its complete typed disposition.");
        return true;
    }
}
