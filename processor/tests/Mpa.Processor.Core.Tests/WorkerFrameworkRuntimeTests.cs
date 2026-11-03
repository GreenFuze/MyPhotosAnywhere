using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class WorkerProcessTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorkerFrameworkDiscoveryUsesOnlyExplicitlyCapturedExecutingRuntimeAndRetiresActualChild(bool selected)
    {
        using var root=new WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spec=(await SpecAsync(root.Path,"framework-runtime").ConfigureAwait(false)) with{UseCurrentFrameworkRuntime=selected};
        await using var worker=await FramedWorkerProcess.StartAsync(spec,deadline.Token).ConfigureAwait(false);
        await using var frames=worker.ReadFramesAsync(deadline.Token).GetAsyncEnumerator();Assert.IsTrue(await frames.MoveNextAsync().ConfigureAwait(false));
        var expected=selected?Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..")):"not-set";
        Assert.AreEqual(expected,await File.ReadAllTextAsync(Path.Combine(root.Path,"framework-runtime.txt"),deadline.Token).ConfigureAwait(false));
        await worker.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(worker.RetirementConfirmed);Assert.AreEqual(0L,new FileInfo(spec.NewDiagnosticPath).Length);
    }
}
