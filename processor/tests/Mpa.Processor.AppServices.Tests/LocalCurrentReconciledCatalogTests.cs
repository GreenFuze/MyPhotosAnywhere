using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentReconciledCatalogSealsThenReadsTheInventoryAndOneLastRootBeforeAtomicEmptyActivation()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var trace=InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareReconciledGraphAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        trace.Clear();var before=fixture.Http.BodyReads;
        var catalog=await fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before+1,fixture.Http.BodyReads);Assert.AreEqual("root",trace[^1]);
        Assert.IsTrue(trace.IndexOf("final-changes")<trace.LastIndexOf("root"));Assert.AreEqual(0L,catalog.Graph.Assets);
        var calls=fixture.Data.Setup.Accounts.Readers.DocumentCalls;var bodies=fixture.Http.BodyReads;var metadata=fixture.Http.MetadataReads;
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.ActivateReconciledAsync(catalog,stage,CancellationToken.None)).ConfigureAwait(false);
        var active=await fixture.Reader.ActivateReconciledAsync(catalog,stage,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(prepared.Id,active.StageId);Assert.AreEqual(0L,active.Index.Assets);Assert.IsNull(active.Catalog);
        Assert.AreEqual(active,await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(calls,fixture.Data.Setup.Accounts.Readers.DocumentCalls);
        Assert.AreEqual(bodies,fixture.Http.BodyReads);Assert.AreEqual(metadata,fixture.Http.MetadataReads);Assert.AreEqual(0,fixture.Http.Puts);
        Assert.AreEqual(catalog.Checkpoint,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).ReadCurrentInventory(),CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentReconciledCatalogUsesTheOriginalPublishedRootPlanAndARealSealedCanonicalFileWithoutCompletingItsGeneration()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        // This existing transport fixture declares a SYNTHETIC publication snapshot
        // descriptor. The private canonical stage below is real; generation/snapshot
        // completion or retirement is deliberately not claimed by this test.
        var publication=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var plan=await PrepareTransitionAsync(fixture,publication.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);
        var captured=await ReadCompletionCaptureAsync(fixture.Data,plan.GenerationId!.Value).ConfigureAwait(false);
        var trace=InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareReconciledGraphAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;trace.Clear();
        var catalog=await fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("root",trace[^1]);Assert.AreEqual(1L,catalog.Graph.Assets);var reads=fixture.Http.BodyReads;
        var active=await fixture.Reader.ActivateReconciledAsync(catalog,stage,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(publication.Reference,active.Catalog);Assert.AreEqual(plan.Sequence,active.RootSequence);Assert.AreEqual(1L,active.Index.Assets);
        Assert.AreEqual(1,(await stage.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Count);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(1,fixture.Http.Puts);Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        Assert.AreEqual(captured,await ReadCompletionCaptureAsync(fixture.Data,plan.GenerationId.Value).ConfigureAwait(false));
        Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).PublishedHead(),CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentReconciledCatalogRollsBackTheRootTerminalAndPointerTogetherAndRecoversTheExactCommittedReceipt()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);_=await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareReconciledGraphAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        var catalog=await fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None).ConfigureAwait(false);
        Task<CurrentRootSigningDisposition> Disposition()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var current=new LocalCurrentControlObservations(database,fixture.Data.Budget).Current()!;
            return new CurrentControlTransitionJournal(database,fixture.Data.Budget).CaptureSigningInventory(current).Roots.Single(root=>root.Intent.Id==plan.Id).Disposition;
        },CancellationToken.None);
        Assert.AreEqual(CurrentRootSigningDisposition.Pending,await Disposition().ConfigureAwait(false));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {CurrentStageSql(database,"CREATE TRIGGER fail_reconciled_activation BEFORE INSERT ON processor_meta WHEN NEW.key='current_catalog_active_v1:sha256' BEGIN SELECT RAISE(ABORT,'reconciled final hash'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>fixture.Reader.ActivateReconciledAsync(catalog,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(CurrentRootSigningDisposition.Pending,await Disposition().ConfigureAwait(false));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_reconciled_activation");return true;},CancellationToken.None).ConfigureAwait(false);
        var active=await fixture.Reader.ActivateReconciledAsync(catalog,stage,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(prepared.Id,active.StageId);Assert.AreEqual(CurrentRootSigningDisposition.Activated,await Disposition().ConfigureAwait(false));
        // Exact local-ack recovery is a historical read, not permission to relabel
        // the pre-activation signing-inventory digest after its own terminal changes.
        Assert.AreEqual(active,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(catalog.Checkpoint,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).ReadCurrentInventory(),CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentReconciledCatalogReaderOrStageExitJoinsTheUncooperativeInventoryBeforeReleasingItsRealFile(bool readerExit)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareReconciledGraphAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Data.Setup.Accounts.Readers.MetadataRead=async(_,candidate,_)=>
        {entered.TrySetResult();await release.Task.ConfigureAwait(false);return fixture.Data.Cloud[candidate.FileId];};
        var reading=fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=readerExit ? fixture.Reader.DisposeAsync().AsTask() : stage.DisposeAsync().AsTask();
        try {Assert.IsFalse(reading.IsCompleted);Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(stage.ExitConfirmed);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(readerExit,fixture.Reader.ExitConfirmed);Assert.AreEqual(!readerExit,stage.ExitConfirmed);
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).ReadCurrentInventory(),CancellationToken.None).ConfigureAwait(false));
        await stage.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(stage.ExitConfirmed);
        await pool.RetireAsync(prepared.Id,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);
    }

    [TestMethod]
    [DataRow("new-scan")]
    [DataRow("unread-candidate")]
    [DataRow("new-outbox")]
    public async Task CurrentReconciledCatalogChangedInventoryClosesAdmissionAndPreservesNewWork(string mutation)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareReconciledGraphAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        var catalog=await fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None).ConfigureAwait(false);
        var work=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var repository=new WorkspaceObservationRepository(database,fixture.Data.Budget);
            switch(mutation)
            {
                case "new-scan": repository.BeginScan(catalog.Checkpoint.Context);return Guid.Empty;
                case "unread-candidate": repository.RememberCandidates(repository.LastAttempt()!,[new("newly-unread",9,12)]);return Guid.Empty;
                case "new-outbox":
                    var outbox=new WorkspacePublicationRepository(database,fixture.Data.Budget);var old=outbox.Read(outbox.Identities()[0])!;
                    return outbox.Prepare(Guid.NewGuid(),old.Scope,old.Role,old.MimeType,old.Payload.Span).Id;
                default: throw new InvalidOperationException("Unsupported test mutation.");
            }
        },CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;var metadata=fixture.Http.MetadataReads;var calls=fixture.Data.Setup.Accounts.Readers.DocumentCalls;
        if(mutation=="new-outbox") await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>fixture.Reader.ActivateReconciledAsync(catalog,stage,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>fixture.Reader.ActivateReconciledAsync(catalog,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(metadata,fixture.Http.MetadataReads);
        Assert.AreEqual(calls,fixture.Data.Setup.Accounts.Readers.DocumentCalls);Assert.AreEqual(prepared.Id,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Id);
        if(work!=Guid.Empty) Assert.IsNotNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,fixture.Data.Budget).Read(work),CancellationToken.None).ConfigureAwait(false));
        if(mutation=="unread-candidate") Assert.IsTrue(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).KnownIdentities(catalog.Checkpoint.Context).Contains("newly-unread"),CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentReconciledCatalogUnsealedFileIsRejectedBeforeStartingAnotherInventory()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareReconciledGraphAsync(fixture,pool,seal:false).ConfigureAwait(false);await using var stage=prepared.Stage;
        var before=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).LastAttempt(),CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;var calls=fixture.Data.Setup.Accounts.Readers.DocumentCalls;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(calls,fixture.Data.Setup.Accounts.Readers.DocumentCalls);
        Assert.AreEqual(before,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).LastAttempt(),CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentReconciledCatalogNewActualRootNonceRejectsTheOldCombinedAdmission()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareReconciledGraphAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        var catalog=await fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ActivateReconciledAsync(catalog,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    private static async Task<(Guid Id,LocalCurrentSqliteStage Stage,LocalCurrentControlGraph Graph)> PrepareReconciledGraphAsync(CurrentReaderFixture fixture,LocalCurrentStagePool pool,bool seal=true)
    {
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var id=Guid.NewGuid();var stage=await pool.OpenAsync(capture,id,CancellationToken.None).ConfigureAwait(false);
        try
        {
            var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None).ConfigureAwait(false);
            if(seal) await stage.SealAsync(graph,CancellationToken.None).ConfigureAwait(false);return(id,stage,graph);
        }
        catch {await stage.DisposeAsync().ConfigureAwait(false);throw;}
    }
}
