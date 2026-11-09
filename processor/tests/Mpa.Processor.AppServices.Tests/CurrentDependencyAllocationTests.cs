using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentDependencyAllocationRetainsOriginalBytesAndSpendsInventoryBeforeAnySecondProviderOperation()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var snapshot=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        var writes=(fixture.Data.Creates,fixture.Http.Puts,fixture.Data.Cloud.Count);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult("current-dependency-known-id");};
        await using var owner=DependencyAllocator(owned);
        var saved=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
        AssertDependencyAllocation(owned,admission.Original.Dependency,saved,"current-dependency-known-id");
        await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>fixture.Reader.AssertCatalogDependencyCloudAdmissionAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var next=await fixture.Reader.InspectCatalogDependencyCloudAsync(inventory,saved.Id,CancellationToken.None).ConfigureAwait(false);
        var resumed=await owner.AllocateAsync(next,CancellationToken.None).ConfigureAwait(false);
        AssertDependencyAllocation(owned,saved,resumed,saved.FileId!);Assert.AreEqual(1,calls);Assert.IsNull(owner.Failure);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);
        Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Http.Puts,fixture.Data.Cloud.Count));
        CollectionAssert.AreEqual(snapshot,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("current-dependency-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDependencyAllocationJoinsActualLateProviderAndCheckpointsBeforeCancellationOrExit(bool exit)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var writes=(fixture.Data.Creates,fixture.Http.Puts,fixture.Data.Cloud.Count);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=async _=>{calls++;entered.TrySetResult();await release.Task.ConfigureAwait(false);return "late-current-dependency";};
        var disposals=fixture.Data.Setup.Accounts.Readers.DisposalAttempts;
        await using var owner=DependencyAllocator(owned);using var caller=new CancellationTokenSource();
        var running=owner.AllocateAsync(admission,caller.Token);var stopping=Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=owner.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(running.IsCompleted);Assert.IsFalse(owner.ExitConfirmed);if(exit) Assert.IsFalse(stopping.IsCompleted);
            Assert.AreEqual(disposals,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally
        {
            release.TrySetResult();
            try {await running.ConfigureAwait(false);} catch(OperationCanceledException) { }
        }
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var saved=await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false);
        AssertDependencyAllocation(owned,admission.Original.Dependency,saved,"late-current-dependency");Assert.AreEqual(1,calls);
        if(exit) Assert.IsTrue(owner.ExitConfirmed);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);
        Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Http.Puts,fixture.Data.Cloud.Count));Assert.IsTrue(File.Exists(owned.Path));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDependencyAllocationKeepsFailedStorageCheckpointAndRetriesOrRetiresWithoutAllocatingAgain(bool exit)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);
        await using var reader=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,budget);
        var admission=await PrepareDependencyAllocationAdmissionAsync(owned,reader).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;space.Available=1;return Task.FromResult("dependency-known-before-low-storage");};
        await using var owner=new CurrentCatalogDependencyAllocationService(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,budget,reader);
        try
        {
            await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.AllocateAsync(admission,CancellationToken.None)).ConfigureAwait(false);
            Assert.IsNotNull(owner.Failure);Assert.AreEqual(1,calls);Assert.AreEqual(0L,budget.Status().ReservedBytes);
            Assert.IsNull((await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false)).FileId);
            Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));
            if(exit)
            {
                await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.DisposeAsync().AsTask()).ConfigureAwait(false);
                Assert.IsFalse(owner.ExitConfirmed);Assert.AreEqual(1,calls);
            }
        }
        finally {space.Available=20*StorageBudgetManager.Gibibyte;}
        if(exit) {await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);}
        else
        {
            var recovered=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
            AssertDependencyAllocation(owned,admission.Original.Dependency,recovered,"dependency-known-before-low-storage");Assert.IsNull(owner.Failure);
        }
        var saved=await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false);
        AssertDependencyAllocation(owned,admission.Original.Dependency,saved,"dependency-known-before-low-storage");Assert.AreEqual(1,calls);
        Assert.AreEqual(0L,budget.Status().ReservedBytes);Assert.IsTrue(File.Exists(owned.Path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDependencyAllocationRejectsForeignReaderAndSupersedingCaptureBeforeGenerateIds(bool stale)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult("must-not-allocate-dependency");};
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        if(stale) _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await using var owner=new CurrentCatalogDependencyAllocationService(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,stale?fixture.Reader:other);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>owner.AllocateAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,calls);Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentDependencyAllocationRejectsDifferentBudgetOwnerBeforeProviderOrResourceOwnership()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var writes=(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts);
        var other=new StorageBudgetManager(new CurrentAllocationSpace());
        Assert.ThrowsExactly<InvalidOperationException>(()=>new CurrentCatalogDependencyAllocationService(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,other,fixture.Reader));
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(writes,(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts));
        Assert.AreEqual(0L,other.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("metadata")]
    [DataRow("current-dependency-original")]
    [DataRow("not a canonical id")]
    public async Task CurrentDependencyAllocationRejectsProtectedOriginalControlAndMalformedIdsWithoutPoisoningExit(string returned)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        var calls=0;var writes=(fixture.Data.Creates,fixture.Http.Puts,fixture.Data.Cloud.Count);
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult(returned);};
        await using var owner=DependencyAllocator(owned);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>owner.AllocateAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1,calls);Assert.IsNull((await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false)).FileId);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));
        await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);
        Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Http.Puts,fixture.Data.Cloud.Count));Assert.IsFalse(fixture.Http.BodyFiles.Contains("current-dependency-original"));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CurrentDependencyAllocationRollsBackActualSqlFailureHumanDriftAndRewrittenIdentityThenKeepsItsFirstKnownId(int mode)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,mode switch
            {
                0=>"CREATE TRIGGER reject_dependency_allocation BEFORE UPDATE OF file_id ON workspace_publication BEGIN SELECT RAISE(ABORT,'dependency allocation fixture'); END",
                1=>"CREATE TRIGGER reject_dependency_allocation AFTER UPDATE OF file_id ON workspace_publication BEGIN UPDATE library_configuration SET revision=revision+1 WHERE id=1; END",
                2=>"CREATE TRIGGER reject_dependency_allocation AFTER UPDATE OF file_id ON workspace_publication BEGIN UPDATE workspace_publication SET file_id='rewritten-dependency-id' WHERE id=NEW.id; END",
                _=>throw new ArgumentOutOfRangeException(nameof(mode))
            });return true;
        },CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult("dependency-first-known-after-sql-failure");};
        await using var owner=DependencyAllocator(owned);
        try
        {
            Task<WorkspacePublicationRecord> Run()=>owner.AllocateAsync(admission,CancellationToken.None);
            if(mode==0) await Assert.ThrowsExactlyAsync<SqliteException>(Run).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<WorkspacePublicationConflictException>(Run).ConfigureAwait(false);
            Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(1,calls);Assert.IsNotNull(owner.Failure);
            Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
        finally {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER reject_dependency_allocation");return true;},CancellationToken.None).ConfigureAwait(false);}
        var saved=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
        AssertDependencyAllocation(owned,admission.Original.Dependency,saved,"dependency-first-known-after-sql-failure");Assert.AreEqual(1,calls);Assert.IsNull(owner.Failure);
        Assert.AreEqual(before.Signing,(await CurrentBirthStateAsync(fixture).ConfigureAwait(false)).Signing);Assert.IsTrue(File.Exists(owned.Path));
    }

    [TestMethod]
    public async Task CurrentDependencyAllocationRetainsActualReturnedIdentityAfterHumanSourceChangeWithoutGrantingObsoleteUpload()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=async _=>
        {
            calls++;await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            {
                var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
                sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;
            },CancellationToken.None).ConfigureAwait(false);return "known-before-human-source-change";
        };
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var writes=(fixture.Data.Creates,fixture.Http.Puts,fixture.Data.Cloud.Count);
        await using var owner=DependencyAllocator(owned);
        var saved=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
        AssertDependencyAllocation(owned,admission.Original.Dependency,saved,"known-before-human-source-change");Assert.AreEqual(1,calls);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.InspectCatalogDependencyCloudAsync(inventory,saved.Id,CancellationToken.None)).ConfigureAwait(false);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Head,after.Head);
        Assert.AreEqual(before.Human+1,after.Human);Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Http.Puts,fixture.Data.Cloud.Count));Assert.IsTrue(File.Exists(owned.Path));
    }

    [TestMethod]
    public async Task CurrentDependencyAllocationAdmissionRefusesActualPublishedRevocationWithoutChangingOriginalPendingWork()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var original=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);
        _=await RevokeForOriginalAuthorityTestAsync(fixture).ConfigureAwait(false);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        var writes=(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts);
        await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(()=>fixture.Reader.InspectCatalogDependencyCloudAsync(inventory,original.Original.Dependency.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(writes,(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts));
        Assert.IsNull((await ReadAllocatedDependencyAsync(owned,original.Original.Dependency.Id).ConfigureAwait(false)).FileId);Assert.IsTrue(File.Exists(owned.Path));
    }

    [TestMethod]
    public async Task CurrentDependencyAllocationLocalRetryKeepsAnAlreadyObservedOwnObjectWithoutAliasingAnotherRole()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult("observed-own-dependency-id");};
        await using var owner=DependencyAllocator(owned);
        var saved=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
        fixture.Data.Cloud.Add(saved.FileId!,new(saved.Scope.AccountId,saved.FileId!,saved.Scope.FolderId,9,saved.Payload.ToArray()));
        fixture.Data.MetadataMimeTypes[saved.FileId!]=saved.MimeType;
        _=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        var again=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget)
            .RetainReturnedOriginalDependencyAllocation(admission.Original,saved.FileId!),CancellationToken.None).ConfigureAwait(false);
        AssertDependencyAllocation(owned,saved,again,saved.FileId!);Assert.AreEqual(1,calls);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    private static CurrentCatalogDependencyAllocationService DependencyAllocator(CurrentDependencyFixture owned)=>
        new(owned.Fixture.Data.Setup.Accounts.State,owned.Fixture.Data.Setup.Accounts.Service,owned.Fixture.Data.Budget,owned.Fixture.Reader);

    private static async Task<LocalCurrentCatalogDependencyCloudAdmission> PrepareDependencyAllocationAdmissionAsync(CurrentDependencyFixture owned,LocalCurrentControlReader? reader=null)
    {
        var fixture=owned.Fixture;
        var row=await fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,"coverage-root","application/json",owned.Payload,CancellationToken.None).ConfigureAwait(false);
        var active=reader??fixture.Reader;var capture=await active.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var inventory=await active.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        return await active.InspectCatalogDependencyCloudAsync(inventory,row.Id,CancellationToken.None).ConfigureAwait(false);
    }

    private static Task<WorkspacePublicationRecord> ReadAllocatedDependencyAsync(CurrentDependencyFixture owned,Guid id)=>owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(
        database=>new WorkspacePublicationRepository(database,owned.Fixture.Data.Budget).Read(id)??throw new InvalidOperationException("Original dependency disappeared."),CancellationToken.None);

    private static void AssertDependencyAllocation(CurrentDependencyFixture owned,WorkspacePublicationRecord expected,WorkspacePublicationRecord actual,string file)
    {
        Assert.AreEqual(expected.Id,actual.Id);Assert.AreEqual(expected.Scope,actual.Scope);Assert.AreEqual(owned.Basis.Publication.Capture.Scope,actual.Scope);
        Assert.AreEqual(expected.Role,actual.Role);Assert.AreEqual(expected.MimeType,actual.MimeType);Assert.AreEqual(expected.Sha256,actual.Sha256);
        CollectionAssert.AreEqual(expected.Payload.ToArray(),actual.Payload.ToArray());Assert.AreEqual(file,actual.FileId);Assert.IsNull(actual.VerifiedVersion);
    }
}
