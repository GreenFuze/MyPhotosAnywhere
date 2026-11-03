using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly JsonSerializerOptions UnsignedTestJson=new() {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CurrentUnsignedDispositionPreservesEveryPartialOriginalAndAdmitsOnlyTheNextRealCapture(int materialization)
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(materialization).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);var reads=fixture.Http.BodyReads;var puts=fixture.Http.Puts;
        var before=await File.ReadAllBytesAsync(owned.Partial.Path).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Partial.Capture.Scope,receipt.Scope);Assert.AreEqual(owned.Partial.Capture.SnapshotId,receipt.SnapshotId);
        Assert.AreEqual(owned.Partial.Capture.CommitIntentId,receipt.CommitIntentId);Assert.AreEqual("publication-planned",receipt.OriginalPhase);
        Assert.AreEqual(owned.Head.Commit,receipt.SelectedAnchor);Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        CollectionAssert.AreEqual(before,await File.ReadAllBytesAsync(owned.Partial.Path).ConfigureAwait(false));
        await AssertUnsignedOriginalAsync(owned).ConfigureAwait(false);
        await using var selectedCompletion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>selectedCompletion.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        await using var superseded=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>superseded.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).BeginSnapshotRetirement(receipt.Scope.GenerationId),CancellationToken.None)).ConfigureAwait(false);
        await using var retirement=new CurrentUnsignedGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(receipt,await retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(owned.Partial.Path));Assert.AreEqual(receipt,await retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));await AssertUnsignedOriginalAsync(owned).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(puts,fixture.Http.Puts);Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        _=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
        var next=await CreateLineageFrozenCaptureAsync(fixture).ConfigureAwait(false);
        Assert.AreNotEqual(receipt.Scope.GenerationId,next.Capture.Scope.GenerationId);Assert.AreEqual(0L,next.Capture.Snapshot!.Assets);
        var fresh=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var basis=new CapturedCatalogBasis(next.Capture.Snapshot!,owned.Head.Commit,owned.Head.Sequence,next.Approved.EnvelopeSha256,0,1);
        var captured=await fixture.Reader.CaptureCurrentPublicationBasisAsync(fresh,next.Capture.Scope.GenerationId,basis,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Head.Sequence,captured.Basis.ParentSequence);Assert.AreEqual(owned.Head.Commit,captured.Basis.Parent);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentUnsignedDispositionRollsBackItsReceiptAndPhaseTogetherAndRetriesTheOriginalIdentity()
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(1).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {CurrentStageSql(database,"CREATE TRIGGER fail_unsigned_phase BEFORE UPDATE OF phase ON catalog_generation_capture WHEN NEW.phase='complete' BEGIN SELECT RAISE(ABORT,'unsigned atomic phase'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ReadUnsignedDispositionAsync(owned).ConfigureAwait(false));
        Assert.AreEqual("publication-planned",(await ReadCompletionCaptureAsync(fixture.Data,owned.Partial.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
        await AssertUnsignedOriginalAsync(owned).ConfigureAwait(false);Assert.IsTrue(File.Exists(owned.Partial.Path));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_unsigned_phase");return true;},CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentUnsignedDispositionRejectsUnchangedSourcesForeignReadersAndOldRootNoncesWithoutAlteringWork()
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(0,advance:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var initial=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.DisposeUnsignedGenerationAsync(initial,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        await owned.AdvanceSourcesAsync().ConfigureAwait(false);var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ReadUnsignedDispositionAsync(owned).ConfigureAwait(false));await AssertUnsignedOriginalAsync(owned).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CurrentUnsignedDispositionCannotSkipAReservedButUnsealedNativeCounter()
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(0,advance:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inputs=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var store=new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget);
            try{return(Store:store,Frozen:store.Open(new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Read(owned.Partial.Capture.Scope.GenerationId)!,CancellationToken.None));}
            catch{store.Dispose();throw;}
        },CancellationToken.None).ConfigureAwait(false);
        PreparedCatalogDependencies dependencies;
        using(inputs.Store) using(inputs.Frozen)
            dependencies=await new CatalogGenerationDependencyBuilder(new(owned.Partial.Capture.Scope.AccountId,owned.Partial.Capture.Scope.FolderId),
                new CurrentCompletionFixtureSink(fixture,owned.Partial.Capture.Scope)).BuildAsync(inputs.Frozen,owned.Partial.Approved,null,CancellationToken.None).ConfigureAwait(false);
        var intent=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var captures=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget);
            var plan=new CapturedCatalogCommitPlan(captures.Publication(owned.Partial.Capture.Scope.GenerationId)!.Basis,dependencies.RootIndex,dependencies.SourceCoverage);
            captures.SealDependencies(owned.Partial.Capture.Scope.GenerationId,plan);
            return new ProtocolSigningJournal(database,fixture.Data.Budget).Reserve(owned.Partial.Capture.CommitIntentId,owned.Partial.Capture.Scope.ActorId,
                owned.Partial.Capture.Scope.ActorEpoch,"catalog-commit",sequence=>plan.EncodeBody(owned.Partial.Capture.Scope,sequence));
        },CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(intent.Envelope);Assert.AreEqual(2L,intent.Sequence);
        await owned.AdvanceSourcesAsync().ConfigureAwait(false);var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ReadUnsignedDispositionAsync(owned).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Partial.Path));
        var after=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Data.Budget).Read(intent.Id),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(intent.BodySha256,after!.BodySha256);Assert.AreEqual(intent.Sequence,after.Sequence);Assert.IsNull(after.Envelope);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentUnsignedRetirementPreservesCorruptionOfflineAndRecoversTheLostActualUnlinkAcknowledgement()
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(2).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var original=await File.ReadAllBytesAsync(owned.Partial.Path).ConfigureAwait(false);var damaged=original.ToArray();damaged[^1]^=1;
        await File.WriteAllBytesAsync(owned.Partial.Path,damaged).ConfigureAwait(false);
        await using(var retirement=new CurrentUnsignedGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(owned.Partial.Path));await File.WriteAllBytesAsync(owned.Partial.Path,original).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",inventory.Checkpoint.Context.AccountRevision,CancellationToken.None).ConfigureAwait(false);
        var capture=await ReadCompletionCaptureAsync(fixture.Data,receipt.Scope.GenerationId).ConfigureAwait(false);Assert.AreEqual("snapshot-retiring",capture.Phase);
        using(var store=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget),CancellationToken.None).ConfigureAwait(false))
        using(var actual=await Task.Run(()=>store.RetireCompletedSnapshot(capture,CancellationToken.None)).ConfigureAwait(false))
            Assert.IsFalse(File.Exists(owned.Partial.Path)); // Crash after actual unlink before acknowledgement.
        await using var retry=new CurrentUnsignedGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(receipt,await retry.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));await AssertUnsignedOriginalAsync(owned).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CurrentUnsignedRetirementExitJoinsTheActualPrivateFileOwner()
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(1).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var directory=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>database.StateDirectory,CancellationToken.None).ConfigureAwait(false);
        using var space=new CurrentCompletionBlockingSpace(directory,receipt.Scope.GenerationId);var budget=new StorageBudgetManager(space);
        await using var retirement=new CurrentUnsignedGenerationRetirementService(fixture.Data.Setup.Accounts.State,budget);
        var finishing=retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None);Task stopping;
        try
        {
            await space.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);stopping=retirement.DisposeAsync().AsTask();
            Assert.IsFalse(finishing.IsCompleted);Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(retirement.ExitConfirmed);Assert.IsTrue(File.Exists(owned.Partial.Path));
        }
        finally{space.Release.Set();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>finishing).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.IsTrue(retirement.ExitConfirmed);Assert.AreEqual(0L,budget.Status().ReservedBytes);
        await using var retry=new CurrentUnsignedGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(receipt,await retry.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentUnsignedDispositionRejectsSemanticRetargetingEvenWhenItsOuterHashIsRecomputed()
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(1).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            using var connection=OpenCurrentCompletionDatabase(database);using var transaction=connection.BeginTransaction();
            var key="current_unsigned_generation_disposition_v1:"+receipt.Scope.GenerationId.ToString("D");
            using var query=connection.CreateCommand();query.Transaction=transaction;query.CommandText="SELECT value FROM processor_meta WHERE key=$key";query.Parameters.AddWithValue("$key",key);
            var node=JsonNode.Parse((string)query.ExecuteScalar()!)!.AsObject();node["SelectedAnchor"]=null;
            var text=Encoding.UTF8.GetString(new CanonicalJson(49152).Encode(JsonSerializer.SerializeToUtf8Bytes(node)));
            var digest=Convert.ToHexStringLower(SHA256.HashData(new CanonicalJson(65536).Encode(JsonSerializer.SerializeToUtf8Bytes(new
                {domain="mpa-current-unsigned-disposition-integrity-v1",receipt.Scope,receipt.SnapshotId,receipt.CommitIntentId,text},UnsignedTestJson))));
            using var update=connection.CreateCommand();update.Transaction=transaction;update.CommandText="UPDATE processor_meta SET value=CASE WHEN key=$key THEN $text ELSE $sha END WHERE key=$key OR key=$hash";
            update.Parameters.AddWithValue("$key",key);update.Parameters.AddWithValue("$hash",key+":sha256");update.Parameters.AddWithValue("$text",text);update.Parameters.AddWithValue("$sha",digest);
            Assert.AreEqual(2,update.ExecuteNonQuery());transaction.Commit();return true;
        },CancellationToken.None).ConfigureAwait(false);
        await using var retirement=new CurrentUnsignedGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var failure=await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        StringAssert.Contains(failure.Message,"selected anchor"); // Prove semantic rejection, not merely a bad recomputed outer hash.
        Assert.IsTrue(File.Exists(owned.Partial.Path));Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentUnsignedFirstPartialGenerationCanRetireWithoutFabricatingAnyPublishedHead()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        var partial=await CreateLineageFrozenCaptureAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var captures=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget);
            captures.CapturePublicationBasis(partial.Capture.Scope.GenerationId,new(partial.Capture.Snapshot!,null,0,partial.Approved.EnvelopeSha256,0,1));
            new WorkspacePublicationRepository(database,fixture.Data.Budget).Prepare(Guid.NewGuid(),partial.Capture.Scope,"coverage-root","application/json",
                new CatalogCoverageDocumentWriter(new(partial.Capture.Scope.AccountId,partial.Capture.Scope.FolderId)).EncodeRoot(partial.Capture.Scope.LibraryId,partial.Capture.Scope.ConfigurationRevision,1,0,[]));
            var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
            sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;
        },CancellationToken.None).ConfigureAwait(false);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(receipt.SelectedAnchor);Assert.IsNull(await LineageHeadAsync(fixture).ConfigureAwait(false));
        await using var retirement=new CurrentUnsignedGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(receipt,await retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(partial.Path));Assert.IsNull(await LineageHeadAsync(fixture).ConfigureAwait(false));
        _=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
        var next=await CreateLineageFrozenCaptureAsync(fixture).ConfigureAwait(false);var fresh=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var value=await fixture.Reader.CaptureCurrentPublicationBasisAsync(fresh,next.Capture.Scope.GenerationId,
            new(next.Capture.Snapshot!,null,0,next.Approved.EnvelopeSha256,0,1),CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(value.Basis.Parent);Assert.AreEqual(0L,value.Basis.ParentSequence);Assert.AreEqual(0L,next.Capture.Snapshot!.Assets);
    }

    [TestMethod]
    public async Task CurrentUnsignedDispositionLowStorageKeepsItsOriginalPartialWorkAndPrivateClaim()
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(1).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            // Capture actual public current state; never construct a legacy checkpoint,
            // fake a final fence or reach through the app's opaque inventory owner.
            var fenced=new LocalCurrentControlObservations(database,fixture.Data.Budget).Current()!;
            Assert.AreEqual(inventory.Checkpoint.RootAttemptId,fenced.Id);
            var signing=new CurrentControlTransitionJournal(database,fixture.Data.Budget).CaptureSigningInventory(fenced);
            var publications=new WorkspacePublicationRepository(database,fixture.Data.Budget).Inventory(inventory.Checkpoint.Context);
            var low=new StorageBudgetManager(new RetirementNoSpace());
            Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>new CatalogGenerationCaptureRepository(database,low)
                .DisposeUnsignedCurrentGeneration(owned.Partial.Capture.Scope.GenerationId,inventory.Checkpoint,signing,fenced,publications));
            Assert.AreEqual(0L,low.Status().ReservedBytes);return true;
        },CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(await ReadUnsignedDispositionAsync(owned).ConfigureAwait(false));
        Assert.AreEqual("publication-planned",(await ReadCompletionCaptureAsync(fixture.Data,owned.Partial.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
        Assert.IsTrue(File.Exists(owned.Partial.Path));await AssertUnsignedOriginalAsync(owned).ConfigureAwait(false);
        _=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
    }

    private static Task<CurrentUnsignedCatalogDisposition?> ReadUnsignedDispositionAsync(UnsignedGenerationFixture owned)=>owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        new CatalogGenerationCaptureRepository(database,owned.Fixture.Data.Budget).UnsignedDisposition(owned.Partial.Capture.Scope.GenerationId),CancellationToken.None);
    private static Task<bool> AssertUnsignedOriginalAsync(UnsignedGenerationFixture owned)=>owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
    {
        var current=new WorkspacePublicationRepository(database,owned.Fixture.Data.Budget).Read(owned.Work.Id)!;
        Assert.AreEqual(owned.Work.Scope,current.Scope);Assert.AreEqual(owned.Work.FileId,current.FileId);Assert.AreEqual(owned.Work.VerifiedVersion,current.VerifiedVersion);
        Assert.AreEqual(owned.Work.Sha256,current.Sha256);CollectionAssert.AreEqual(owned.Work.Payload.ToArray(),current.Payload.ToArray());
        Assert.IsNull(new ProtocolSigningJournal(database,owned.Fixture.Data.Budget).Read(owned.Partial.Capture.CommitIntentId));return true;
    },CancellationToken.None);

    private sealed class UnsignedGenerationFixture(CurrentCompletionFixture selected,PublishedCatalogHead head,LineageFrozenCapture partial,WorkspacePublicationRecord work) : IAsyncDisposable
    {
        internal CurrentReaderFixture Fixture=>selected.Fixture;
        internal PublishedCatalogHead Head {get;}=head;
        internal LineageFrozenCapture Partial {get;}=partial;
        internal WorkspacePublicationRecord Work {get;}=work;
        internal Task<bool> AdvanceSourcesAsync()=>Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;},CancellationToken.None);
        internal static async Task<UnsignedGenerationFixture> CreateAsync(int materialization,bool advance=true)
        {
            var selected=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);
            try
            {
                var fixture=selected.Fixture;var head=await selected.CompleteAsync().ConfigureAwait(false);
                await using(var retirement=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
                    _=await retirement.FinishAsync(selected.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
                var partial=await CreateLineageFrozenCaptureAsync(fixture).ConfigureAwait(false);
                var work=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
                {
                    var captures=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget);
                    captures.CapturePublicationBasis(partial.Capture.Scope.GenerationId,new(partial.Capture.Snapshot!,head.Commit,head.Sequence,partial.Approved.EnvelopeSha256,0,1));
                    var bytes=new CatalogCoverageDocumentWriter(new(partial.Capture.Scope.AccountId,partial.Capture.Scope.FolderId))
                        .EncodeRoot(partial.Capture.Scope.LibraryId,partial.Capture.Scope.ConfigurationRevision,1,0,[]);
                    var outbox=new WorkspacePublicationRepository(database,fixture.Data.Budget);
                    var row=outbox.Prepare(Guid.NewGuid(),partial.Capture.Scope,"coverage-root","application/json",bytes);
                    if(materialization>0) row=outbox.AssignFileId(row.Id,row.Sha256,"unsigned-original-"+row.Id.ToString("N"));
                    return row;
                },CancellationToken.None).ConfigureAwait(false);
                if(materialization>1)
                {
                    fixture.Data.Cloud[work.FileId!]=new("permission-A",work.FileId!,"metadata",9,work.Payload.ToArray());fixture.Data.MetadataMimeTypes[work.FileId!]="application/json";
                    if(materialization==3) work=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,fixture.Data.Budget)
                        .ConfirmVerified(work.Id,work.Scope.AccountId,work.Scope.FolderId,work.FileId!,work.Sha256,work.Payload.Length,9),CancellationToken.None).ConfigureAwait(false);
                }
                var result=new UnsignedGenerationFixture(selected,head,partial,work);
                if(advance) await result.AdvanceSourcesAsync().ConfigureAwait(false);return result;
            }
            catch{await selected.DisposeAsync().ConfigureAwait(false);throw;}
        }
        public ValueTask DisposeAsync()=>selected.DisposeAsync();
    }
}
