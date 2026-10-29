using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspaceConditionalJsonTests
{
    [TestMethod]
    public async Task ConditionalPublicationAwaitsLocalAdmissionAfterActualMetadataAndRetainsSameConditionOnRejection()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var capture=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        var admissions=0;var reject=true;var failure=new InvalidOperationException("Synthetic revoked local authority.");
        Task Admit(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();admissions++;Assert.IsGreaterThanOrEqualTo(3,fixture.MetadataReads);Assert.AreEqual(0,fixture.Puts);
            if(reject) throw failure;return Task.CompletedTask;
        }
        var actual=await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>connection.ReplaceAdmittedConditionalWorkspaceJsonAsync(capture,Next,Hash(Next),Admit,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(failure,actual);Assert.AreEqual(1,admissions);Assert.AreEqual(0,fixture.Puts);CollectionAssert.AreEqual(Initial,fixture.Stored);
        reject=false;var result=await connection.ReplaceAdmittedConditionalWorkspaceJsonAsync(capture,Next,Hash(Next),Admit,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2,admissions);Assert.AreEqual(1,fixture.Puts);Assert.AreEqual(8L,result.Version);CollectionAssert.AreEqual(Next,fixture.Stored);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConditionalPublicationJoinsUncooperativeLocalAdmissionAndCannotPutAfterCancellationOrClose(bool exit)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var capture=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Admit(CancellationToken token) {token.ThrowIfCancellationRequested();entered.TrySetResult();await release.Task.ConfigureAwait(false);}
        var pending=connection.ReplaceAdmittedConditionalWorkspaceJsonAsync(capture,Next,Hash(Next),Admit,caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if(exit) stopping=connection.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try {Assert.IsFalse(pending.IsCompleted);Assert.AreEqual(0,fixture.Puts);Assert.IsFalse(fixture.HandlerDisposed);if(exit) Assert.IsFalse(stopping.IsCompleted);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>pending).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Puts);CollectionAssert.AreEqual(Initial,fixture.Stored);Assert.AreEqual(3,fixture.MetadataReads);
        if(exit) Assert.IsTrue(fixture.HandlerDisposed);
    }
}
