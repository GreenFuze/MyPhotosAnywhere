using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class WorkerResultAdmissionTests
{
    [TestMethod]
    public async Task ActualWorkerOwnerRejectsOriginalCancellationBeforeItsLinkedCallbackRuns()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var original=new CancellationTokenSource();await using var supervisor=new ProcessorWorkSupervisor();
        var operation=new AdmissionOperation(await WorkerProcessTests.SpecAsync(root.Path,"ignore-eof").ConfigureAwait(false));
        var running=supervisor.Start(operation,original.Token);
        await operation.Ready.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        using var releaseCallback=new ManualResetEventSlim();var callbackEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Registered after the supervisor linked token: the real original caller
        // is canceled while its newer callback holds delivery to the supervisor.
        using var callback=original.Token.Register(()=>
        {
            callbackEntered.TrySetResult();if(!releaseCallback.Wait(TimeSpan.FromSeconds(30)))throw new TimeoutException("Original cancellation callback was not released.");
        });
        var cancellation=Task.Run(original.Cancel,CancellationToken.None);
        try
        {
            await callbackEntered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);Assert.IsTrue(original.IsCancellationRequested);Assert.IsFalse(operation.Linked.IsCancellationRequested);
            operation.AllowCommit.TrySetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(()=>running.WaitAsync(deadline.Token)).ConfigureAwait(false);
            Assert.AreEqual(0,operation.Commits);Assert.IsTrue(operation.Retired);Assert.IsNull(supervisor.Failure);
        }
        finally {operation.AllowCommit.TrySetResult();releaseCallback.Set();await cancellation.ConfigureAwait(false);}
        Assert.AreEqual(ProcessorWorkState.Ready,supervisor.State);
    }

    private sealed class AdmissionOperation(WorkerLaunchSpec launch) : OwnedWorkerOperation
    {
        private FramedWorkerProcess? _worker;
        public override WorkerLaunchSpec Launch=>launch;
        internal TaskCompletionSource Ready { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowCommit { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Linked { get; private set; }
        internal int Commits { get; private set; }
        internal bool Retired { get; private set; }
        public override async Task ExecuteAsync(FramedWorkerProcess worker,CancellationToken cancellationToken)
        {
            _worker=worker;Linked=cancellationToken;
            await using var frames=worker.ReadFramesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            if(!await frames.MoveNextAsync().ConfigureAwait(false))throw new InvalidOperationException("Actual admission fixture did not start.");
            Ready.SetResult();await AllowCommit.Task.WaitAsync(TimeSpan.FromSeconds(30),cancellationToken).ConfigureAwait(false);
            AdmitDurableResult(()=>Commits++,cancellationToken);
        }
        public override ValueTask DisposeAsync()
        {
            if(_worker is not null&&!_worker.RetirementConfirmed)throw new InvalidOperationException("Actual admission fixture has not retired.");
            Retired=true;return ValueTask.CompletedTask;
        }
    }
}
