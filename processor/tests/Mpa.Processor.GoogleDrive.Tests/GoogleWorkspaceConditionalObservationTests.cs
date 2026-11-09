using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspaceConditionalJsonTests
{
    private static readonly string[] ObservationSteps=["metadata","bytes"];
    [TestMethod]
    public async Task ObservedConditionalJsonCheckpointsMetadataBeforeBodyAndCompleteOwnedBytesBeforeReturn()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var observer=new ConditionalObserver
        {
            MetadataAction=(_,_)=>{Assert.AreEqual(1,fixture.MetadataReads);Assert.AreEqual(0,fixture.BlobReads);return Task.CompletedTask;},
            BytesAction=(_,_)=>{Assert.AreEqual(2,fixture.MetadataReads);Assert.AreEqual(1,fixture.BlobReads);return Task.CompletedTask;}
        };
        var result=await connection.ObserveConditionalWorkspaceJsonAsync("workspace","current-file",observer,CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(ObservationSteps,observer.Events.ToArray());Assert.IsNotNull(observer.Metadata);Assert.IsNotNull(observer.Bytes);
        Assert.AreEqual("account-A",observer.Metadata.AccountId);Assert.AreEqual("workspace",observer.Metadata.FolderId);Assert.AreEqual("current-file",observer.Metadata.FileId);
        Assert.AreEqual(7L,observer.Metadata.Version);Assert.AreEqual(Initial.Length,observer.Metadata.ByteLength);
        Assert.AreEqual(result.Metadata.ParentsSha256,observer.Metadata.ParentsSha256);Assert.AreEqual(result.EntityTag,observer.Metadata.EntityTag);
        CollectionAssert.AreEqual(Initial,observer.Bytes);Assert.AreEqual(0,fixture.Puts);
    }

    [TestMethod]
    public async Task ObservedConditionalJsonCannotDownloadABodyAfterItsMetadataCheckpointFails()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var observer=new ConditionalObserver { MetadataAction=(_,_)=>throw new IOException("Synthetic durable metadata checkpoint failure.") };
        await Assert.ThrowsExactlyAsync<IOException>(()=>connection.ObserveConditionalWorkspaceJsonAsync("workspace","current-file",observer,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNotNull(observer.Metadata);Assert.IsNull(observer.Bytes);Assert.AreEqual(1,fixture.MetadataReads);Assert.AreEqual(0,fixture.BlobReads);
        Assert.AreEqual(7L,(await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false)).Version);
    }

    [TestMethod]
    public async Task ObservedConditionalJsonDoesNotReturnWhenTheCompleteByteCheckpointFails()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var observer=new ConditionalObserver { BytesAction=(_,_)=>throw new IOException("Synthetic exact-byte checkpoint failure.") };
        await Assert.ThrowsExactlyAsync<IOException>(()=>connection.ObserveConditionalWorkspaceJsonAsync("workspace","current-file",observer,CancellationToken.None)).ConfigureAwait(false);
        CollectionAssert.AreEqual(Initial,observer.Bytes!);Assert.AreEqual(2,fixture.MetadataReads);Assert.AreEqual(1,fixture.BlobReads);Assert.AreEqual(0,fixture.Puts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObservedConditionalJsonCancellationOrCloseJoinsMetadataCheckpointAndDoesNotStartBody(bool exit)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer=new ConditionalObserver {MetadataAction=async(_,_)=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);}};
        var reading=connection.ObserveConditionalWorkspaceJsonAsync("workspace","current-file",observer,caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if(exit) stopping=connection.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try {Assert.IsFalse(reading.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(fixture.HandlerDisposed);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.BlobReads);Assert.IsNull(observer.Bytes);Assert.AreEqual(1,fixture.MetadataReads);
    }

    [TestMethod]
    public async Task ObservedConditionalJsonCloseJoinsCompleteByteCheckpointAndCannotReturnLateBytes()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer=new ConditionalObserver {BytesAction=async(_,_)=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);}};
        var reading=connection.ObserveConditionalWorkspaceJsonAsync("workspace","current-file",observer,CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=connection.DisposeAsync().AsTask();
        try {Assert.IsFalse(reading.IsCompleted);Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(fixture.HandlerDisposed);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        CollectionAssert.AreEqual(Initial,observer.Bytes!);Assert.AreEqual(2,fixture.MetadataReads);Assert.AreEqual(1,fixture.BlobReads);Assert.IsTrue(fixture.HandlerDisposed);
    }

    [TestMethod]
    public async Task ObservedConditionalJsonCannotCheckpointACompleteBodyWhenItsActualReadFenceChanges()
    {
        using var fixture=new Fixture {Transform=(call,metadata)=>{if(call==2) metadata["version"]="8";}};
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);var observer=new ConditionalObserver();
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>connection.ObserveConditionalWorkspaceJsonAsync("workspace","current-file",observer,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNotNull(observer.Metadata);Assert.IsNull(observer.Bytes);Assert.AreEqual(2,fixture.MetadataReads);Assert.AreEqual(1,fixture.BlobReads);
    }

    private sealed class ConditionalObserver : IGoogleWorkspaceConditionalObserver
    {
        public List<string> Events { get; }=[];
        public DriveWorkspaceConditionalMetadata? Metadata { get; private set; }
        public byte[]? Bytes { get; private set; }
        public Func<DriveWorkspaceConditionalMetadata,CancellationToken,Task>? MetadataAction { get; init; }
        public Func<DriveWorkspaceConditionalJson,CancellationToken,Task>? BytesAction { get; init; }
        public async Task RememberMetadataAsync(DriveWorkspaceConditionalMetadata metadata,CancellationToken token)
        {Events.Add("metadata");Metadata=metadata;if(MetadataAction is not null) await MetadataAction(metadata,token).ConfigureAwait(false);}
        public async Task RememberBytesAsync(DriveWorkspaceConditionalJson observed,CancellationToken token)
        {Events.Add("bytes");Bytes=observed.Bytes.ToArray();if(BytesAction is not null) await BytesAction(observed,token).ConfigureAwait(false);}
    }
}
