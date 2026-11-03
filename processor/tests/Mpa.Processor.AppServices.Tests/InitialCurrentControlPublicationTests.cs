using System.Net;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task InitialCurrentPublicationSerializesExactFirstHeadAndDoesNotRestartBootstrapInventory()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var inventories=fixture.InitialInventories;var events=ConfigureCurrentPublication(fixture);
        await using var owner=CurrentPublisher(fixture);
        var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>owner.PublishAsync(CancellationToken.None))).ConfigureAwait(false);
        Assert.IsTrue(results.All(result=>result==results[0]));var published=results[0];
        Assert.AreEqual("captured-3",published.Slot.FileId);Assert.AreEqual(3L,published.Receipt.RootSequence);Assert.AreEqual(7L,published.Receipt.DriveVersion);
        var head=LibraryCurrentControl.Read(fixture.Cloud[published.Slot.FileId!].Bytes.Span,published.Slot.ConnectionDescriptor());
        Assert.IsNull(head.Catalog);Assert.AreEqual(head.EnvelopeSha256,published.Receipt.EnvelopeSha256);
        Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(6,fixture.Creates);Assert.AreEqual(3,fixture.Cloud.Count);
        Assert.AreEqual(inventories,fixture.InitialInventories);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        string[] expectedAttempt=["read:captured-1","read:captured-2","create:captured-3","read:captured-1","read:captured-2","read:captured-3"];
        CollectionAssert.AreEqual(Enumerable.Range(0,4).SelectMany(_=>expectedAttempt).ToArray(),events.ToArray());
        await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        await using var reopened=CurrentPublisher(fixture);
        Assert.AreEqual(published,await reopened.PublishAsync(CancellationToken.None).ConfigureAwait(false));Assert.AreEqual(3,fixture.Allocations);
        var outbox=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,fixture.Budget).Identities(10),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2,outbox.Count); // Mutable current never enters the immutable outbox.
    }

    [TestMethod]
    public async Task InitialCurrentPublicationLostCompletionResumesSameIdentityAndSealedBytesAfterOwnerReopen()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        var create=fixture.Setup.Accounts.Readers.WorkspaceCreate!;var lost=false;
        fixture.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,hash,token)=>
        {
            var result=await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false);
            if (!lost) { lost=true;throw new HttpRequestException("Synthetic lost current completion after actual storage."); }
            return result;
        };
        await using(var owner=CurrentPublisher(fixture))
            await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));Assert.AreEqual(3,fixture.Cloud.Count);
        var first=fixture.Cloud["captured-3"].Bytes.ToArray();
        await using var reopened=CurrentPublisher(fixture);var result=await reopened.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("captured-3",result.Slot.FileId);Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(4,fixture.Creates);
        CollectionAssert.AreEqual(first,fixture.Cloud["captured-3"].Bytes.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitialCurrentPublicationPolicyReplacementOrVersionRewindStopsBeforeCreate(bool rewind)
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        var original=fixture.Cloud["captured-1"];
        fixture.Cloud["captured-1"]=rewind ? original with {DriveVersion=6} : original with {Bytes=new byte[]{1}};
        await using var owner=CurrentPublisher(fixture);
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2,fixture.Creates);Assert.AreEqual(3,fixture.Allocations);Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));
        fixture.Cloud["captured-1"]=original;
        var resumed=await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("captured-3",resumed.Slot.FileId);Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(3,fixture.Creates);
    }

    [TestMethod]
    public async Task InitialCurrentPublicationCannotOverwriteAnAlreadyConflictingSameIdOrChooseACopy()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        await using(var allocator=ControlAllocator(fixture)) await allocator.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Cloud["captured-3"]=new("permission-A","captured-3","metadata",9,new byte[]{9,8,7});
        fixture.Cloud["equal-copy"]=new("permission-A","equal-copy","metadata",9,new byte[]{9,8,7});
        var original=fixture.Cloud["captured-3"].Bytes.ToArray();await using var owner=CurrentPublisher(fixture);
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(2,fixture.Creates);Assert.AreEqual(4,fixture.Cloud.Count);
        CollectionAssert.AreEqual(original,fixture.Cloud["captured-3"].Bytes.ToArray());Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitialCurrentPublicationRejectsSourcesOrAccountChangedDuringPhysicalCreateButRetainsWork(bool sources)
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        var create=fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,hash,token)=>
        {
            var result=await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false);
            if (sources) await fixture.Setup.AddSourceAsync("changed-during-current-create").ConfigureAwait(false);
            else await fixture.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
            return result;
        };
        await using var owner=CurrentPublisher(fixture);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3,fixture.Creates);Assert.AreEqual(3,fixture.Cloud.Count);Assert.AreEqual(3,fixture.Allocations);
        Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));
        var saved=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Budget)
            .Read(new LibraryCurrentControlSlotRepository(database,fixture.Budget).Current()!.Id),CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(fixture.Cloud["captured-3"].Bytes.ToArray(),saved!.Envelope!.Value.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitialCurrentPublicationCloseOrCancellationJoinsUncooperativeWriteBeforeReleasingBorrowers(bool exit)
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        var create=fixture.Setup.Accounts.Readers.WorkspaceCreate!;using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,hash,_)=>
        { entered.TrySetResult();await release.Task.ConfigureAwait(false);return await create(folder,file,mime,bytes,hash,CancellationToken.None).ConfigureAwait(false); };
        await using var owner=CurrentPublisher(fixture);var publishing=owner.PublishAsync(caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if (exit) stopping=owner.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try
        {
            Assert.IsFalse(publishing.IsCompleted);if (exit) Assert.IsFalse(stopping.IsCompleted);
            Assert.AreEqual(0,fixture.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(()=>publishing).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(3,fixture.Cloud.Count);Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));
        fixture.Setup.Accounts.Readers.WorkspaceCreate=create;
        await using var resumed=CurrentPublisher(fixture);var result=await resumed.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("captured-3",result.Slot.FileId);Assert.AreEqual(3,fixture.Allocations);if (exit) Assert.IsTrue(owner.ExitConfirmed);
    }

    [TestMethod]
    public async Task InitialCurrentPublicationLowStorageRollsBackReceiptWithoutLosingCreatedIdentity()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);var create=fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,hash,token)=>
        { var result=await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false);space.Available=1;return result; };
        await using var owner=CurrentPublisher(fixture,budget);
        try { await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false); }
        finally { space.Available=20*StorageBudgetManager.Gibibyte;fixture.Setup.Accounts.Readers.WorkspaceCreate=create; }
        Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));Assert.AreEqual(3,fixture.Cloud.Count);Assert.AreEqual(0L,budget.Status().ReservedBytes);
        var resumed=await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("captured-3",resumed.Slot.FileId);Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(4,fixture.Creates);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitialCurrentPublicationCorruptOrMissingReceiptStopsBeforeAnotherCreate(bool missing)
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        await using var owner=CurrentPublisher(fixture);await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1,await CompletionSqlAsync(fixture,missing ?
            "DELETE FROM processor_meta WHERE key='initial_current_publication_v1'" :
            "UPDATE processor_meta SET value='{}' WHERE key='initial_current_publication_v1'").ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3,fixture.Creates);Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(3,fixture.Cloud.Count);
    }

    [TestMethod]
    public async Task InitialCurrentPublicationRequiresSharingApprovalBeforeAnyCreate()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        fixture.Setup.Shared=true;await using var owner=CurrentPublisher(fixture);
        await Assert.ThrowsExactlyAsync<LibraryWorkspaceApprovalException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2,fixture.Creates);Assert.AreEqual(3,fixture.Allocations);Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InitialCurrentPublicationReceiptCannotAcceptARewoundActualCurrentVersion()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        await using var owner=CurrentPublisher(fixture);var first=await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Cloud["captured-3"]=fixture.Cloud["captured-3"] with {DriveVersion=6};
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(first.Receipt,await CurrentReceiptAsync(fixture).ConfigureAwait(false));Assert.AreEqual(3,fixture.Allocations);
    }

    [TestMethod]
    public async Task InitialCurrentPublicationFailedReceiptTransactionRollsBackItsBodyDigestAndClaimTogether()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        await CompletionSqlAsync(fixture,"""
            CREATE TRIGGER fail_current_receipt BEFORE INSERT ON processor_meta
            WHEN NEW.key='initial_current_publication_v1:sha256'
            BEGIN SELECT RAISE(ABORT,'synthetic receipt checkpoint failure'); END;
            """).ConfigureAwait(false);
        await using var owner=CurrentPublisher(fixture);
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));Assert.AreEqual(3,fixture.Cloud.Count);
        await CompletionSqlAsync(fixture,"DROP TRIGGER fail_current_receipt").ConfigureAwait(false);
        var resumed=await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("captured-3",resumed.Slot.FileId);Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(4,fixture.Creates);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitialCurrentPublicationDoesNotTrustProviderCreateIdentityOrBody(bool body)
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        var create=fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,hash,token)=>
        {
            var result=await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false);
            return body ? result with {Bytes=new byte[bytes.Length]} : result with {AccountId="foreign-account"};
        };
        await using var owner=CurrentPublisher(fixture);
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));Assert.AreEqual(3,fixture.Cloud.Count);Assert.AreEqual(3,fixture.Allocations);
        fixture.Setup.Accounts.Readers.WorkspaceCreate=create;
        Assert.AreEqual("captured-3",(await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false)).Slot.FileId);
        Assert.AreEqual(3,fixture.Allocations);
    }

    [TestMethod]
    public async Task InitialCurrentPublicationRechecksSelectedPolicyBytesAfterPhysicalCreateBeforeFinalFence()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);var events=ConfigureCurrentPublication(fixture);
        var original=fixture.Cloud["captured-2"];var create=fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,hash,token)=>
        { var result=await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false);fixture.Cloud["captured-2"]=original with {Bytes=new byte[]{0}};return result; };
        await using var owner=CurrentPublisher(fixture);
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsFalse(events.Contains("read:captured-3",StringComparer.Ordinal));Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));
        fixture.Cloud["captured-2"]=original;fixture.Setup.Accounts.Readers.WorkspaceCreate=create;
        var resumed=await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("captured-3",resumed.Slot.FileId);Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(4,fixture.Creates);
    }

    [TestMethod]
    public async Task InitialCurrentPublicationFailedAllocationRetirementRetainsBorrowersUntilTheSameIdentityPersists()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);
        var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);
        fixture.Setup.Accounts.Readers.WorkspaceAllocate=_=>{fixture.Allocations++;space.Available=1;return Task.FromResult("retained-initial-current-id");};
        await using var owner=CurrentPublisher(fixture,budget);
        try
        {
            await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.DisposeAsync().AsTask()).ConfigureAwait(false);
            Assert.IsFalse(owner.ExitConfirmed);Assert.AreEqual(0,fixture.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally { space.Available=20*StorageBudgetManager.Gibibyte; }
        await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);
        var slot=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,budget).Current(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("retained-initial-current-id",slot!.FileId);Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(2,fixture.Creates);
        Assert.IsNull(await CurrentReceiptAsync(fixture).ConfigureAwait(false));
    }

    private static InitialCurrentControlPublicationService CurrentPublisher(InitialPublicationFixture fixture,StorageBudgetManager? budget=null)=>
        new(fixture.Setup.Accounts.State,fixture.Setup.Accounts.Service,fixture.Setup.Keys,budget ?? fixture.Budget);
    private static Task<InitialCurrentControlPublicationReceipt?> CurrentReceiptAsync(InitialPublicationFixture fixture)=>
        fixture.Setup.Accounts.State.ExecuteAsync(database=>new InitialCurrentControlPublicationRepository(database,fixture.Budget).Read(),CancellationToken.None);
    private static List<string> ConfigureCurrentPublication(InitialPublicationFixture fixture)
    {
        var events=new List<string>();var create=fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceRead=(folder,file,length,sha,token)=>
        {
            token.ThrowIfCancellationRequested();events.Add("read:"+file);
            if (!fixture.Cloud.TryGetValue(file,out var result)) throw new HttpRequestException("Synthetic exact missing identity.",null,HttpStatusCode.NotFound);
            if (result.Bytes.Length!=length || Convert.ToHexStringLower(SHA256.HashData(result.Bytes.Span))!=sha) throw new DriveWorkspaceObjectChangedException();
            Assert.AreEqual(folder,result.FolderId);return Task.FromResult(result);
        };
        fixture.Setup.Accounts.Readers.WorkspaceCreate=(folder,file,mime,bytes,sha,token)=>
        {
            events.Add("create:"+file);
            if (fixture.Cloud.TryGetValue(file,out var existing) && !existing.Bytes.Span.SequenceEqual(bytes.Span)) throw new DriveWorkspaceObjectChangedException();
            return create(folder,file,mime,bytes,sha,token);
        };
        return events;
    }
}
