using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentSqliteStageSealsOnlyTheSelectedCompleteGraphBeforeItsFinalRootFence()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selected=await AddCatalogHeadAsync(fixture.Data,"canonical-selected",includeAsset:true).ConfigureAwait(false);
        var orphan=await AddCatalogHeadAsync(fixture.Data,"canonical-orphan",selected.Reference,includeAsset:true).ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,selected.Reference).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        await using var stage=await LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,capture,Guid.NewGuid(),CancellationToken.None).ConfigureAwait(false);
        var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None).ConfigureAwait(false);
        var sealedStage=await stage.SealAsync(graph,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(1L,sealedStage.Assets);
        var page=await stage.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(1,page.Assets.Count);Assert.IsFalse(page.HasMore);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains(orphan.Reference.FileId));Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        var claim=(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single();Assert.AreEqual("sealed",claim.Phase);Assert.AreEqual(capture.Attempt.Id,claim.PreparedAttempt);Assert.AreEqual(sealedStage,claim.Descriptor);
        var fence=await fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual("fenced",fence.Current.Attempt.Phase);
        Assert.AreEqual(page.Assets[0].Id,(await stage.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets[0].Id);
        // A sealed projection remains readable, but its captured nonce is no longer fresh authority.
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>stage.SealAsync(graph,CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentSqliteStageFreshSameRootResumeRetainsOriginalNonceAndExactFileBytes()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selected=await AddCatalogHeadAsync(fixture.Data,"canonical-resume",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,selected.Reference).ConfigureAwait(false);
        var first=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var policies=await fixture.Reader.ReadSelectedPoliciesAsync(first,CancellationToken.None).ConfigureAwait(false);
        var id=Guid.NewGuid();CurrentCatalogStageDescriptor descriptor;
        await using(var stage=await LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,first,id,CancellationToken.None).ConfigureAwait(false))
        {var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None).ConfigureAwait(false);descriptor=await stage.SealAsync(graph,CancellationToken.None).ConfigureAwait(false);}
        var path=await StagePathAsync(fixture,id).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var second=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);Assert.AreNotEqual(first.Attempt.Id,second.Attempt.Id);
        var freshPolicies=await fixture.Reader.ReadSelectedPoliciesAsync(second,CancellationToken.None).ConfigureAwait(false);
        await using(var stage=await LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,second,id,CancellationToken.None).ConfigureAwait(false))
        {
            var graph=await fixture.Reader.ReadSelectedGraphAsync(freshPolicies,stage,CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(descriptor,await stage.SealAsync(graph,CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(first.Attempt.Id,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().PreparedAttempt);
            await fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None).ConfigureAwait(false);
        }
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(path).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalCurrentSqliteStageRejectsOtherStageWitnessAndOldNonceWithoutChangingTheClaim()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var other=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);
        var id=Guid.NewGuid();await using var stage=await LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,capture,id,CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>stage.SealAsync(other,CancellationToken.None)).ConfigureAwait(false);
        var own=await fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None).ConfigureAwait(false);
        var next=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>stage.SealAsync(own,CancellationToken.None)).ConfigureAwait(false);
        var claim=(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single();Assert.AreEqual("preparing",claim.Phase);Assert.IsNull(claim.Descriptor);Assert.AreEqual(capture.Attempt.Id,claim.PreparedAttempt);Assert.AreNotEqual(next.Attempt.Id,claim.PreparedAttempt);
    }

    [TestMethod]
    public async Task LocalCurrentSqliteStageCannotRetargetItsIdentityToAnotherValidRootHead()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var first=await AddCatalogHeadAsync(fixture.Data,"canonical-first",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,first.Reference).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var id=Guid.NewGuid();
        await using(var stage=await LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,capture,id,CancellationToken.None).ConfigureAwait(false)) { }
        var original=(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single();var second=await AddCatalogHeadAsync(fixture.Data,"canonical-second",first.Reference,includeAsset:true).ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,second.Reference).ConfigureAwait(false);var next=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,next,id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(original,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single());
    }

    [TestMethod]
    public async Task LocalCurrentSqliteStageNativeWritesDoNotOccupyTheStateQueueAndExitJoinsBeforeReopening()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selected=await AddCatalogHeadAsync(fixture.Data,"canonical-joined",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,selected.Reference).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        using var space=new CurrentStageBlockedSpace();var budget=new StorageBudgetManager(space);var id=Guid.NewGuid();
        await using var stage=await LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,budget,capture,id,CancellationToken.None).ConfigureAwait(false);
        CatalogFactsLeaf? leaf=null;
        await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage {Hook=value=>{leaf=value;return Task.CompletedTask;}},CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(leaf);
        // Bound the rendezvous around the native write itself. Signed ancestry and
        // transport setup have already completed; they are not part of this wait.
        space.Armed=true;var reading=stage.AddAsync(leaf,CancellationToken.None);
        try
        {
            await space.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.AreEqual(id,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Id); // The state queue is available while native storage is blocked.
            var closing=stage.DisposeAsync().AsTask();Assert.IsFalse(closing.IsCompleted);Assert.IsFalse(stage.ExitConfirmed);
            space.Release.Set();await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await closing.ConfigureAwait(false);Assert.IsTrue(stage.ExitConfirmed);
        }
        finally {space.Release.Set();}
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>stage.ReadPageAsync(null,60,CancellationToken.None)).ConfigureAwait(false);
        // The reader and its borrowed account/state/key owners remain usable.
        var fresh=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await using var reopened=await LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fresh,id,CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentSqliteStageClaimLimitAndPairedHistoryFailClosedWithoutRemovingFiles()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var claims=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new CurrentCatalogStageJournal(database,fixture.Data.Budget);journal.Begin(capture.Attempt,Guid.NewGuid());var second=Guid.NewGuid();
            CurrentStageSql(database,"CREATE TRIGGER reject_stage_hash BEFORE INSERT ON processor_meta WHEN NEW.key='current_catalog_stage_v1:"+second.ToString("N")+":sha256' BEGIN SELECT RAISE(ABORT,'injected paired-write failure'); END");
            Assert.ThrowsExactly<SqliteException>(()=>journal.Begin(capture.Attempt,second));Assert.AreEqual(1,journal.ReadAll().Count);
            CurrentStageSql(database,"DROP TRIGGER reject_stage_hash");var space=new CurrentAllocationSpace {Available=1};
            Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>new CurrentCatalogStageJournal(database,new(space)).Begin(capture.Attempt,second));Assert.AreEqual(1,journal.ReadAll().Count);
            journal.Begin(capture.Attempt,second);
            Assert.ThrowsExactly<CatalogPreparationCapacityException>(()=>journal.Begin(capture.Attempt,Guid.NewGuid()));return journal.ReadAll();
        },CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2,claims.Count);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=Path.Combine(database.StateDirectory,"processor.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());connection.Open();
            using var command=connection.CreateCommand();command.CommandText="DELETE FROM processor_meta WHERE key=$key";command.Parameters.AddWithValue("$key","current_catalog_stage_v1:"+claims[0].Id.ToString("N")+":sha256");Assert.AreEqual(1,command.ExecuteNonQuery());
            Assert.ThrowsExactly<InvalidDataException>(()=>new CurrentCatalogStageJournal(database,fixture.Data.Budget).ReadAll());return true;
        },CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentSqliteStageStorageAccountingIncludesOnlyOwnedFilesAndRetirementPreservesOriginals()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);var id=Guid.NewGuid();
        var state=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        var original=Path.Combine(state.StateDirectory,"never-owned-original.jpg");await File.WriteAllBytesAsync(original,[1,2,3,4]).ConfigureAwait(false);
        var reader=new ProcessorStateStorageReader(state.StateDirectory);var before=reader.Read().OwnedCacheBytes;
        await using(var stage=await LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,capture,id,CancellationToken.None).ConfigureAwait(false))
        {
            var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(0L,(await stage.SealAsync(graph,CancellationToken.None).ConfigureAwait(false)).Assets);
            Assert.IsGreaterThan(before,reader.Read().OwnedCacheBytes);Assert.IsGreaterThan(4096L,CurrentCatalogStageArea.MeasureOwnedBytes(state.StateDirectory));
        }
        var retiring=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {var journal=new CurrentCatalogStageJournal(database,fixture.Data.Budget);return journal.BeginRetirement(journal.ReadAll().Single());},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<IOException>(()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new CurrentCatalogStageJournal(database,fixture.Data.Budget).CompleteRetirement(retiring);return true;},CancellationToken.None)).ConfigureAwait(false);
        using(var area=new CurrentCatalogStageArea(state.StateDirectory,state.InstallationId,[retiring],fixture.Data.Budget))
        {
            area.RemoveRetiring(retiring);
            await Assert.ThrowsExactlyAsync<IOException>(()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new CurrentCatalogStageJournal(database,fixture.Data.Budget).CompleteRetirement(retiring);return true;},CancellationToken.None)).ConfigureAwait(false);
        }
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new CurrentCatalogStageJournal(database,fixture.Data.Budget).CompleteRetirement(retiring);return true;},CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);Assert.IsFalse(File.Exists(await StagePathAsync(fixture,id).ConfigureAwait(false)));
        CollectionAssert.AreEqual(new byte[]{1,2,3,4},await File.ReadAllBytesAsync(original).ConfigureAwait(false));
    }

    private static Task<IReadOnlyList<CurrentCatalogStageClaim>> StageClaimsAsync(CurrentReaderFixture fixture)=>
        fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentCatalogStageJournal(database,fixture.Data.Budget).ReadAll(),CancellationToken.None);
    private static Task<string> StagePathAsync(CurrentReaderFixture fixture,Guid id)=>
        fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>Path.Combine(database.StateDirectory,"current-catalog-stages","stage-"+id.ToString("N")+".sqlite"),CancellationToken.None);
    private static void CurrentStageSql(ProcessorStateDatabase database,string sql)
    {
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=Path.Combine(database.StateDirectory,"processor.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());
        connection.Open();using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();
    }
    private sealed class CurrentStageBlockedSpace : IStorageSpaceReader,IDisposable
    {
        internal bool Armed {get;set;}
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release {get;}=new(false);
        public StorageSpaceSnapshot Read()
        {
            if(Armed) {Armed=false;Entered.TrySetResult();Release.Wait();}
            return new(100*StorageBudgetManager.Gibibyte,20*StorageBudgetManager.Gibibyte,0);
        }
        public void Dispose()=>Release.Dispose();
    }
}
