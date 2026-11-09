using System.Diagnostics;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class WorkerProcessTests
{
    [TestMethod]
    public async Task NativeExitMustNotWaitForAnUncooperativePipeCancellationCallbackAndRetirementStillJoinsIt()
    {
        using var root=new WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var starting=FramedWorkerProcess.StartAsync(await SpecAsync(root.Path,"ignore-eof").ConfigureAwait(false),deadline.Token);
        await using var attempt=new JoinedWorkerTestAttempt(starting);
        var worker=await starting.ConfigureAwait(false);
        await using var frames=worker.ReadFramesAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        Assert.IsTrue(await frames.MoveNextAsync().ConfigureAwait(false));
        // Fault injection into the real pipe cancellation owner, without adding a
        // public production hook. Native IO cancellation need not be cooperative.
        var field=typeof(FramedWorkerProcess).GetField("_stopReading",BindingFlags.Instance|BindingFlags.NonPublic) ?? throw new InvalidOperationException("The real pipe cancellation owner is unavailable.");
        var cancellation=(CancellationTokenSource)field.GetValue(worker)!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var release=new ManualResetEventSlim(false);
        using var registration=cancellation.Token.Register(()=>{entered.TrySetResult();release.Wait();});
        using var child=Process.GetProcessById(worker.ProcessId);var stopping=worker.DisposeAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsFalse(stopping.IsCompleted,"Physical exit must precede joining a blocked IO callback.");
            Assert.IsFalse(worker.RetirementConfirmed,"An exited process alone cannot retire its pipe owner.");
        }
        finally {release.Set();await stopping.ConfigureAwait(false);}
        Assert.IsTrue(worker.ExitConfirmed);Assert.IsTrue(worker.RetirementConfirmed);
        using var original=File.Open(Path.Combine(root.Path,"worker-read-lease.bin"),FileMode.Open,FileAccess.Read,FileShare.None);Assert.AreEqual(42,original.ReadByte());
    }
}
