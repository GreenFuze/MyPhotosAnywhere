using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspacePublicationTests
{
    [TestMethod]
    public async Task ObservedAllocationJoinsItsExactCanonicalReceiptBeforeReturningAndCreatesNoFile()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var receipts=new List<DriveWorkspaceAllocation>();var admissions=0;
        var observer=new AllocationRecorder(value=>{receipts.Add(value);return Task.CompletedTask;});
        var id=await connection.ObserveWorkspaceObjectIdAllocationAsync(observer,token=>
        {token.ThrowIfCancellationRequested();admissions++;return Task.CompletedTask;},CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("allocated-file",id);Assert.AreEqual(1,receipts.Count);
        Assert.AreEqual(new DriveWorkspaceAllocation("account-A",id),receipts[0]);Assert.IsGreaterThanOrEqualTo(1,admissions);
        Assert.AreEqual(1,fixture.Allocations);Assert.AreEqual(0,fixture.Creates);Assert.AreEqual(0,fixture.RootReads);
        Assert.IsTrue(fixture.Responses.All(value=>value.Disposed));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObservedAllocationCancellationOrExitCannotLoseAnEnteredKnownIdCheckpoint(bool exit)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DriveWorkspaceAllocation? saved=null;
        var observer=new AllocationRecorder(async value=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);saved=value;});
        var pending=connection.ObserveWorkspaceObjectIdAllocationAsync(observer,_=>Task.CompletedTask,caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if(exit) stopping=connection.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try{Assert.IsFalse(pending.IsCompleted);Assert.IsNull(saved);if(exit) Assert.IsFalse(stopping.IsCompleted);}
        finally{release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>pending).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(new DriveWorkspaceAllocation("account-A","allocated-file"),saved);
        Assert.AreEqual(1,fixture.Allocations);Assert.AreEqual(0,fixture.Creates);Assert.IsTrue(fixture.Responses.All(value=>value.Disposed));
    }

    [TestMethod]
    public async Task ObservedAllocationRejectionCannotGenerateAnIdOrLeakAdmissionIntoTheNextOrdinaryCall()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var failure=new InvalidOperationException("Actual current authority changed.");var observed=0;
        var observer=new AllocationRecorder(_=>{observed++;return Task.CompletedTask;});
        var actual=await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>connection.ObserveWorkspaceObjectIdAllocationAsync(
            observer,_=>throw failure,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(failure,actual);Assert.AreEqual(0,observed);Assert.AreEqual(0,fixture.Allocations);Assert.AreEqual(0,fixture.Creates);
        Assert.AreEqual("allocated-file",await connection.AllocateWorkspaceObjectIdAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(1,fixture.Allocations);Assert.AreEqual(0,observed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObservedAllocationJoinsUncooperativePreRequestAdmissionBeforeCancellationOrExit(bool exit)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var observed=0;
        var observer=new AllocationRecorder(_=>{observed++;return Task.CompletedTask;});
        async Task Admit(CancellationToken token){token.ThrowIfCancellationRequested();entered.TrySetResult();await release.Task.ConfigureAwait(false);}
        var pending=connection.ObserveWorkspaceObjectIdAllocationAsync(observer,Admit,caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if(exit) stopping=connection.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try{Assert.IsFalse(pending.IsCompleted);Assert.AreEqual(0,fixture.Allocations);Assert.AreEqual(0,observed);if(exit) Assert.IsFalse(stopping.IsCompleted);}
        finally{release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>pending).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Allocations);Assert.AreEqual(0,fixture.Creates);Assert.AreEqual(0,observed);
    }

    [TestMethod]
    public async Task ObservedAllocationPropagatesItsActualCheckpointFailureWithoutRepeatingTheProviderRequest()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var failure=new IOException("Known-ID checkpoint needs retry.");DriveWorkspaceAllocation? known=null;
        var observer=new AllocationRecorder(value=>{known=value;throw failure;});
        var actual=await Assert.ThrowsExactlyAsync<IOException>(()=>connection.ObserveWorkspaceObjectIdAllocationAsync(observer,_=>Task.CompletedTask,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(failure,actual);Assert.AreEqual(new DriveWorkspaceAllocation("account-A","allocated-file"),known);
        Assert.AreEqual(1,fixture.Allocations);Assert.AreEqual(0,fixture.Creates);Assert.IsTrue(fixture.Responses.All(value=>value.Disposed));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"space\":\"drive\",\"ids\":[\"first\",\"second\"]}")]
    public async Task ObservedAllocationNeverCheckpointsMalformedOrAmbiguousProviderIdentities(string body)
    {
        using var fixture=new Fixture{AllocationBody=body};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);var observed=0;
        var observer=new AllocationRecorder(_=>{observed++;return Task.CompletedTask;});
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>connection.ObserveWorkspaceObjectIdAllocationAsync(observer,_=>Task.CompletedTask,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,observed);Assert.AreEqual(1,fixture.Allocations);Assert.AreEqual(0,fixture.Creates);
    }

    private sealed class AllocationRecorder(Func<DriveWorkspaceAllocation,Task> remember) : IGoogleWorkspaceAllocationObserver
    {public Task RememberAllocatedIdAsync(DriveWorkspaceAllocation allocation)=>remember(allocation);}
}
