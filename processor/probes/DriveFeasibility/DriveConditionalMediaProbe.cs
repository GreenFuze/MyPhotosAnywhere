using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.DriveProbes;

/// <summary>Capability evidence only: writes solely to the tiny synthetic file created
/// and durably journaled here. No existing-file target, original or deletion is exposed.</summary>
internal sealed class DriveConditionalMediaProbe(UserCredential credential)
{
    /// <summary>Resume ONLY an actual durably discarded completion in a private journal.
    /// Reuse the same generated fixture ID; never allocate a replacement after uncertainty.</summary>
    internal async Task RecoverAsync(string journalName,string reportDirectory,CancellationToken token)
    {
        var retained=await ReadRetainedAsync(journalName,reportDirectory,token).ConfigureAwait(false);
        var id=retained.FileId;var parent=retained.ParentId;var lastCondition=retained.PreCompletionEtag;
        var first="MPA synthetic current metadata A; no user content.\n"u8.ToArray();
        var expected="MPA synthetic current metadata C; no user content.\n"u8.ToArray();
        await RecoverCoreAsync(journalName,reportDirectory,id,parent,lastCondition,first,expected,token).ConfigureAwait(false);
    }

    // Local historical evidence only. Actual scope, pending bytes and a fresh read
    // fence remain mandatory before the only exposed stale-condition test request.
    internal static async Task<RetainedSyntheticMedia> ReadRetainedAsync(string journalName,string reportDirectory,CancellationToken token)
    {
        const string prefix="conditional-media-";const string suffix=".journal.jsonl";
        if (!journalName.StartsWith(prefix,StringComparison.Ordinal) || !journalName.EndsWith(suffix,StringComparison.Ordinal) ||
            journalName.Length!=prefix.Length+32+suffix.Length || !Guid.TryParseExact(journalName.AsSpan(prefix.Length,32),"N",out var run) ||
            journalName!=prefix+run.ToString("N")+suffix) throw new ArgumentException("A retained private synthetic media journal name is required.",nameof(journalName));
        var path=Path.Combine(reportDirectory,journalName);
        byte[] owned;
        await using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous))
        {
            if (file.Length is < 1 or > 65536) throw new InvalidDataException("The retained synthetic journal exceeds its bound.");
            owned=new byte[checked((int)file.Length)];await file.ReadExactlyAsync(owned,token).ConfigureAwait(false);
            if (await file.ReadAsync(new byte[1],token).ConfigureAwait(false)!=0) throw new InvalidDataException("The retained journal changed while reading.");
        }
        var text=new System.Text.UTF8Encoding(false,true).GetString(owned);
        if (!text.EndsWith('\n')) throw new InvalidDataException("The retained journal has an incomplete final record.");
        var lines=text[..^1].Split('\n');
        if (lines.Length is < 4 or > 32) throw new InvalidDataException("The retained synthetic journal lost its bounded operation history.");
        string? id=null,parent=null,lastCondition=null,discardedCondition=null,createdFolder=null;var discarded=false;
        var first="MPA synthetic current metadata A; no user content.\n"u8.ToArray();
        var expected="MPA synthetic current metadata C; no user content.\n"u8.ToArray();
        foreach(var line in lines)
        {
            using var document=JsonDocument.Parse(line);var entry=document.RootElement;
            switch(entry.GetProperty("operation").GetString())
            {
                case "before-synthetic-media-create":
                    if (id is not null || entry.GetProperty("sha256").GetString()!=Hash(first) || entry.GetProperty("bytes").GetInt32()!=first.Length)
                        throw new InvalidDataException("The retained journal changed its original synthetic creation.");
                    id=entry.GetProperty("id").GetString();parent=entry.GetProperty("parent").GetString();break;
                case "before-synthetic-media-update":
                    if (entry.GetProperty("id").GetString()!=id) throw new InvalidDataException("The retained media target changed.");
                    if (discarded && (entry.GetProperty("sha256").GetString()!=Hash(first) || entry.GetProperty("condition").GetString()!=discardedCondition))
                        throw new InvalidDataException("Later work changed the retained stale-condition intent.");
                    lastCondition=entry.GetProperty("sha256").GetString()==Hash(expected) && entry.GetProperty("bytes").GetInt32()==expected.Length
                        ? entry.GetProperty("condition").GetString() : null;break;
                case "synthetic-media-completion-discarded":
                    if (discarded || entry.GetProperty("id").GetString()!=id || entry.GetProperty("status").GetInt32()!=200 ||
                        entry.GetProperty("bodyConsumed").GetBoolean() || lastCondition is null)
                        throw new InvalidDataException("The retained discarded completion does not match its fixed pending media intent.");
                    discarded=true;discardedCondition=lastCondition;break;
                case "before-folder-create":
                    if (createdFolder is not null || entry.GetProperty("name").GetString()!="MPA conditional media synthetic probe "+run.ToString("N"))
                        throw new InvalidDataException("The retained synthetic parent creation changed.");
                    createdFolder=entry.GetProperty("id").GetString();break;
                case "synthetic-stale-media-result":case "after-rejected-synthetic-media":case "synthetic-media-read-fence":break;
                default:throw new InvalidDataException("Unknown retained synthetic journal operation.");
            }
        }
        if (!discarded || id is null || parent is null || parent!=createdFolder || discardedCondition is null ||
            !EntityTagHeaderValue.TryParse(discardedCondition,out var capturedTag) || capturedTag.IsWeak || capturedTag.Tag=="*")
            throw new InvalidDataException("No exact actual discarded synthetic completion is retained; do not fabricate one.");
        ProbeDriveClient.CheckId(id);ProbeDriveClient.CheckId(parent);
        return new(id,parent,discardedCondition);
    }

    private async Task RecoverCoreAsync(string journalName,string reportDirectory,string id,string parent,string lastCondition,byte[] first,byte[] expected,CancellationToken token)
    {
        using var http=new HttpClient(new SocketsHttpHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(60) };
        var api=new ProbeDriveClient(http,credential);var metadata=await api.GetMetadataAsync(id,token).ConfigureAwait(false);
        if (metadata.GetProperty("mimeType").GetString()!="application/octet-stream" || metadata.GetProperty("sha256Checksum").GetString()!=Hash(expected) ||
            metadata.GetProperty("trashed").GetBoolean() || !metadata.GetProperty("ownedByMe").GetBoolean() ||
            !metadata.GetProperty("parents").EnumerateArray().Select(x=>x.GetString()).SequenceEqual([parent]))
            throw new InvalidDataException("The actual retained object is not the exact pending synthetic media in its original parent.");
        var folder=await api.GetMetadataAsync(parent,token).ConfigureAwait(false);
        if (folder.GetProperty("mimeType").GetString()!=ProbeDriveClient.FolderMime || !folder.GetProperty("ownedByMe").GetBoolean() || folder.GetProperty("trashed").GetBoolean())
            throw new InvalidDataException("The retained synthetic parent lost its owned folder scope.");
        var access=await credential.GetAccessTokenForRequestAsync(cancellationToken:token).ConfigureAwait(false);
        using var journal=new ProbeCreateJournal(Path.Combine(reportDirectory,"conditional-media-recovery-"+Guid.NewGuid().ToString("N")+".journal.jsonl"));
        var conditional=new ConditionalSyntheticClient(http,access,id,journal);
        var before=await conditional.ReadV2Async(token).ConfigureAwait(false);
        if (before.Status!=200 || before.Trashed || before.Version<1 || !EntityTagHeaderValue.TryParse(before.Etag,out var tag) || tag.IsWeak || tag.Tag=="*" || before.Etag==lastCondition)
            throw new InvalidDataException("The recovered fixture does not prove a new actual content entity tag.");
        var actual=await api.DownloadAsync(id,null,null,token).ConfigureAwait(false);
        if (!expected.AsSpan().SequenceEqual(actual) || await conditional.ReadV2Async(token).ConfigureAwait(false)!=before)
            throw new InvalidDataException("The recovered exact bytes changed during their read fence.");
        // The retained, formerly valid pre-completion tag must fail against the same
        // actual file. This never supplies the new current tag to a write operation.
        var stale=await conditional.WriteMediaAsync(first,lastCondition,token).ConfigureAwait(false);
        if (stale.Status!=412) throw new InvalidDataException("The retained stale pre-completion condition was not rejected.");
        var after=await conditional.ReadV2Async(token).ConfigureAwait(false);
        var final=await api.DownloadAsync(id,null,null,token).ConfigureAwait(false);
        if (!expected.AsSpan().SequenceEqual(final) || await conditional.ReadV2Async(token).ConfigureAwait(false)!=after)
            throw new InvalidDataException("Recovered bytes changed after the stale condition was rejected.");
        journal.Record(new { operation="recovered-rejection-observation",before,observed=after,completeSha256=Hash(final),
            numericVersionAdvance=after.Version-before.Version,exactIncrementHypothesis=after.Version==before.Version+1 });
        AssertRejectedContent(before,after);
        journal.Record(new { operation="same-fixture-lost-completion-recovered",id,parent,before,observed=after,sha256=Hash(final),bytes=final.Length });
        var report=new { operation="synthetic-media-same-id-recovery",observedAtUtc=DateTimeOffset.UtcNow,retainedJournal=journalName,
            exactPendingBytesRecovered=true,stalePreCompletionCondition=412,completeBytesPreserved=true,noNewIdentityAllocated=true,
            numericVersionStableOnRejection=after.Version==before.Version,rejectedUploadVersionAdvance=after.Version-before.Version,
            actualSocketLossTested=false,originalFilesChanged=false };
        await File.WriteAllBytesAsync(Path.Combine(reportDirectory,"conditional-media-recovery-"+Guid.NewGuid().ToString("N")+".json"),JsonSerializer.SerializeToUtf8Bytes(report),token).ConfigureAwait(false);
        Console.WriteLine("Same retained synthetic fixture recovered exact pending bytes; stale pre-completion tag rejected. No replacement identity or original write occurred.");
    }

    internal async Task RunAsync(string disposableRoot,string reportDirectory,CancellationToken token)
    {
        ProbeDriveClient.CheckId(disposableRoot);
        using var loss=new ConditionalMediaLossHandler(new SocketsHttpHandler { AllowAutoRedirect=false });
        using var http=new HttpClient(loss,disposeHandler:false) { Timeout=TimeSpan.FromSeconds(60) };
        var api=new ProbeDriveClient(http,credential);var root=await api.GetMetadataAsync(disposableRoot,token).ConfigureAwait(false);
        if (root.GetProperty("mimeType").GetString()!=ProbeDriveClient.FolderMime || root.GetProperty("trashed").GetBoolean() ||
            !root.GetProperty("ownedByMe").GetBoolean() || !root.GetProperty("capabilities").GetProperty("canAddChildren").GetBoolean())
            throw new InvalidDataException("The synthetic media root must be an owned writable non-trashed folder.");
        var run=Guid.NewGuid().ToString("N");
        using var journal=new ProbeCreateJournal(Path.Combine(reportDirectory,"conditional-media-"+run+".journal.jsonl"));
        var folder=await api.CreateFolderAsync(disposableRoot,"MPA conditional media synthetic probe "+run,journal,token).ConfigureAwait(false);
        var id=await api.ReserveIdAsync(token).ConfigureAwait(false);
        var first="MPA synthetic current metadata A; no user content.\n"u8.ToArray();
        var second="MPA synthetic current metadata B; no user content.\n"u8.ToArray();
        var third="MPA synthetic current metadata C; no user content.\n"u8.ToArray();
        journal.Record(new { operation="before-synthetic-media-create",id,parent=folder,bytes=first.Length,sha256=Hash(first) });
        await api.UploadResumableAsync(id,folder,"conditional-media-fixture.bin",first,token).ConfigureAwait(false);
        var access=await credential.GetAccessTokenForRequestAsync(cancellationToken:token).ConfigureAwait(false);
        var conditional=new ConditionalSyntheticClient(http,access,id,journal);
        var initial=await ExactAsync(first).ConfigureAwait(false);
        var updated=await conditional.WriteMediaAsync(second,initial.Etag!,token).ConfigureAwait(false);
        if (updated.Status!=200) throw new InvalidDataException("The matching actual entity tag did not permit the synthetic media update.");
        var after=await ExactAsync(second).ConfigureAwait(false);Advanced(initial,after);
        var stale=await conditional.WriteMediaAsync(third,initial.Etag!,token).ConfigureAwait(false);
        journal.Record(new { operation="synthetic-stale-media-result",status=stale.Status,stale.Version,expectedVersion=after.Version });
        if (stale.Status!=412)
            throw new InvalidDataException("A formerly valid stale media entity tag was not rejected: HTTP "+stale.Status+". Mutable media publication remains disabled.");
        var unchanged=await ExactAsync(second).ConfigureAwait(false);
        journal.Record(new { operation="after-rejected-synthetic-media",before=after,observed=unchanged,completeBytesStillExact=true });
        AssertRejectedContent(after,unchanged);
        loss.Arm(id,journal);var lost=false;
        try { await conditional.WriteMediaAsync(third,after.Etag!,token).ConfigureAwait(false); }
        catch (SyntheticMediaCompletionLostException) { lost=true; }
        if (!lost || !loss.Discarded) throw new InvalidDataException("The successful media completion was not discarded before client consumption.");
        var reconciled=await ExactAsync(third).ConfigureAwait(false);Advanced(after,reconciled);
        var staleAfterLoss=await conditional.WriteMediaAsync(first,after.Etag!,token).ConfigureAwait(false);
        if (staleAfterLoss.Status!=412) throw new InvalidDataException("Lost-ack recovery did not reject the stale captured entity tag.");
        var final=await ExactAsync(third).ConfigureAwait(false);AssertRejectedContent(reconciled,final);
        var report=new { operation="synthetic-conditional-media",observedAtUtc=DateTimeOffset.UtcNow,
            matchingCapturedTagUpdate=200,staleCapturedTagUpdate=412,staleCapturedTagAfterLostAck=412,
            completeBytesVerified=true,injectedCompletionTransportFailure=true,actualSocketLossTested=false,
            rejectedUploadVersionAdvance=unchanged.Version-after.Version,numericVersionStableOnRejection=unchanged.Version==after.Version,
            afterLostAckRejectedVersionAdvance=final.Version-reconciled.Version,unchangedContentEtagOnRejection=true,
            currentVersion=final.Version,currentSha256=Hash(third),externalTargetsAccepted=false,
            originalFilesChanged=false,fixtureRetained=true,permanentDeleteExposed=false };
        await File.WriteAllBytesAsync(Path.Combine(reportDirectory,"conditional-media-"+run+".json"),JsonSerializer.SerializeToUtf8Bytes(report),token).ConfigureAwait(false);
        Console.WriteLine("Synthetic v2 media captured/stale-tag and injected lost-completion checks passed; no existing originals changed.");

        async Task<ConditionalObservation> ExactAsync(byte[] expected)
        {
            // Capture checksum-bearing v3 metadata before the v2 read window. Do not
            // mix an additional metadata projection into the exact body-read fence.
            var metadata=await api.GetMetadataAsync(id,token).ConfigureAwait(false);
            var before=await conditional.ReadV2Async(token).ConfigureAwait(false);
            if (before.Status!=200 || before.Trashed || before.Version<1 || !EntityTagHeaderValue.TryParse(before.Etag,out var tag) || tag.IsWeak || tag.Tag=="*")
                throw new InvalidDataException("Synthetic media lacks its actual strong entity tag/version.");
            var downloaded=await api.DownloadAsync(id,null,null,token).ConfigureAwait(false);
            if (metadata.GetProperty("sha256Checksum").GetString()!=Hash(expected) || metadata.GetProperty("trashed").GetBoolean() ||
                !metadata.GetProperty("ownedByMe").GetBoolean() || !metadata.GetProperty("parents").EnumerateArray().Select(x=>x.GetString()).SequenceEqual([folder]) ||
                !expected.AsSpan().SequenceEqual(downloaded))
                throw new InvalidDataException("Synthetic media scope/checksum/download bytes changed.");
            var afterRead=await conditional.ReadV2Async(token).ConfigureAwait(false);
            journal.Record(new { operation="synthetic-media-read-fence",before,observed=afterRead,completeSha256=Hash(expected) });
            if (afterRead!=before) throw new InvalidDataException("Synthetic media changed during exact verification.");
            return before;
        }
    }
    private static void Advanced(ConditionalObservation before,ConditionalObservation after)
    { if (after.Version<=before.Version || after.Etag==before.Etag) throw new InvalidDataException("Synthetic media did not advance its actual version/entity tag."); }
    // A numeric version can move BETWEEN separately fenced captures. The original
    // exact+1 hypothesis was falsified by reopening the same completed fixture:
    // the stale tag rejected412 and every byte/tag/fence stayed exact, with delta0.
    // Do not substitute numeric version for ETag or pretend a 412 is a no-op receipt.
    // Within-capture metadata equality, full bytes and actual scope are checked by
    // each caller. Rewinds, a changed content tag, Trash and wrong status still fail.
    internal static void AssertRejectedContent(ConditionalObservation before,ConditionalObservation after)
    {
        if (before.Version<1 || after.Version<before.Version || before.Status!=200 || before.Trashed ||
            !EntityTagHeaderValue.TryParse(before.Etag,out var tag) || tag.IsWeak || tag.Tag=="*" ||
            after.Etag!=before.Etag || after.Trashed || after.Status!=200)
            throw new InvalidDataException("Rejected media upload changed content ETag, rewound version or lost its exact current file state.");
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}

