using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutMetadataJobTests
{
    private static readonly string MediaHash=new('d',64);
    private const string Sidecar="{\"title\":\"a.jpg\",\"photoTakenTime\":{\"timestamp\":\"-1\"},\"geoData\":{\"latitude\":31.7,\"longitude\":35.2},\"future\":{\"keep\":true}}";
    private static ArtifactDescriptor Source()=>new(Encoding.UTF8.GetByteCount(Sidecar),Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sidecar))),"input.bin");

    [TestMethod]
    public void TakeoutMetadataJobCapturesExactSidecarMediaAndRecipeAndRejectsConflictingReplays()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);
        var repository=new TakeoutMetadataJobRepository(database);var job=Guid.NewGuid();var item=Guid.NewGuid();var media=Guid.NewGuid();var source=Source();
        Assert.IsTrue(repository.Ensure(job,item,media,MediaHash,source));Assert.IsFalse(repository.Ensure(job,item,media,MediaHash,source));
        Assert.ThrowsExactly<ConflictingJobItemException>(()=>repository.Ensure(job,item,Guid.NewGuid(),MediaHash,source));
        Assert.ThrowsExactly<ConflictingJobItemException>(()=>repository.Ensure(job,item,media,new string('e',64),source));
        var work=repository.ClaimNext();Assert.IsNotNull(work);Assert.AreEqual(job,work.Lease.JobId);Assert.AreEqual(item,work.Lease.ItemId);
        Assert.AreEqual(media,work.MediaId);Assert.AreEqual(MediaHash,work.MediaInputSha256);Assert.AreEqual(source.Sha256,work.Sidecar.Sha256);Assert.AreEqual(source.Bytes,work.Sidecar.Bytes);
        StringAssert.Contains(work.Lease.CapturedItemContext,TakeoutMetadataDocument.RecipeSha256);repository.AssertCurrent(work);
        Assert.IsNull(repository.ClaimNext());Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());
    }

    [TestMethod]
    public void TakeoutMetadataJobPauseAndReopenPreserveCapturedInputAndRejectTheOldOwner()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var job=Guid.NewGuid();var item=Guid.NewGuid();var media=Guid.NewGuid();TakeoutMetadataWork original;
        using(var database=new ProcessorStateDatabase(root.Path))
        {
            var repository=new TakeoutMetadataJobRepository(database);repository.Ensure(job,item,media,MediaHash,Source());original=repository.ClaimNext()!;
            var jobs=new DurableJobRepository(database);var stopped=jobs.Pause(job).Single();
            Assert.ThrowsExactly<StaleStageLeaseException>(()=>repository.AssertCurrent(original));
            // No child was launched for this claimed attempt: the exact owner has
            // no physical worker/IO to join before releasing its stopped lease.
            jobs.ReleaseStoppedAttempt(stopped);jobs.Resume(job);
        }
        using var reopened=new ProcessorStateDatabase(root.Path);var current=new TakeoutMetadataJobRepository(reopened);
        Assert.ThrowsExactly<InvalidOperationException>(()=>current.AssertCurrent(original));
        var retry=current.ClaimNext()!;Assert.AreEqual(2,retry.Lease.Attempt);Assert.AreNotEqual(original.Lease.LeaseId,retry.Lease.LeaseId);
        Assert.AreEqual(original.Lease.CapturedItemContext,retry.Lease.CapturedItemContext);Assert.AreEqual(MediaHash,retry.MediaInputSha256);Assert.AreEqual(media,retry.MediaId);Assert.AreEqual(original.Sidecar.Sha256,retry.Sidecar.Sha256);
        current.AssertCurrent(retry);Assert.AreEqual(0,new DurableJobRepository(reopened).PendingReceiptCount());
    }

    [TestMethod]
    public void TakeoutMetadataJobCannotClaimMediaUploadOrBorrowForeignNativeState()
    {
        using var first=new WorkerProcessTests.WorkerTestDirectory();using var second=new WorkerProcessTests.WorkerTestDirectory();
        using var a=new ProcessorStateDatabase(first.Path);using var b=new ProcessorStateDatabase(second.Path);
        var jobs=new DurableJobRepository(a);var upload=Guid.NewGuid();var recipe=new string('a',64);
        jobs.CreateWithInitialItem(upload,"takeout-import","{}"u8,new(Guid.NewGuid(),recipe,[new("upload",recipe,[])]));
        var repository=new TakeoutMetadataJobRepository(a);Assert.IsNull(repository.ClaimNext());
        repository.Ensure(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),MediaHash,Source());var work=repository.ClaimNext()!;
        Assert.ThrowsExactly<InvalidOperationException>(()=>new TakeoutMetadataJobRepository(b).AssertCurrent(work));
        Assert.AreEqual(1,jobs.Summary(upload).Queued);Assert.AreEqual(upload,jobs.ClaimNext("takeout-import")!.JobId);
        Assert.AreEqual(0,jobs.PendingReceiptCount());
    }

    [TestMethod]
    public void TakeoutMetadataJobDetectsRecipeAndContextCorruptionBeforeAnyWorkerAdmission()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);
        var repository=new TakeoutMetadataJobRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Guid.NewGuid(),Guid.NewGuid(),MediaHash,Source());var work=repository.ClaimNext()!;
        using(var change=database.Connection.CreateCommand())
        {change.CommandText="UPDATE job_item SET input_fingerprint=$hash WHERE job_id=$job";change.Parameters.AddWithValue("$hash",new string('b',64));change.Parameters.AddWithValue("$job",job.ToString("D"));Assert.AreEqual(1,change.ExecuteNonQuery());}
        Assert.ThrowsExactly<InvalidDataException>(()=>repository.AssertCurrent(work));
        using(var change=database.Connection.CreateCommand())
        {change.CommandText="UPDATE item_stage SET recipe_hash=$hash WHERE job_id=$job";change.Parameters.AddWithValue("$hash",new string('c',64));change.Parameters.AddWithValue("$job",job.ToString("D"));Assert.AreEqual(1,change.ExecuteNonQuery());}
        Assert.ThrowsExactly<StaleStageLeaseException>(()=>repository.AssertCurrent(work));Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());
    }

    [TestMethod]
    public void TakeoutMetadataJobRefusesOversizedOrDifferentAssignedInputBeforeAdmission()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);var repository=new TakeoutMetadataJobRepository(database);var job=Guid.NewGuid();
        Assert.ThrowsExactly<ArgumentException>(()=>repository.Ensure(job,Guid.NewGuid(),Guid.NewGuid(),MediaHash,new(128*1024+1,new string('a',64),"input.bin")));
        Assert.ThrowsExactly<ArgumentException>(()=>repository.Ensure(job,Guid.NewGuid(),Guid.NewGuid(),MediaHash,new(1,new string('a',64),"other.bin")));
        Assert.ThrowsExactly<ArgumentException>(()=>repository.Ensure(job,Guid.NewGuid(),Guid.Empty,MediaHash,Source()));
        Assert.ThrowsExactly<ArgumentException>(()=>repository.Ensure(job,Guid.NewGuid(),Guid.NewGuid(),"unknown",Source()));
        Assert.ThrowsExactly<KeyNotFoundException>(()=>new DurableJobRepository(database).Summary(job));Assert.IsNull(repository.ClaimNext());
    }

    [TestMethod]
    public void TakeoutArtifactRecomputesFactsAndRejectsSubstitutionInsteadOfTrustingWorkerFields()
    {
        var raw=Encoding.UTF8.GetBytes(Sidecar);var source=Source();var metadata=TakeoutMetadataDocument.ParseSource(raw);var encoded=metadata.EncodeArtifact();
        var verified=TakeoutMetadataDocument.ReadVerifiedArtifact(encoded,source);Assert.AreEqual(-1000L,verified.CapturedUtc);Assert.AreEqual(31.7,verified.Latitude);
        CollectionAssert.AreEqual(raw,verified.CopyRawSource());
        var altered=JsonNode.Parse(encoded)!.AsObject();altered["title"]="substitute.jpg";
        Assert.ThrowsExactly<ProtocolFormatException>(()=>TakeoutMetadataDocument.ReadVerifiedArtifact(JsonSerializer.SerializeToUtf8Bytes(altered),source));
        altered=JsonNode.Parse(encoded)!.AsObject();altered["unexpected"]=true;
        Assert.ThrowsExactly<ProtocolFormatException>(()=>TakeoutMetadataDocument.ReadVerifiedArtifact(JsonSerializer.SerializeToUtf8Bytes(altered),source));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>TakeoutMetadataDocument.ReadVerifiedArtifact(encoded,new(source.Bytes,new string('f',64),"input.bin")));
        Array.Fill(raw,(byte)'x');CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Sidecar),metadata.CopyRawSource());
    }

    [TestMethod]
    public void TakeoutSharedReaderRejectsMalformedTextAsMetadataIssueAndKeepsRawInputUntouched()
    {
        var malformed="{\"title\":\"\\uD800\",\"creationTime\":{}}"u8.ToArray();var before=malformed.ToArray();
        var error=Assert.ThrowsExactly<TakeoutMetadataIssueException>(()=>TakeoutMetadataDocument.ParseSource(malformed));Assert.AreEqual("invalid-metadata",error.Code);
        CollectionAssert.AreEqual(before,malformed);
        var deep=Encoding.UTF8.GetBytes(new string('[',33)+"0"+new string(']',33));
        Assert.ThrowsExactly<TakeoutMetadataIssueException>(()=>TakeoutMetadataDocument.ParseSource(deep));
    }
}
