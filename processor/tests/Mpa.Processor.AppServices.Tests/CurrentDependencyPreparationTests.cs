using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentDependencyOwnsQueuedBytesAndResumesTheSameOriginalRecordAfterReaderRecreation()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var snapshot=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        var writes=(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeBody=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        var mutable=owned.Payload.ToArray();var preparing=fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,"coverage-root","application/json",mutable,CancellationToken.None);
        WorkspacePublicationRecord first;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);mutable[^1]^=1;release.TrySetResult();
            first=await preparing.ConfigureAwait(false);
        }
        finally {release.TrySetResult();fixture.Http.BeforeBody=null;}
        Assert.AreEqual(owned.Basis.Publication.Capture.Scope,first.Scope);Assert.IsNull(first.FileId);Assert.IsNull(first.VerifiedVersion);
        CollectionAssert.AreEqual(owned.Payload,first.Payload.ToArray());
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreNotEqual(before.Publications,after.Publications);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Head,after.Head);
        Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Human,after.Human);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var checkpoint=new WorkspaceObservationRepository(database,fixture.Data.Budget).ReadCurrentInventory()!;
            var actual=new WorkspacePublicationRepository(database,fixture.Data.Budget).Inventory(checkpoint.Context);
            Assert.AreNotEqual(checkpoint.PublicationSha256,actual.Sha256); // The old checkpoint is spent, never rewritten into a new grant.
            Assert.AreEqual(1,actual.Objects.Count(value=>value.Id==first.Id));return true;
        },CancellationToken.None).ConfigureAwait(false);
        await using var recreated=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var retry=await recreated.PrepareCurrentDependencyAsync(owned.Generation,"coverage-root","application/json",owned.Payload,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Id,retry.Id);Assert.AreEqual(first.Scope,retry.Scope);Assert.AreEqual(first.Sha256,retry.Sha256);
        CollectionAssert.AreEqual(first.Payload.ToArray(),retry.Payload.ToArray());Assert.IsNull(retry.FileId);Assert.IsNull(retry.VerifiedVersion);
        CollectionAssert.AreEqual(snapshot,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));
        Assert.AreEqual(writes,(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("current-dependency-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentDependencyRejectsInvalidInputsAndPreCancellationBeforeAnyProviderIo()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts,fixture.Data.Creates,fixture.Data.Allocations);var id=Guid.NewGuid();
        Assert.ThrowsExactly<ArgumentException>(()=>fixture.Reader.PrepareCurrentDependencyAsync(Guid.Empty,"coverage-root","application/json","{}"u8.ToArray(),CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(()=>fixture.Reader.PrepareCurrentDependencyAsync(id,"catalog-commit","application/json","{}"u8.ToArray(),CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(()=>fixture.Reader.PrepareCurrentDependencyAsync(id,"facts-chunk","application/json","{}"u8.ToArray(),CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(()=>fixture.Reader.PrepareCurrentDependencyAsync(id,"coverage-root","application/gzip","{}"u8.ToArray(),CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(()=>fixture.Reader.PrepareCurrentDependencyAsync(id,"coverage-root","application/json",Array.Empty<byte>(),CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(()=>fixture.Reader.PrepareCurrentDependencyAsync(id,"coverage-root","application/json",new byte[ProtocolDocumentReader.MaximumDocumentBytes+1],CancellationToken.None));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>fixture.Reader.PrepareCurrentDependencyAsync(id,"coverage-root","application/json","{\"z\":1,\"a\":2}"u8.ToArray(),CancellationToken.None));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>fixture.Reader.PrepareCurrentDependencyAsync(id,"coverage-root","application/json","{}"u8.ToArray(),canceled.Token));
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts,fixture.Data.Creates,fixture.Data.Allocations));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CurrentDependencyRollsBackFailedInsertHumanDriftAndUnexpectedAllocationInItsActualTransaction(int mode)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,mode switch
            {
                0=>"CREATE TRIGGER reject_current_dependency BEFORE INSERT ON workspace_publication BEGIN SELECT RAISE(ABORT,'dependency insertion fixture'); END",
                1=>"CREATE TRIGGER reject_current_dependency AFTER INSERT ON workspace_publication BEGIN UPDATE library_configuration SET revision=revision+1 WHERE id=1; END",
                2=>"CREATE TRIGGER reject_current_dependency AFTER INSERT ON workspace_publication BEGIN UPDATE workspace_publication SET file_id='unexpected-local-allocation' WHERE id=NEW.id; END",
                _=>throw new ArgumentOutOfRangeException(nameof(mode))
            });return true;
        },CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var snapshot=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        var writes=(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts);
        try
        {
            Task<WorkspacePublicationRecord> Run()=>fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,"coverage-root","application/json",owned.Payload,CancellationToken.None);
            if(mode==0) await Assert.ThrowsExactlyAsync<SqliteException>(Run).ConfigureAwait(false);
            else if(mode==1) await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(Run).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(Run).ConfigureAwait(false);
            var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
            Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Head,after.Head);
            Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Human,after.Human);
            CollectionAssert.AreEqual(snapshot,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
        finally {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER reject_current_dependency");return true;},CancellationToken.None).ConfigureAwait(false);}
        var recovered=await fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,"coverage-root","application/json",owned.Payload,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Basis.Publication.Capture.Scope,recovered.Scope);Assert.IsNull(recovered.FileId);Assert.IsNull(recovered.VerifiedVersion);
        CollectionAssert.AreEqual(owned.Payload,recovered.Payload.ToArray());Assert.AreEqual(writes,(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDependencyRejectsChangedHumanSourcesAndActualPublishedRevocationWithoutRetargetingTheSnapshot(bool revoke)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        if(revoke) _=await RevokeForOriginalAuthorityTestAsync(fixture).ConfigureAwait(false);
        else await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;},CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        Task<WorkspacePublicationRecord> Run()=>fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,"coverage-root","application/json",owned.Payload,CancellationToken.None);
        if(revoke) await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(Run).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(Run).ConfigureAwait(false);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);
        Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(before.Captures,after.Captures);
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentDependencyReaderExitJoinsItsActualEnteredRootReadAndRejectsLatePreparation()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeBody=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        var running=fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,"coverage-root","application/json",owned.Payload,CancellationToken.None);Task? stopping=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);stopping=fixture.Reader.DisposeAsync().AsTask();
            Assert.IsFalse(running.IsCompleted);Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(fixture.Reader.ExitConfirmed);
        }
        finally {release.TrySetResult();fixture.Http.BeforeBody=null;}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);Assert.IsNotNull(stopping);await stopping.ConfigureAwait(false);
        Assert.IsTrue(fixture.Reader.ExitConfirmed);var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Captures,after.Captures);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentDependencyLowStorageRollsBackOnlyItsNewRecordAndKeepsTheOriginalPreparedSnapshot()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var fenced=new LocalCurrentControlObservations(database,fixture.Data.Budget).Current()!;
            Assert.AreEqual(inventory.Checkpoint.RootAttemptId,fenced.Id);
            var signing=new CurrentControlTransitionJournal(database,fixture.Data.Budget).CaptureSigningInventory(fenced);
            var publications=new WorkspacePublicationRepository(database,fixture.Data.Budget).Inventory(inventory.Checkpoint.Context);
            var low=new StorageBudgetManager(new RetirementNoSpace());
            Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>new CatalogGenerationCaptureRepository(database,low)
                .PrepareCurrentDependency(owned.Generation,CatalogDependencyPayload.Capture("coverage-root","application/json",owned.Payload),
                    inventory.Checkpoint,signing,fenced,publications));
            Assert.AreEqual(0L,low.Status().ReservedBytes);return true;
        },CancellationToken.None).ConfigureAwait(false);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);
        Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Captures,after.Captures);Assert.IsTrue(File.Exists(owned.Path));
        _=await fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,"coverage-root","application/json",owned.Payload,CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CurrentDependencyCannotPrepareNewFactsInsideAnAdvancedSignedGeneration()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var scope=owned.Capture.Scope;var bytes=new CatalogCoverageDocumentWriter(new(scope.AccountId,scope.FolderId)).EncodeRoot(scope.LibraryId,scope.ConfigurationRevision,1,0,[]);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>fixture.Reader.PrepareCurrentDependencyAsync(scope.GenerationId,
            "coverage-root","application/json",bytes,CancellationToken.None)).ConfigureAwait(false);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);
        Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Captures,after.Captures);Assert.IsTrue(File.Exists(owned.SnapshotPath));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    private sealed class CurrentDependencyFixture(CurrentReaderFixture fixture,CurrentPreparedCatalogBasis basis,byte[] payload,string path) : IAsyncDisposable
    {
        internal CurrentReaderFixture Fixture {get;}=fixture;
        internal CurrentPreparedCatalogBasis Basis {get;}=basis;
        internal byte[] Payload {get;}=payload;
        internal string Path {get;}=path;
        internal Guid Generation=>Basis.Publication.Capture.Scope.GenerationId;
        internal static async Task<CurrentDependencyFixture> CreateAsync()
        {
            var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
            try
            {
                InstallCurrentInventoryTransport(fixture);await AddCurrentSnapshotFactAsync(fixture,"current-dependency-original").ConfigureAwait(false);
                CurrentPreparedCatalogBasis basis;await using(var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false))
                    basis=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
                var scope=basis.Publication.Capture.Scope;
                var payload=new CatalogCoverageDocumentWriter(new(scope.AccountId,scope.FolderId)).EncodeRoot(scope.LibraryId,scope.ConfigurationRevision,1,0,[]);
                var path=await CurrentSnapshotPathAsync(fixture,basis.Publication.Capture).ConfigureAwait(false);return new(fixture,basis,payload,path);
            }
            catch {await fixture.DisposeAsync().ConfigureAwait(false);throw;}
        }
        public ValueTask DisposeAsync()=>Fixture.DisposeAsync();
    }
}