internal sealed record RetainedSyntheticMedia(string FileId,string ParentId,string PreCompletionEtag);

internal sealed class SyntheticMediaCompletionLostException() : IOException("Synthetic media completion discarded before client consumption.");
internal sealed class ConditionalMediaLossHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private string? _id;
    private ProbeCreateJournal? _journal;
    internal bool Discarded { get; private set; }
    internal void Arm(string id,ProbeCreateJournal journal)
    { ProbeDriveClient.CheckId(id);ArgumentNullException.ThrowIfNull(journal);if (_id is not null || Discarded) throw new InvalidOperationException("Arm one synthetic media completion only.");_id=id;_journal=journal; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        var response=await base.SendAsync(request,token).ConfigureAwait(false);
        if (_id is not null && request.Method==HttpMethod.Put && request.RequestUri?.Host=="www.googleapis.com" &&
            request.RequestUri.AbsolutePath=="/upload/drive/v2/files/"+_id && response.StatusCode==HttpStatusCode.OK)
        {
            try { _journal!.Record(new { operation="synthetic-media-completion-discarded",id=_id,status=200,bodyConsumed=false }); }
            finally { response.Dispose(); }
            _id=null;Discarded=true;throw new SyntheticMediaCompletionLostException();
        }
        return response;
    }
}
