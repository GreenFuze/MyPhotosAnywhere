using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspacePublicationTests
{
    private static Task<DriveWorkspaceObject> AdmittedCreate(GoogleAccountConnection connection,
        Func<CancellationToken,Task> admit,CancellationToken token=default)=>
        connection.CreateAdmittedWorkspaceObjectAsync("workspace","allocated-file","application/json",Content,Digest,admit,token);

    [TestMethod]
    public async Task AdmittedCreateChecksEveryPhysicalRequestAndRetainsItsFirstOwnedBytes()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var input=Content.ToArray();var admissions=0;
        var result=await connection.CreateAdmittedWorkspaceObjectAsync("workspace","allocated-file","application/json",input,Digest,
            token=>{token.ThrowIfCancellationRequested();admissions++;input[0]=0;return Task.CompletedTask;},CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(Content,result.Bytes.ToArray());Assert.AreEqual(0,input[0]);
        Assert.AreEqual(7,admissions);Assert.AreEqual(admissions,fixture.Calls);
        Assert.AreEqual(1,fixture.Creates);Assert.AreEqual(0,fixture.Allocations);
        Assert.AreEqual(2,fixture.RootReads);Assert.AreEqual(2,fixture.MetadataReads);Assert.AreEqual(1,fixture.BlobReads);
        Assert.IsTrue(fixture.Responses.All(value=>value.Disposed));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task AdmittedCreateStopsAtEachActualBoundaryAndCannotLeakItsGrantToAnotherCall(int rejectedRequest)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var admissions=0;var failure=new InvalidOperationException("This exact original header no longer has current admission.");
        var error=await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>AdmittedCreate(connection,token=>
        {
            token.ThrowIfCancellationRequested();if(++admissions==rejectedRequest) throw failure;
            return Task.CompletedTask;
        })).ConfigureAwait(false);
        Assert.AreSame(failure,error);Assert.AreEqual(rejectedRequest,admissions);Assert.AreEqual(rejectedRequest-1,fixture.Calls);
        Assert.AreEqual(rejectedRequest<=3?0:1,fixture.Creates);Assert.AreEqual(0,fixture.Allocations);
        Assert.AreEqual("allocated-file",(await Create(connection).ConfigureAwait(false)).FileId);
        Assert.AreEqual(rejectedRequest,admissions);Assert.IsTrue(fixture.Responses.All(value=>value.Disposed));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AdmittedCreateJoinsAStoppedPreCreateAdmissionWithoutSendingTheWrite(bool exit)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        using var caller=new CancellationTokenSource();var calls=0;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creating=AdmittedCreate(connection,async token=>
        {token.ThrowIfCancellationRequested();if(++calls==3){entered.TrySetResult();await release.Task.ConfigureAwait(false);}},caller.Token);
        var stopping=Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=connection.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(creating.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);
            Assert.AreEqual(0,fixture.Creates);Assert.AreEqual(2,fixture.Calls);
        }
        finally{release.TrySetResult();try{await creating.ConfigureAwait(false);}catch(OperationCanceledException){}}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>creating).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Creates);Assert.AreEqual(0,fixture.Allocations);
        Assert.IsTrue(fixture.Responses.All(value=>value.Disposed));
    }

    [TestMethod]
    public async Task AdmittedCreateJoinsAnEnteredUncooperativeWriteAndKeepsItsCapturedIdentityForRetry()
    {
        using var fixture=new Fixture{BlockCreate=true};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        using var caller=new CancellationTokenSource();var admissions=0;
        var creating=AdmittedCreate(connection,token=>{token.ThrowIfCancellationRequested();admissions++;return Task.CompletedTask;},caller.Token);
        var stopping=Task.CompletedTask;
        try
        {
            await fixture.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await caller.CancelAsync().ConfigureAwait(false);stopping=connection.DisposeAsync().AsTask();
            Assert.IsFalse(creating.IsCompleted);Assert.IsFalse(stopping.IsCompleted);
            Assert.AreEqual(1,fixture.Creates);Assert.AreEqual(3,admissions);
        }
        finally{fixture.CreateRelease.TrySetResult(true);try{await creating.ConfigureAwait(false);}catch(OperationCanceledException){}}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>creating).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.MetadataReads);Assert.AreEqual(0,fixture.Allocations);
        Assert.IsTrue(fixture.Responses.All(value=>value.Disposed));
    }
}
