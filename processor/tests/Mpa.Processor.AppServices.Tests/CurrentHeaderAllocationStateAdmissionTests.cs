using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentHeaderAllocationStateAdmissionCannotDeadlockWithAScanWaitingOnItsActualAccountLease()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admit=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creates=fixture.Data.Creates;var allocations=fixture.Data.Allocations;
        using var scanCancellation=new CancellationTokenSource();Task<LocalCurrentControlCapture>? scan=null;
        var account=fixture.Data.Setup.Accounts.Service.ReadDriveAsync("permission-A",async(connection,revision,token)=>
        {
            Assert.IsNotNull(connection);Assert.AreEqual(admission.Original.Inventory.Context.AccountRevision,revision);
            entered.TrySetResult();await admit.Task.ConfigureAwait(false);
            return await fixture.Reader.AssertCatalogHeaderCloudAdmissionAsync(admission,token).ConfigureAwait(false);
        },CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            scan=fixture.Reader.CaptureAsync(scanCancellation.Token);
            using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(true)
            {
                var current=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
                    new LocalCurrentControlObservations(database,fixture.Data.Budget).Current(),ready.Token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Concurrent capture lost the actual current nonce.");
                if(current.Id!=admission.Original.Inventory.RootAttemptId) break;
                await Task.Delay(10,ready.Token).ConfigureAwait(false);
            }
            // Capture now owns the reader's network lane and waits for this real
            // account lease. Its state-only admission must never wait on that lane.
            admit.TrySetResult();
            await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>account)
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally
        {
            admit.TrySetResult();await scanCancellation.CancelAsync().ConfigureAwait(false);
            if(scan is not null){try{await scan.ConfigureAwait(false);}catch(OperationCanceledException){}}
            try{await account.ConfigureAwait(false);}catch(CurrentControlObservationConflictException){}
        }
        Assert.AreEqual(creates,fixture.Data.Creates);Assert.AreEqual(allocations,fixture.Data.Allocations);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
        Assert.IsNull((await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!.FileId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderAllocationStateAdmissionJoinsEnteredSqliteDuringCancellationAndReaderExit(bool exit)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();using var caller=new CancellationTokenSource();
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            HeaderAdmissionProbeConnection(database).CreateFunction<int>("mpa_header_admission_probe",()=>{entered.TrySetResult();release.Wait();return 1;});
            HeaderAdmissionProbeSql(database,"CREATE TEMP VIEW workspace_publication AS SELECT * FROM main.workspace_publication WHERE mpa_header_admission_probe()=1");
            return true;
        },CancellationToken.None).ConfigureAwait(false);
        var checking=fixture.Reader.AssertCatalogHeaderCloudAdmissionAsync(admission,caller.Token);
        var stopping=Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=fixture.Reader.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(checking.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);
        }
        finally
        {
            release.Set();try{await checking.ConfigureAwait(false);}catch(OperationCanceledException){}
            await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            {
                HeaderAdmissionProbeSql(database,"DROP VIEW temp.workspace_publication");
                HeaderAdmissionProbeConnection(database).CreateFunction<int>("mpa_header_admission_probe",(Func<int>?)null);return true;
            },CancellationToken.None).ConfigureAwait(false);
        }
        await Assert.ThrowsAsync<OperationCanceledException>(()=>checking).ConfigureAwait(false);
        if(exit) Assert.IsTrue(fixture.Reader.ExitConfirmed);
        Assert.IsNull((await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!.FileId);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    // Fixture instrumentation only, always under its actual serialized state
    // callback. The borrowed connection remains owned by the Core database.
    private static SqliteConnection HeaderAdmissionProbeConnection(ProcessorStateDatabase database)=>
        typeof(ProcessorStateDatabase).GetProperty("Connection",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(database) as SqliteConnection
            ?? throw new InvalidOperationException("Fixture cannot instrument its actual private SQLite owner.");
    private static void HeaderAdmissionProbeSql(ProcessorStateDatabase database,string sql)
    {
        using var command=HeaderAdmissionProbeConnection(database).CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();
    }
}
