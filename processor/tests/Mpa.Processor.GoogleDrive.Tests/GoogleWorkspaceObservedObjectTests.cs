using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspaceTests
{
    private static readonly string[] ObjectObservationOrder=["metadata","metadata","bytes"];

    [TestMethod]
    public async Task ObservedImmutableReadsCheckpointBothActualMetadataFencesAndOwnedBytesWithoutLaterIo()
    {
        var observer=new ObjectObserver();using var fixture=new Fixture {BlobObserved=()=>Assert.AreEqual(1,observer.Metadata.Count)};
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        observer.ByteHook=actual=>
        {
            Assert.AreEqual(2,fixture.MetadataCalls);Assert.AreEqual(7L,actual.Metadata.Version);Assert.AreEqual(Digest,actual.Sha256);
            Assert.IsTrue(MemoryMarshal.TryGetArray(actual.Bytes,out var copy));copy.Array![copy.Offset]=0;
            CollectionAssert.AreEqual(Content,actual.Bytes.ToArray());return Task.CompletedTask;
        };
        var result=await ReadObservedAsync(connection,observer).ConfigureAwait(false);
        CollectionAssert.AreEqual(ObjectObservationOrder,observer.Events);CollectionAssert.AreEqual(Content,result.Bytes.ToArray());
        Assert.AreEqual(4,fixture.Calls);Assert.AreEqual(1,fixture.BlobCalls);
    }

    [TestMethod]
    public async Task ObservedImmutableWrongDigestRetainsActualCompleteByteEvidenceBeforeRejectingTheReference()
    {
        var corrupt=Content.ToArray();corrupt[0]=0;using var fixture=new Fixture {Blob=corrupt};var observer=new ObjectObserver();
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>ReadObservedAsync(connection,observer)).ConfigureAwait(false);
        Assert.AreEqual(2,observer.Metadata.Count);Assert.IsNotNull(observer.Body);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(corrupt)),observer.Body.Sha256);
        Assert.AreNotEqual(Digest,observer.Body.Sha256);Assert.AreEqual(2,fixture.MetadataCalls);
    }

    [TestMethod]
    public async Task ObservedImmutableFirstCheckpointFailurePreventsBodyDownload()
    {
        using var fixture=new Fixture();var observer=new ObjectObserver {MetadataHook=_=>throw new InvalidOperationException("synthetic checkpoint failure")};
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>ReadObservedAsync(connection,observer)).ConfigureAwait(false);
        Assert.AreEqual(1,fixture.MetadataCalls);Assert.AreEqual(0,fixture.BlobCalls);Assert.IsNull(observer.Body);
    }

    [TestMethod]
    public async Task ObservedImmutableChangedFinalVersionIsDeliveredBeforeRejectingTheWindowAndNeverDeliversBytes()
    {
        using var fixture=new Fixture {After=Changed(root=>root["version"]="8")};var observer=new ObjectObserver();
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>ReadObservedAsync(connection,observer)).ConfigureAwait(false);
        Assert.AreEqual(2,observer.Metadata.Count);Assert.AreEqual(8L,observer.Metadata[^1].Version);
        Assert.IsNull(observer.Body);Assert.AreEqual(1,fixture.BlobCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObservedImmutableCancellationOrCloseJoinsFinalMetadataCheckpointAndDeniesLateByteAdmission(bool exit)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var observer=new ObjectObserver();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.MetadataHook=async _=>{if(observer.Metadata.Count==2) {entered.TrySetResult();await release.Task.ConfigureAwait(false);}};
        var reading=connection.ReadObservedWorkspaceObjectAsync("workspace","catalog-file",Content.Length,Digest,observer,caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if(exit) stopping=connection.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try {Assert.IsFalse(reading.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsNull(observer.Body);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsNull(observer.Body);Assert.AreEqual(2,fixture.MetadataCalls);Assert.AreEqual(1,fixture.BlobCalls);
    }

    [TestMethod]
    public async Task ObservedImmutableCloseJoinsUncooperativeCompleteByteCheckpointAndCannotReturnLate()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer=new ObjectObserver {ByteHook=async _=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);}};
        var reading=ReadObservedAsync(connection,observer);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var stopping=connection.DisposeAsync().AsTask();
        try {Assert.IsFalse(reading.IsCompleted);Assert.IsFalse(stopping.IsCompleted);Assert.IsNotNull(observer.Body);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(4,fixture.Calls);Assert.AreEqual(1,fixture.BlobCalls);
    }

    [TestMethod]
    public async Task ObservedImmutableOriginalMimeNeverReachesCheckpointOrBody()
    {
        using var fixture=new Fixture {Before=Changed(root=>root["mimeType"]="image/jpeg")};var observer=new ObjectObserver();
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>ReadObservedAsync(connection,observer)).ConfigureAwait(false);
        Assert.AreEqual(0,observer.Metadata.Count);Assert.AreEqual(0,fixture.BlobCalls);Assert.IsNull(observer.Body);
    }

    private static Task<DriveWorkspaceObject> ReadObservedAsync(GoogleAccountConnection connection,ObjectObserver observer)=>
        connection.ReadObservedWorkspaceObjectAsync("workspace","catalog-file",Content.Length,Digest,observer,CancellationToken.None);
    private sealed class ObjectObserver : IGoogleWorkspaceObjectObserver
    {
        internal List<string> Events {get;}=[];
        internal List<DriveWorkspaceObjectMetadata> Metadata {get;}=[];
        internal DriveWorkspaceObjectObservation? Body {get;private set;}
        internal Func<DriveWorkspaceObjectMetadata,Task>? MetadataHook {get;set;}
        internal Func<DriveWorkspaceObjectObservation,Task>? ByteHook {get;set;}
        public async Task RememberMetadataAsync(DriveWorkspaceObjectMetadata actual,CancellationToken token)
        {Events.Add("metadata");Metadata.Add(actual);if(MetadataHook is not null) await MetadataHook(actual).ConfigureAwait(false);}
        public async Task RememberBytesAsync(DriveWorkspaceObjectObservation actual,CancellationToken token)
        {Events.Add("bytes");Body=actual;if(ByteHook is not null) await ByteHook(actual).ConfigureAwait(false);}
    }
}
