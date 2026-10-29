using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentPreparedGraphProducerSealsExactFrozenDependenciesAndReopensSamePlanWithoutSigningOrSelecting(bool selected)
    {
        IAsyncDisposable? lifetime=null;
        try
        {
            CurrentReaderFixture fixture;CurrentPreparedCatalogBasis basis;PublishedCatalogHead? head=null;
            if(selected)
            {
                var prior=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);lifetime=prior;fixture=prior.Fixture;
                await using(var finish=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
                    head=await finish.FinishAsync(prior.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
                await using var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);basis=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else {var fresh=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);lifetime=fresh;fixture=fresh.Fixture;basis=fresh.Basis;}
            InstallPreparedGraphCreates(fixture,basis.Publication.Capture.Scope);
            var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var path=await CurrentSnapshotPathAsync(fixture,basis.Publication.Capture).ConfigureAwait(false);
            var original=await File.ReadAllBytesAsync(path).ConfigureAwait(false);var creates=fixture.Data.Creates;var allocations=fixture.Data.Allocations;var puts=fixture.Http.Puts;
            CapturedCatalogPublication first;
            await using(var producer=await NewCurrentPreparedGraphOwnerAsync(fixture).ConfigureAwait(false))
            {
                first=await producer.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("dependencies-ready",first.Capture.Phase);Assert.AreEqual(basis.Publication.Basis,first.Basis);Assert.IsNotNull(first.Plan);
                Assert.AreEqual(basis.Publication.Capture with {Phase="dependencies-ready"},first.Capture);
                await producer.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(producer.ExitConfirmed);Assert.IsNull(producer.Failure);
            }
            Assert.AreEqual(creates+4,fixture.Data.Creates);Assert.AreEqual(allocations+4,fixture.Data.Allocations);
            await using(var resumed=await NewCurrentPreparedGraphOwnerAsync(fixture).ConfigureAwait(false))
            {
                Assert.AreEqual(first,await resumed.PrepareAsync(CancellationToken.None).ConfigureAwait(false));
                await resumed.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(resumed.ExitConfirmed);
            }
            Assert.AreEqual(creates+4,fixture.Data.Creates);Assert.AreEqual(allocations+4,fixture.Data.Allocations);Assert.AreEqual(puts,fixture.Http.Puts);
            var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(before.Head,after.Head);
            Assert.AreEqual(head,await LineageHeadAsync(fixture).ConfigureAwait(false));
            Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Data.Budget).Read(first.Capture.CommitIntentId),CancellationToken.None).ConfigureAwait(false));
            CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
            Assert.IsFalse(fixture.Http.BodyFiles.Contains(selected?"read-only-original":"current-dependency-original"));
        }
        finally {if(lifetime is not null) await lifetime.DisposeAsync().ConfigureAwait(false);}
    }

    [TestMethod]
    public async Task CurrentPreparedGraphProducerKeepsItsFrozenFactsWhenNewDiscoveryArrivesDuringActualUpload()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        var original=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;var added=false;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,payload,hash,token)=>
        {
            if(!added) {added=true;await AddCurrentSnapshotFactAsync(fixture,"new-after-original-freeze").ConfigureAwait(false);}
            return await create(folder,file,mime,payload,hash,token).ConfigureAwait(false);
        };
        await using var producer=await NewCurrentPreparedGraphOwnerAsync(fixture).ConfigureAwait(false);
        var result=await producer.PrepareAsync(CancellationToken.None).ConfigureAwait(false);Assert.IsTrue(added);
        Assert.AreEqual(owned.Basis.Publication.Capture.SnapshotId,result.Capture.SnapshotId);Assert.AreEqual(owned.Basis.Publication.Basis,result.Basis);Assert.AreEqual(1L,result.Basis.Snapshot.Assets);
        CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CurrentPreparedGraphRejectsStoredPlansWithEqualCountChangedFactsMissingFactsOrChangedCoverage(int mode)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        var bad=await BuildPreparedGraphForTestAsync(owned,mode).ConfigureAwait(false);
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).SealDependencies(owned.Generation,bad),CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);var writes=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        await using var producer=await NewCurrentPreparedGraphOwnerAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>producer.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Publications,after.Publications);
        Assert.AreEqual(retained,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Publication(owned.Generation),CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CurrentPreparedGraphSealRollsBackActualSqlHumanAndOutboxDriftThenRetriesOriginalVerifiedBytes(int mode)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        _=await BuildPreparedGraphForTestAsync(owned,-1).ConfigureAwait(false);var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        var writes=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,mode switch
            {
                0=>"CREATE TRIGGER fail_current_graph BEFORE UPDATE OF plan ON catalog_generation_publication BEGIN SELECT RAISE(ABORT,'current graph fixture'); END",
                1=>"CREATE TRIGGER fail_current_graph AFTER UPDATE OF plan ON catalog_generation_publication BEGIN UPDATE library_configuration SET revision=revision+1 WHERE id=1; END",
                2=>"CREATE TRIGGER fail_current_graph AFTER UPDATE OF plan ON catalog_generation_publication BEGIN UPDATE workspace_publication SET verified_version=999 WHERE role='facts-chunk'; END",
                _=>throw new ArgumentOutOfRangeException(nameof(mode))
            });return true;
        },CancellationToken.None).ConfigureAwait(false);
        await using var producer=await NewCurrentPreparedGraphOwnerAsync(fixture).ConfigureAwait(false);
        try
        {
            if(mode==0) await Assert.ThrowsExactlyAsync<SqliteException>(()=>producer.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            else if(mode==1) await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>producer.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>producer.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);
            CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
        finally {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_current_graph");return true;},CancellationToken.None).ConfigureAwait(false);}
        var result=await producer.PrepareAsync(CancellationToken.None).ConfigureAwait(false);Assert.AreEqual("dependencies-ready",result.Capture.Phase);Assert.AreEqual(owned.Basis.Publication.Basis,result.Basis);
        Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentPreparedGraphCancelOrExitJoinsActualUncooperativeGraphReadAndRetainsFrozenFile(bool exit)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        var plan=await BuildPreparedGraphForTestAsync(owned,-1).ConfigureAwait(false);
        var sealedPlan=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).SealDependencies(owned.Generation,plan),CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);var read=fixture.Data.Setup.Accounts.Readers.WorkspaceRead!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Data.Setup.Accounts.Readers.WorkspaceRead=async(folder,file,length,hash,token)=>
        {token.ThrowIfCancellationRequested();entered.TrySetResult();await release.Task.ConfigureAwait(false);return await read(folder,file,length,hash,CancellationToken.None).ConfigureAwait(false);};
        using var caller=new CancellationTokenSource();await using var producer=await NewCurrentPreparedGraphOwnerAsync(fixture).ConfigureAwait(false);
        var running=producer.PrepareAsync(caller.Token);var stopping=Task.CompletedTask;var disposals=fixture.Data.Setup.Accounts.Readers.DisposalAttempts;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=producer.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(running.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(producer.ExitConfirmed);
            Assert.AreEqual(disposals,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);Assert.IsTrue(fixture.Data.Budget.Status().ReservedBytes>0);Assert.IsTrue(File.Exists(owned.Path));
        }
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(exit,producer.ExitConfirmed);Assert.AreEqual(sealedPlan,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Publication(owned.Generation),CancellationToken.None).ConfigureAwait(false));
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Head,after.Head);
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    public async Task CurrentPreparedGraphConstructorRejectsForeignBudgetBeforeAnyNetworkOrNativeCapture()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        var other=new StorageBudgetManager(new ProcessorStateStorageReader(input.StateDirectory));
        Assert.ThrowsExactly<InvalidOperationException>(()=>new CurrentCatalogDependencyPreparationService(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,other,fixture.Reader));
        Assert.AreEqual(0,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(0L,other.Status().ReservedBytes);
    }

    private static async Task<CurrentCatalogDependencyPreparationService> NewCurrentPreparedGraphOwnerAsync(CurrentReaderFixture fixture)
    {
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        return new(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader);
    }
    private static void InstallPreparedGraphCreates(CurrentReaderFixture fixture,WorkspacePublicationScope scope)
    {
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=(folder,file,mime,payload,hash,token)=>
        {
            token.ThrowIfCancellationRequested();Assert.AreEqual(scope.FolderId,folder);Assert.AreEqual(CurrentHash(payload.ToArray()),hash);fixture.Data.Creates++;
            if(!fixture.Data.Cloud.TryGetValue(file,out var actual)) {actual=new DriveWorkspaceObject(scope.AccountId,file,folder,7,payload.ToArray()){MimeType=mime};fixture.Data.Cloud.Add(file,actual);}
            CollectionAssert.AreEqual(payload.ToArray(),actual.Bytes.ToArray());fixture.Data.MetadataMimeTypes[file]=mime;return Task.FromResult(actual);
        };
    }
    private static async Task<CapturedCatalogCommitPlan> BuildPreparedGraphForTestAsync(CurrentDependencyFixture owned,int mode)
    {
        var fixture=owned.Fixture;var publication=owned.Basis.Publication;var scope=publication.Capture.Scope;var workspace=new CatalogWorkspace(scope.AccountId,scope.FolderId);
        using var store=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget),CancellationToken.None).ConfigureAwait(false);
        using var frozen=store.Open(publication.Capture,CancellationToken.None);
        await using var sink=new CurrentCatalogGenerationObjectSink(owned.Generation,fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader);
        var good=await new CatalogGenerationDependencyBuilder(workspace,sink,WorkspaceObservationRepository.MaximumDocuments).BuildAsync(frozen,owned.Basis.Sources,publication.Basis.Parent,CancellationToken.None).ConfigureAwait(false);
        var index=good.RootIndex;var coverage=good.SourceCoverage;
        if(mode==0)
        {
            var asset=frozen.ReadPage("").Assets.Single() with {Name="same-count-substituted-name.jpg"};var prefix=asset.Id.ToString("N")[..2];var expanded=new DriveCatalogFacts(scope.LibraryId).Encode(prefix,[asset]);
            byte[] compressed;using(var buffer=new MemoryStream()) {using(var gzip=new GZipStream(buffer,CompressionLevel.Optimal,true)) gzip.Write(expanded);compressed=buffer.ToArray();}
            var reference=await sink.StoreAsync("facts-chunk","application/gzip",compressed,CancellationToken.None).ConfigureAwait(false);
            index=await sink.StoreAsync("facts-index","application/json",new CatalogIndexDocumentWriter(workspace).Encode("",[new(prefix,reference,new(reference,expanded.Length,CurrentHash(expanded)))]),CancellationToken.None).ConfigureAwait(false);
        }
        else if(mode==1) index=await sink.StoreAsync("facts-index","application/json",new CatalogIndexDocumentWriter(workspace).Encode("",[]),CancellationToken.None).ConfigureAwait(false);
        else if(mode==2)
        {
            var writer=new CatalogCoverageDocumentWriter(workspace);var discovery=frozen.DiscoveryRevision+1;var sources=frozen.Coverage;
            var bytes=writer.EncodePage(scope.LibraryId,frozen.ConfigurationRevision,discovery,sources);
            var page=await sink.StoreAsync("coverage-page","application/json",bytes,CancellationToken.None).ConfigureAwait(false);
            coverage=await sink.StoreAsync("coverage-root","application/json",writer.EncodeRoot(scope.LibraryId,frozen.ConfigurationRevision,discovery,sources.Count,[new(sources[0].Id,sources[^1].Id,sources.Count,page)]),CancellationToken.None).ConfigureAwait(false);
        }
        return new(publication.Basis,index,coverage);
    }
}
