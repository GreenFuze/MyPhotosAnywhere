using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentFreshHeaderPreparesActualGenesisOrSelectedParentAndRetainsTheFirstCounterBodyAndSignature(bool selected)
    {
        IAsyncDisposable? lifetime=null;
        try
        {
            CurrentReaderFixture fixture;CurrentPreparedCatalogBasis basis;
            if(selected)
            {
                var prior=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);lifetime=prior;fixture=prior.Fixture;
                await using(var complete=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
                    _=await complete.FinishAsync(prior.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
                await using var preparation=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);
                basis=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var fresh=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);lifetime=fresh;fixture=fresh.Fixture;basis=fresh.Basis;
            }
            InstallPreparedGraphCreates(fixture,basis.Publication.Capture.Scope);
            var path=await CurrentSnapshotPathAsync(fixture,basis.Publication.Capture).ConfigureAwait(false);var original=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var creates=fixture.Data.Creates;var allocations=fixture.Data.Allocations;var puts=fixture.Http.Puts;
            WorkspacePublicationRecord first;
            await using(var owner=await NewCurrentFreshHeaderOwnerAsync(fixture).ConfigureAwait(false))
            {
                first=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
                await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);Assert.IsNull(owner.Failure);
            }
            var publication=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Publication(basis.Publication.Capture.Scope.GenerationId)!,CancellationToken.None).ConfigureAwait(false);
            var native=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Data.Budget).Read(publication.Capture.CommitIntentId)!,CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("commit-sealed",publication.Capture.Phase);Assert.AreEqual(basis.Publication.Basis,publication.Basis);Assert.IsNotNull(publication.Plan);
            Assert.AreEqual(publication.Capture.CommitIntentId,first.Id);Assert.AreEqual(publication.Capture.Scope,first.Scope);Assert.AreEqual("catalog-commit",first.Role);
            Assert.AreEqual("application/json",first.MimeType);Assert.IsNull(first.FileId);Assert.IsNull(first.VerifiedVersion);
            Assert.AreEqual(publication.Basis.ParentSequence+1,native.Sequence);Assert.AreEqual(first.Sha256,native.EnvelopeSha256);
            Assert.IsNotNull(native.Envelope);CollectionAssert.AreEqual(native.Envelope.Value.ToArray(),first.Payload.ToArray());
            Assert.AreEqual(creates+4,fixture.Data.Creates);Assert.AreEqual(allocations+4,fixture.Data.Allocations);Assert.AreEqual(puts,fixture.Http.Puts);
            var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(before.Head,after.Head);
            await using(var restarted=await NewCurrentFreshHeaderOwnerAsync(fixture).ConfigureAwait(false))
            {
                var again=await restarted.PrepareAsync(CancellationToken.None).ConfigureAwait(false);SameFreshHeader(first,again);
                await restarted.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(restarted.ExitConfirmed);Assert.IsNull(restarted.Failure);
            }
            var retried=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(after.Signing,retried.Signing);Assert.AreEqual(after.Publications,retried.Publications);Assert.AreEqual(after.Captures,retried.Captures);
            Assert.AreEqual(creates+4,fixture.Data.Creates);Assert.AreEqual(allocations+4,fixture.Data.Allocations);Assert.AreEqual(puts,fixture.Http.Puts);
            CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
            Assert.IsFalse(fixture.Http.BodyFiles.Contains(selected?"read-only-original":"current-dependency-original"));
        }
        finally {if(lifetime is not null) await lifetime.DisposeAsync().ConfigureAwait(false);}
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CurrentFreshHeaderReservationRollsBackSqlOtherActorHumanAndOutboxDriftWithoutLosingTheOriginalPlan(int mode)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        await using(var dependencies=await NewCurrentPreparedGraphOwnerAsync(fixture).ConfigureAwait(false))
            _=await dependencies.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var original=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        var writes=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);var id=owned.Basis.Publication.Capture.CommitIntentId.ToString("D");
        var mutation=mode switch
        {
            0=>"SELECT RAISE(ABORT,'actual fresh header reserve rollback');",
            1=>"UPDATE signing_actor SET sequence=sequence+1 WHERE role='controller';",
            2=>"UPDATE library_configuration SET revision=revision+1 WHERE id=1;",
            3=>"UPDATE workspace_publication SET verified_version=999 WHERE role='facts-chunk';",
            _=>throw new ArgumentOutOfRangeException(nameof(mode))
        };
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"CREATE TRIGGER fail_fresh_header AFTER INSERT ON signing_intent WHEN NEW.id='"+id+"' BEGIN "+mutation+" END");return true;},CancellationToken.None).ConfigureAwait(false);
        await using var owner=await NewCurrentFreshHeaderOwnerAsync(fixture).ConfigureAwait(false);
        try
        {
            if(mode==0) await Assert.ThrowsExactlyAsync<SqliteException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            else if(mode==1)
            {
                var failure=await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
                Assert.AreEqual("Signing actor/counter does not match its complete captured intent history.",failure.Message);
            }
            else if(mode==2) await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Head,after.Head);
            CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
        finally {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_fresh_header");return true;},CancellationToken.None).ConfigureAwait(false);}
        var result=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(owned.Basis.Publication.Capture.CommitIntentId,result.Id);
        Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CurrentFreshHeaderEnvelopeAndPendingWriteRollbackTogetherAndRetryKeepsTheAlreadyReservedCounter(int mode)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        await using(var dependencies=await NewCurrentPreparedGraphOwnerAsync(fixture).ConfigureAwait(false))
            _=await dependencies.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);var writes=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        var id=owned.Basis.Publication.Capture.CommitIntentId.ToString("D");
        var trigger=mode switch
        {
            0=>"CREATE TRIGGER fail_fresh_header BEFORE UPDATE OF envelope ON signing_intent WHEN NEW.id='"+id+"' BEGIN SELECT RAISE(ABORT,'actual envelope rollback'); END",
            1=>"CREATE TRIGGER fail_fresh_header AFTER UPDATE OF envelope ON signing_intent WHEN NEW.id='"+id+"' BEGIN UPDATE workspace_publication SET verified_version=999 WHERE role='facts-chunk'; END",
            2=>"CREATE TRIGGER fail_fresh_header AFTER INSERT ON workspace_publication WHEN NEW.role='catalog-commit' BEGIN UPDATE library_configuration SET revision=revision+1 WHERE id=1; END",
            3=>"CREATE TRIGGER fail_fresh_header BEFORE UPDATE OF phase ON catalog_generation_capture WHEN NEW.phase='commit-sealed' BEGIN SELECT RAISE(ABORT,'actual header phase rollback'); END",
            _=>throw new ArgumentOutOfRangeException(nameof(mode))
        };
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,trigger);return true;},CancellationToken.None).ConfigureAwait(false);
        await using var owner=await NewCurrentFreshHeaderOwnerAsync(fixture).ConfigureAwait(false);
        ProtocolSigningIntent reserved;
        try
        {
            if(mode is 0 or 3) await Assert.ThrowsExactlyAsync<SqliteException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            else if(mode==1) await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            reserved=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Data.Budget).Read(owned.Basis.Publication.Capture.CommitIntentId)!,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(reserved);Assert.IsNull(reserved.Envelope);Assert.IsNull(reserved.EnvelopeSha256);Assert.AreEqual(owned.Basis.Publication.Basis.ParentSequence+1,reserved.Sequence);
            var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Publication(owned.Generation)!,CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("dependencies-ready",retained.Capture.Phase);Assert.AreEqual(owned.Basis.Publication.Basis,retained.Basis);
            Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,fixture.Data.Budget).Read(reserved.Id),CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
        finally {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_fresh_header");return true;},CancellationToken.None).ConfigureAwait(false);}
        var result=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var completed=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Data.Budget).Read(result.Id)!,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(reserved.Id,completed.Id);Assert.AreEqual(reserved.Sequence,completed.Sequence);Assert.AreEqual(reserved.BodySha256,completed.BodySha256);CollectionAssert.AreEqual(reserved.Body.ToArray(),completed.Body.ToArray());
        Assert.IsNotNull(completed.Envelope);CollectionAssert.AreEqual(completed.Envelope.Value.ToArray(),result.Payload.ToArray());Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentFreshHeaderCancelOrExitJoinsActualDependencyReadBeforeNativeSigningOrBorrowedOwnerRetirement(bool exit)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        await using(var dependencies=await NewCurrentPreparedGraphOwnerAsync(fixture).ConfigureAwait(false))
            _=await dependencies.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        var read=fixture.Data.Setup.Accounts.Readers.WorkspaceRead!;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Data.Setup.Accounts.Readers.WorkspaceRead=async(folder,file,length,hash,token)=>
        {token.ThrowIfCancellationRequested();entered.TrySetResult();await release.Task.ConfigureAwait(false);return await read(folder,file,length,hash,CancellationToken.None).ConfigureAwait(false);};
        using var caller=new CancellationTokenSource();await using var owner=await NewCurrentFreshHeaderOwnerAsync(fixture).ConfigureAwait(false);
        var running=owner.PrepareAsync(caller.Token);var stopping=Task.CompletedTask;var disposals=fixture.Data.Setup.Accounts.Readers.DisposalAttempts;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=owner.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(running.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(owner.ExitConfirmed);
            Assert.AreEqual(disposals,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);Assert.IsTrue(fixture.Data.Budget.Status().ReservedBytes>0);Assert.IsTrue(File.Exists(owned.Path));
        }
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(exit,owner.ExitConfirmed);var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Head,after.Head);
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentFreshHeaderUsesActualAllocationUploadAndReceiptWithoutSelectingRootOrReplacingLostReplyIdentity(bool loseReply)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        WorkspacePublicationRecord pending;
        await using(var preparation=await NewCurrentFreshHeaderOwnerAsync(fixture).ConfigureAwait(false))
            pending=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        var cloud=fixture.Data.Cloud.Count;var puts=fixture.Http.Puts;var allocations=fixture.Data.Allocations;
        var p0=await fixture.Reader.InspectCatalogHeaderCloudAsync(await FreshLineageInventoryAsync(fixture).ConfigureAwait(false),owned.Generation,CancellationToken.None).ConfigureAwait(false);
        await using var allocator=new CurrentCatalogHeaderAllocationService(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader);
        var assigned=await allocator.AllocateAsync(p0,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pending.Id,assigned.Id);Assert.AreEqual(pending.Scope,assigned.Scope);Assert.IsNotNull(assigned.FileId);Assert.IsNull(assigned.VerifiedVersion);
        CollectionAssert.AreEqual(pending.Payload.ToArray(),assigned.Payload.ToArray());Assert.AreEqual(allocations+1,fixture.Data.Allocations);Assert.AreEqual(cloud,fixture.Data.Cloud.Count);
        await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>fixture.Reader.AssertCatalogHeaderCloudAdmissionAsync(p0,CancellationToken.None)).ConfigureAwait(false);
        var p1=await fixture.Reader.InspectCatalogHeaderCloudAsync(await FreshLineageInventoryAsync(fixture).ConfigureAwait(false),owned.Generation,CancellationToken.None).ConfigureAwait(false);
        var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;var lost=false;
        if(loseReply) fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,payload,hash,token)=>
        {
            var actual=await create(folder,file,mime,payload,hash,token).ConfigureAwait(false);
            if(!lost) {lost=true;throw new HttpRequestException("Actual fresh header reply lost after storage.");}
            return actual;
        };
        await using var uploader=new CurrentCatalogHeaderUploadService(fixture.Data.Setup.Accounts.Service,fixture.Reader);
        if(loseReply)
        {
            await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>uploader.UploadAsync(p1,CancellationToken.None)).ConfigureAwait(false);Assert.IsTrue(lost);
            var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,fixture.Data.Budget).Read(pending.Id)!,CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(assigned.FileId,retained.FileId);Assert.IsNull(retained.VerifiedVersion);CollectionAssert.AreEqual(pending.Payload.ToArray(),retained.Payload.ToArray());
            p1=await fixture.Reader.InspectCatalogHeaderCloudAsync(await FreshLineageInventoryAsync(fixture).ConfigureAwait(false),owned.Generation,CancellationToken.None).ConfigureAwait(false);
        }
        var saved=await uploader.UploadAsync(p1,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(assigned.Id,saved.Id);Assert.AreEqual(assigned.FileId,saved.FileId);Assert.IsNotNull(saved.VerifiedVersion);
        CollectionAssert.AreEqual(pending.Payload.ToArray(),saved.Payload.ToArray());Assert.AreEqual(allocations+1,fixture.Data.Allocations);Assert.AreEqual(cloud+1,fixture.Data.Cloud.Count);
        await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>fixture.Reader.AssertCatalogHeaderCloudAdmissionAsync(p1,CancellationToken.None)).ConfigureAwait(false);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(before.Head,after.Head);
        Assert.AreEqual(puts,fixture.Http.Puts);Assert.IsNull(uploader.Failure);CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        await uploader.DisposeAsync().ConfigureAwait(false);await allocator.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(uploader.ExitConfirmed);Assert.IsTrue(allocator.ExitConfirmed);
    }

    [TestMethod]
    public async Task CurrentFreshHeaderConstructorRejectsForeignBudgetBeforeNativeOrDriveWork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        var foreign=new StorageBudgetManager(new ProcessorStateStorageReader(input.StateDirectory));
        Assert.ThrowsExactly<InvalidOperationException>(()=>new CurrentCatalogHeaderPreparationService(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,foreign,fixture.Reader));
        Assert.AreEqual(0,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(0L,foreign.Status().ReservedBytes);
    }

    private static async Task<CurrentCatalogHeaderPreparationService> NewCurrentFreshHeaderOwnerAsync(CurrentReaderFixture fixture)
    {
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        return new(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader);
    }
    private static void SameFreshHeader(WorkspacePublicationRecord expected,WorkspacePublicationRecord actual)
    {
        Assert.AreEqual((expected.Id,expected.Scope,expected.Role,expected.MimeType,expected.Sha256,expected.FileId,expected.VerifiedVersion),
            (actual.Id,actual.Scope,actual.Role,actual.MimeType,actual.Sha256,actual.FileId,actual.VerifiedVersion));
        CollectionAssert.AreEqual(expected.Payload.ToArray(),actual.Payload.ToArray());
    }
}
