using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentSupersessionRetiresOnlyItsRealPrivateSnapshotOfflineWithoutInventingASelectedHead()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var before=await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false);
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;var puts=fixture.Http.Puts;
        var receipt=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Capture.Scope,receipt.Scope);Assert.AreEqual(owned.Capture.SnapshotId,receipt.SnapshotId);
        Assert.AreEqual(owned.Capture.CommitIntentId,receipt.CommitIntentId);Assert.IsNull(receipt.SelectedAnchor);Assert.AreEqual(1,receipt.WithdrawnRootPlans);
        CollectionAssert.AreEqual(before,await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false));
        Assert.AreEqual("complete",(await ReadCompletionCaptureAsync(fixture.Data,receipt.Scope.GenerationId).ConfigureAwait(false)).Phase);
        await AssertNoSupersededHeadAsync(owned).ConfigureAwait(false);
        await using var legacyCurrent=CatalogReconciler(fixture.Data);
        await using var legacy=new CatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,legacyCurrent);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>legacy.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        await using var ordinary=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>ordinary.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).BeginSnapshotRetirement(receipt.Scope.GenerationId),CancellationToken.None)).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",inventory.Checkpoint.Context.AccountRevision,CancellationToken.None).ConfigureAwait(false);
        await using var retirement=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(receipt,await retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(owned.SnapshotPath));Assert.AreEqual("retired",(await ReadCompletionCaptureAsync(fixture.Data,receipt.Scope.GenerationId).ConfigureAwait(false)).Phase);
        Assert.AreEqual(receipt,await retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
        await AssertNoSupersededHeadAsync(owned).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(puts,fixture.Http.Puts);Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentSupersessionRollsBackItsPairedReceiptWhenTheAtomicTerminalPhaseFails()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,"CREATE TRIGGER reject_supersession_phase BEFORE UPDATE OF phase ON catalog_generation_capture WHEN NEW.phase='complete' BEGIN SELECT RAISE(ABORT,'supersession atomic phase'); END");return true;
        },CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));
        Assert.IsNull(await ReadSupersessionAsync(owned).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.SnapshotPath));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER reject_supersession_phase");return true;},CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        await AssertNoSupersededHeadAsync(owned).ConfigureAwait(false);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentSupersessionKeepsAnUnconfirmedOrCorruptPrivateFileAndResumesTheSameOfflineRetirement()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var receipt=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var original=await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false);
        await File.WriteAllBytesAsync(owned.SnapshotPath,"not the captured private SQLite file"u8.ToArray()).ConfigureAwait(false);
        await using var retirement=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(owned.SnapshotPath));Assert.AreEqual("snapshot-retiring",(await ReadCompletionCaptureAsync(fixture.Data,receipt.Scope.GenerationId).ConfigureAwait(false)).Phase);
        Assert.AreEqual(receipt,await ReadSupersessionAsync(owned).ConfigureAwait(false));await AssertNoSupersededHeadAsync(owned).ConfigureAwait(false);
        await File.WriteAllBytesAsync(owned.SnapshotPath,original).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",inventory.Checkpoint.Context.AccountRevision,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(receipt,await retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));Assert.IsFalse(File.Exists(owned.SnapshotPath));
    }

    [TestMethod]
    public async Task CurrentSupersessionRecoversALostActualUnlinkAcknowledgementWithoutANewHeadOrCapture()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var receipt=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var capture=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).BeginSupersededSnapshotRetirement(receipt.Scope.GenerationId),CancellationToken.None).ConfigureAwait(false);
        using(var store=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget),CancellationToken.None).ConfigureAwait(false))
        using(var actual=await Task.Run(()=>store.RetireCompletedSnapshot(capture,CancellationToken.None)).ConfigureAwait(false))
            Assert.IsFalse(File.Exists(owned.SnapshotPath)); // Simulate process loss before the original SQL acknowledgement.
        Assert.AreEqual("snapshot-retiring",(await ReadCompletionCaptureAsync(fixture.Data,receipt.Scope.GenerationId).ConfigureAwait(false)).Phase);
        await using var retry=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(receipt,await retry.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
        await AssertNoSupersededHeadAsync(owned).ConfigureAwait(false);Assert.AreEqual("retired",(await ReadCompletionCaptureAsync(fixture.Data,receipt.Scope.GenerationId).ConfigureAwait(false)).Phase);
    }

    [TestMethod]
    public async Task CurrentSupersessionRequiresItsOriginalFreshOwnerAndWithdrawnRootBeforeAnyPhaseWrite()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:false).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlTransitionInProgressException>(()=>fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        var plan=await MaterializedOriginalRootPlanAsync(owned).ConfigureAwait(false);
        _=await fixture.Reader.WithdrawTransitionAsync(plan.Id,CurrentControlTransitionWithdrawalReason.ConfigurationChanged,CancellationToken.None).ConfigureAwait(false);
        var captured=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        inventory=await fixture.Reader.ObserveWorkspaceInventoryAsync(captured,CancellationToken.None).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));Assert.IsNull(await ReadSupersessionAsync(owned).ConfigureAwait(false));
        Assert.IsTrue(File.Exists(owned.SnapshotPath));
    }

    [TestMethod]
    public async Task CurrentSupersessionRejectsUnversionedHumanBindingChangesAgainstTheirActualArchivedConfiguration()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"UPDATE source_binding SET enabled=1");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));Assert.IsNull(await ReadSupersessionAsync(owned).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.SnapshotPath));
    }

    [TestMethod]
    public async Task CurrentSupersessionHistoricalReceiptSurvivesLaterHumanRevisionsButNeverAcceptsSemanticallyRetargetedHashes()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var receipt=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
            sources.Update(source.Id,true,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;
        },CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(receipt,await ReadSupersessionAsync(owned).ConfigureAwait(false));
        var invalid=new[]{receipt with {SnapshotId=Guid.NewGuid()},receipt with {Sequence=receipt.Sequence+1},
            receipt with {HumanConfigurationRevision=receipt.HumanConfigurationRevision+1},receipt with {OriginalWorkSha256=new string('f',64)},
            receipt with {WithdrawnRootPlans=receipt.WithdrawnRootPlans+1},receipt with {RootRecoverySha256=new string('e',64)},
            receipt with {SelectedAnchor=receipt.OriginalCommit},receipt with {Format="unsupported"},receipt with {Major=2}};
        foreach(var value in invalid)
        {
            try
            {
                await ReplaceSupersessionAsync(owned,value).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>ReadSupersessionAsync(owned)).ConfigureAwait(false);
                Assert.IsTrue(File.Exists(owned.SnapshotPath));
            }
            finally {await ReplaceSupersessionAsync(owned,receipt).ConfigureAwait(false);}
            Assert.AreEqual(receipt,await ReadSupersessionAsync(owned).ConfigureAwait(false));
        }
        await AssertNoSupersededHeadAsync(owned).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CurrentSupersessionCannotRelabelASelectedGenerationOrRetireWithoutItsDurableDisposition()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:false).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        await using var retirement=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>retirement.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ReadSupersessionAsync(owned).ConfigureAwait(false));Assert.IsNotNull(await owned.SelectionAsync().ConfigureAwait(false));
        Assert.IsTrue(File.Exists(owned.SnapshotPath));Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentSupersessionExitJoinsUncooperativePrivateConstructionAndRetainsItsOriginalRetirementClaim()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var receipt=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var directory=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>database.StateDirectory,CancellationToken.None).ConfigureAwait(false);
        using var space=new CurrentCompletionBlockingSpace(directory,receipt.Scope.GenerationId);var budget=new StorageBudgetManager(space);
        await using var retirement=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,budget);
        var finishing=retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None);Task stopping;
        try
        {
            await space.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);stopping=retirement.DisposeAsync().AsTask();
            Assert.IsFalse(finishing.IsCompleted);Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(retirement.ExitConfirmed);Assert.IsTrue(File.Exists(owned.SnapshotPath));
        }
        finally {space.Release.Set();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>finishing).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.IsTrue(retirement.ExitConfirmed);Assert.AreEqual(0L,budget.Status().ReservedBytes);
        var capture=await ReadCompletionCaptureAsync(fixture.Data,receipt.Scope.GenerationId).ConfigureAwait(false);Assert.AreEqual("snapshot-retiring",capture.Phase);
        using(var check=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget),CancellationToken.None).ConfigureAwait(false))
            Assert.IsTrue(check.ContainsCaptureFile(capture)); // Directory/use leases physically closed after cancellation.
        Assert.AreEqual(receipt,await ReadSupersessionAsync(owned).ConfigureAwait(false));await AssertNoSupersededHeadAsync(owned).ConfigureAwait(false);
        await using var retry=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(receipt,await retry.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));Assert.IsFalse(File.Exists(owned.SnapshotPath));
    }

    [TestMethod]
    public async Task CurrentSupersessionArchivesExactLongCountersAndMaximumUnicodeNamesWithoutChangingOriginalWork()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {CurrentStageSql(database,"UPDATE library_configuration SET revision=9007199254740992 WHERE id=1");return true;},CancellationToken.None).ConfigureAwait(false);
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var latest=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var sources=new LibrarySourceRepository(database);var old=sources.Snapshot();
            return sources.Add(Guid.NewGuid(),owned.Capture.Scope.AccountId,owned.Capture.Scope.AccountRevision,"extra-source",new string('\u05e9',1024),true,old.Revision);
        },CancellationToken.None).ConfigureAwait(false);
        var receipt=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(9007199254740994L,receipt.HumanConfigurationRevision);Assert.AreEqual(latest.Revision,receipt.HumanConfigurationRevision);
        Assert.AreEqual(owned.Capture.Scope,receipt.Scope);Assert.AreEqual(receipt,await ReadSupersessionAsync(owned).ConfigureAwait(false));
        await using var retirement=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(receipt,await retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));await AssertNoSupersededHeadAsync(owned).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CurrentSupersessionHistoricalReaderRejectsAnArchivedHumanRevisionBehindItsActualRootSourcePolicy()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var earlyInventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var early=await fixture.Reader.InspectMaterializedPredecessorAsync(earlyInventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var latest=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
            return sources.Update(source.Id,true,source.IncludeDescendants,source.Revision,snapshot.Revision);
        },CancellationToken.None).ConfigureAwait(false);
        var original=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var plan=new LibraryAuthorityRepository(database,fixture.Data.Budget).InitialPolicyPlan()!;
            return new ProtocolSigningJournal(database,fixture.Data.Budget).Read(plan.SourceIntentId)!.Body.ToArray();
        },CancellationToken.None).ConfigureAwait(false);
        var policy=await new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget)
            .SignControllerAsync(Guid.NewGuid(),"library-source-configuration",(_,sequence)=>
            {
                var body=System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
                body["sequence"]=sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                body["payload"]!["configurationRevision"]=latest.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var source=latest.Sources.Single();var item=body["payload"]!["sources"]!.AsArray().Single()!;
                item["revision"]=source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                item["includeDescendants"]=source.IncludeDescendants;item["enabled"]=source.Enabled;
                return new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(body));
            },CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {new LibraryAuthorityRepository(database,fixture.Data.Budget).SelectSourceConfiguration(policy.Envelope!.Value.Span);return true;},CancellationToken.None).ConfigureAwait(false);
        var bytes=policy.Envelope!.Value.ToArray();var file="supersession-new-source-policy";
        fixture.Data.Cloud[file]=new("permission-A",file,"metadata",8,bytes);fixture.Data.MetadataMimeTypes[file]="application/json";
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);
        var fence=await fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None).ConfigureAwait(false);
        var reference=new LibraryPolicyReference(new("permission-A",file,"metadata",bytes.Length,policy.EnvelopeSha256!),policy.Sequence);
        var transition=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget)
            .Prepare(Guid.NewGuid(),fence.Current.Attempt,new(capture.Head.Publishers,reference,null)),CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(transition.Id,CancellationToken.None).ConfigureAwait(false);
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var policyCapture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var actualPolicies=await fixture.Reader.ReadSelectedPoliciesAsync(policyCapture,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(latest.Revision,actualPolicies.Sources.Revision);
        Assert.IsTrue(actualPolicies.Sources.Revision>early.Original.HumanConfigurationRevision);
        // Reading policies must not grant/retain a different final inventory nonce.
        var freshCapture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        inventory=await fixture.Reader.ObserveWorkspaceInventoryAsync(freshCapture,CancellationToken.None).ConfigureAwait(false);
        var receipt=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(receipt,await ReadSupersessionAsync(owned).ConfigureAwait(false));
        var changed=receipt with {HumanConfigurationRevision=early.Original.HumanConfigurationRevision,HumanConfigurationSha256=early.Original.HumanConfigurationSha256};
        try
        {
            await ReplaceSupersessionAsync(owned,changed).ConfigureAwait(false); // Recompute outer hashes; keep a real archived human revision.
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>ReadSupersessionAsync(owned)).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(owned.SnapshotPath));
        }
        finally {await ReplaceSupersessionAsync(owned,receipt).ConfigureAwait(false);}
        Assert.AreEqual(receipt,await ReadSupersessionAsync(owned).ConfigureAwait(false));await AssertNoSupersededHeadAsync(owned).ConfigureAwait(false);
    }

    private static Task<CurrentCatalogGenerationSupersession?> ReadSupersessionAsync(CurrentCompletionFixture owned)=>owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(
        database=>new CatalogGenerationCaptureRepository(database,owned.Fixture.Data.Budget).Supersession(owned.Capture.Scope.GenerationId),CancellationToken.None);
    private static async Task AssertNoSupersededHeadAsync(CurrentCompletionFixture owned)
    {
        Assert.IsNull(await owned.SelectionAsync().ConfigureAwait(false));
        Assert.IsNull(await owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,owned.Fixture.Data.Budget).PublishedHead(),CancellationToken.None).ConfigureAwait(false));
    }
    private static Task<bool> ReplaceSupersessionAsync(CurrentCompletionFixture owned,CurrentCatalogGenerationSupersession value)=>owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
    {
        using var connection=OpenCurrentCompletionDatabase(database);using var transaction=connection.BeginTransaction();using var query=connection.CreateCommand();query.Transaction=transaction;
        query.CommandText="SELECT plan_hash FROM catalog_generation_publication WHERE id=$id";query.Parameters.AddWithValue("$id",owned.Capture.Scope.GenerationId.ToString("D"));
        var plan=(string)query.ExecuteScalar()!;var text=Encoding.UTF8.GetString(new CanonicalJson(49152).Encode(JsonSerializer.SerializeToUtf8Bytes(value,SelectionReceiptTestJson)));
        var digest=Convert.ToHexStringLower(SHA256.HashData(new CanonicalJson(65536).Encode(JsonSerializer.SerializeToUtf8Bytes(
            new {domain="mpa-current-generation-supersession-integrity-v1",PlanSha256=plan,text},SelectionReceiptTestJson))));
        query.CommandText="UPDATE processor_meta SET value=$text WHERE key=$key";query.Parameters.AddWithValue("$key","current_catalog_generation_supersession_v1:"+owned.Capture.Scope.GenerationId.ToString("D"));query.Parameters.AddWithValue("$text",text);
        Assert.AreEqual(1,query.ExecuteNonQuery());query.Parameters["$key"].Value+="\u003asha256";query.Parameters["$text"].Value=digest;Assert.AreEqual(1,query.ExecuteNonQuery());transaction.Commit();return true;
    },CancellationToken.None);
}
