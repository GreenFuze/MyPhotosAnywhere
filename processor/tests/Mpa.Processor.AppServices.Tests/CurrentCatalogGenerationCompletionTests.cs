using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentSelectedCompletionRetiresTheRealPublicationSnapshotAndAdmitsTheNextOriginalCapture()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        Assert.IsTrue(File.Exists(owned.SnapshotPath));var selection=await owned.SelectionAsync().ConfigureAwait(false);Assert.IsNotNull(selection);
        await using var legacyCurrent=CatalogReconciler(fixture.Data);
        await using var legacy=new CatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,legacyCurrent);
        var calls=fixture.Data.Setup.Accounts.Readers.DocumentCalls;
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>legacy.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(calls,fixture.Data.Setup.Accounts.Readers.DocumentCalls);Assert.IsTrue(File.Exists(owned.SnapshotPath));
        var reads=fixture.Http.BodyReads;await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var head=await completion.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Capture.Scope.GenerationId,head.GenerationId);Assert.AreEqual(1L,head.Sequence);Assert.AreEqual(1L,head.Assets);
        Assert.IsFalse(File.Exists(owned.SnapshotPath));Assert.AreEqual(reads,fixture.Http.BodyReads);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));Assert.AreEqual(selection,await owned.SelectionAsync().ConfigureAwait(false));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget);var retired=journal.Read(head.GenerationId)!;
            Assert.AreEqual(owned.Capture with {Phase="retired"},retired);Assert.AreEqual(head,journal.PublishedHead());
            Assert.IsNull(journal.Verification(head.GenerationId));var actual=journal.SelectedCurrentVerification(head.GenerationId);Assert.IsNotNull(actual);
            Assert.AreEqual("mpa-current-generation-completion",actual.Format);Assert.AreEqual(owned.Capture.SnapshotId,actual.SnapshotId);
            Assert.AreEqual(owned.Capture.CommitIntentId,actual.CommitIntentId);Assert.AreEqual(owned.Capture.Scope,actual.Scope);
            var next=journal.Begin(owned.Capture.Scope with {GenerationId=Guid.NewGuid()},Guid.NewGuid(),Guid.NewGuid());
            Assert.AreEqual("capturing",next.Phase);Assert.AreNotEqual(owned.Capture.SnapshotId,next.SnapshotId);
            Assert.AreEqual(head,journal.PublishedHead());return true;
        },CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(head,await completion.FinishAsync(head.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentSelectedCompletionLastHeadFailureRollsBackTheTypedVerificationAndPhaseWithoutReplacingTheSnapshot()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var original=await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {CurrentStageSql(database,"CREATE TRIGGER reject_current_completion_head BEFORE INSERT ON processor_meta WHEN NEW.key='catalog_published_head_v1' BEGIN SELECT RAISE(ABORT,'current completion final head'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>owned.CompleteAsync()).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget);
            Assert.AreEqual(owned.Capture,journal.Read(owned.Capture.Scope.GenerationId));Assert.IsNull(journal.PublishedHead());
            Assert.IsNull(journal.SelectedCurrentVerification(owned.Capture.Scope.GenerationId));Assert.IsNull(journal.Verification(owned.Capture.Scope.GenerationId));
            CurrentStageSql(database,"DROP TRIGGER reject_current_completion_head");return true;
        },CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);var head=await owned.CompleteAsync().ConfigureAwait(false);
        await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(head,await completion.FinishAsync(head.GenerationId,CancellationToken.None).ConfigureAwait(false));Assert.IsFalse(File.Exists(owned.SnapshotPath));
    }

    [TestMethod]
    public async Task CurrentSelectedCompletionOfflineCleanupPreservesACorruptRealSnapshotAndResumesOnlyAfterItsExactBytesAreRestored()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var head=await owned.CompleteAsync().ConfigureAwait(false);var original=await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false);
        var replacement=original.ToArray();replacement[^1]^=1;await File.WriteAllBytesAsync(owned.SnapshotPath,replacement).ConfigureAwait(false);
        var selection=await owned.SelectionAsync().ConfigureAwait(false);Assert.IsNotNull(selection);
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",selection.Inventory.Context.AccountRevision,CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;var calls=fixture.Data.Setup.Accounts.Readers.DocumentCalls;
        await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>completion.FinishAsync(head.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        CollectionAssert.AreEqual(replacement,await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false));
        Assert.AreEqual("snapshot-retiring",(await ReadCompletionCaptureAsync(fixture.Data,head.GenerationId).ConfigureAwait(false)).Phase);
        Assert.AreEqual(selection,await owned.SelectionAsync().ConfigureAwait(false));Assert.AreEqual(reads,fixture.Http.BodyReads);
        await File.WriteAllBytesAsync(owned.SnapshotPath,original).ConfigureAwait(false);
        Assert.AreEqual(head,await completion.FinishAsync(head.GenerationId,CancellationToken.None).ConfigureAwait(false));Assert.IsFalse(File.Exists(owned.SnapshotPath));
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(calls,fixture.Data.Setup.Accounts.Readers.DocumentCalls);
    }

    [TestMethod]
    public async Task CurrentSelectedCompletionCannotAcknowledgeOrRetireAnUploadedButActuallyUnselectedGeneration()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        Assert.IsNull(await owned.SelectionAsync().ConfigureAwait(false));var original=await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false);
        Assert.IsNull((await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false)).Head.Catalog);
        await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>completion.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));
        CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false));
        Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).PublishedHead(),CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentSelectedCompletionLostPhysicalRetirementAcknowledgementResumesItsOriginalClaimWithoutDriveAccess()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var head=await owned.CompleteAsync().ConfigureAwait(false);
        var capture=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).BeginSnapshotRetirement(head.GenerationId),CancellationToken.None).ConfigureAwait(false);
        using(var store=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget),CancellationToken.None).ConfigureAwait(false))
        using(var actual=await Task.Run(()=>store.RetireCompletedSnapshot(capture,CancellationToken.None)).ConfigureAwait(false))
            Assert.IsFalse(File.Exists(owned.SnapshotPath)); // Deliberately lose only the later state acknowledgement.
        Assert.AreEqual("snapshot-retiring",(await ReadCompletionCaptureAsync(fixture.Data,head.GenerationId).ConfigureAwait(false)).Phase);
        var selection=await owned.SelectionAsync().ConfigureAwait(false);Assert.IsNotNull(selection);
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",selection.Inventory.Context.AccountRevision,CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(head,await completion.FinishAsync(head.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("retired",(await ReadCompletionCaptureAsync(fixture.Data,head.GenerationId).ConfigureAwait(false)).Phase);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(selection,await owned.SelectionAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentSelectedCompletionExitJoinsAnUncooperativePrivateStoreConstructionAndRetainsItsOriginalClaim()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var directory=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>database.StateDirectory,CancellationToken.None).ConfigureAwait(false);
        using var space=new CurrentCompletionBlockingSpace(directory,owned.Capture.Scope.GenerationId);var budget=new StorageBudgetManager(space);
        await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,budget);
        var finishing=completion.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None);Task stopping;
        try
        {
            await space.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);stopping=completion.DisposeAsync().AsTask();
            Assert.IsFalse(finishing.IsCompleted);Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(completion.ExitConfirmed);
            Assert.IsTrue(File.Exists(owned.SnapshotPath));
        }
        finally {space.Release.Set();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>finishing).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.IsTrue(completion.ExitConfirmed);Assert.AreEqual(0L,budget.Status().ReservedBytes);
        Assert.AreEqual("snapshot-retiring",(await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
        // The constructor's directory lease must have physically closed even
        // though cancellation arrived while its space reader ignored the token.
        using(var check=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget),CancellationToken.None).ConfigureAwait(false))
            Assert.IsTrue(check.ContainsCaptureFile((await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false))));
        await using var retry=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        _=await retry.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);Assert.IsFalse(File.Exists(owned.SnapshotPath));
    }

    [TestMethod]
    public async Task CurrentSelectedCompletionRejectsChangedTypedEvidenceEvenWhenBothOuterIntegrityBindingsAreRecomputed()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);_=await owned.CompleteAsync().ConfigureAwait(false);
        var fixture=owned.Fixture;var original=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).SelectedCurrentVerification(owned.Capture.Scope.GenerationId),CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(original);var unchanged=await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false);
        var corruptions=new[]
        {
            original with {Format="unsupported-completion"},original with {Major=2},original with {GenerationId=Guid.NewGuid()},
            original with {Scope=original.Scope with {AccountId="another-account"}},original with {SnapshotId=Guid.NewGuid()},
            original with {CommitIntentId=Guid.NewGuid()},original with {Sequence=original.Sequence+1},original with {Assets=original.Assets+1},
            original with {Commit=original.Commit with {FileId="another-commit"}},original with {SelectionSha256=new string('f',64)}
        };
        foreach(var changed in corruptions)
        {
            try
            {
                await ReplaceCurrentCompletionAsync(owned,changed).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId)).ConfigureAwait(false);
                await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>completion.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
                CollectionAssert.AreEqual(unchanged,await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false));
            }
            finally {await ReplaceCurrentCompletionAsync(owned,original).ConfigureAwait(false);}
            Assert.AreEqual("complete",(await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
        }
        await using var retry=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        _=await retry.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);Assert.IsFalse(File.Exists(owned.SnapshotPath));
    }

    [TestMethod]
    public async Task CurrentSelectedCompletionRejectsUnknownAndMixedHeadFormatsBeforeAnyPrivateSnapshotRetirement()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var expected=await owned.CompleteAsync().ConfigureAwait(false);
        var fixture=owned.Fixture;var original=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            using var connection=OpenCurrentCompletionDatabase(database);using var query=connection.CreateCommand();
            query.CommandText="SELECT value FROM processor_meta WHERE key='catalog_published_head_v1'";
            return (string)query.ExecuteScalar()!;
        },CancellationToken.None).ConfigureAwait(false);
        var values=new List<string>();
        foreach(var field in new[]{"Format","Major","GenerationId","VerificationSha256"})
        {
            var value=JsonNode.Parse(original)!.AsObject();
            value[field]=field switch {"Format"=>"unsupported-head","Major"=>"2","GenerationId"=>Guid.NewGuid().ToString("D"),_=>new string('f',64)};
            values.Add(value.ToJsonString());
        }
        var legacy=JsonNode.Parse(original)!.AsObject();legacy.Remove("Format");legacy.Remove("Major");values.Add(legacy.ToJsonString());
        var mixed=JsonNode.Parse(original)!.AsObject();mixed["Observation"]=new JsonObject();values.Add(mixed.ToJsonString());
        foreach(var value in values)
        {
            try
            {
                await SetCurrentCompletionHeadAsync(fixture,value).ConfigureAwait(false);
                await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>completion.FinishAsync(expected.GenerationId,CancellationToken.None)).ConfigureAwait(false);
                Assert.IsTrue(File.Exists(owned.SnapshotPath));
                Assert.AreEqual("complete",(await ReadCompletionCaptureAsync(fixture.Data,expected.GenerationId).ConfigureAwait(false)).Phase);
            }
            finally {await SetCurrentCompletionHeadAsync(fixture,original).ConfigureAwait(false);}
            Assert.AreEqual(expected,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
                new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).PublishedHead(),CancellationToken.None).ConfigureAwait(false));
        }
        await using var retry=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(expected,await retry.FinishAsync(expected.GenerationId,CancellationToken.None).ConfigureAwait(false));Assert.IsFalse(File.Exists(owned.SnapshotPath));
    }

    private static Task<bool> ReplaceCurrentCompletionAsync(CurrentCompletionFixture owned,CurrentCatalogGenerationVerification value)=>
        owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            // Corrupt the actual private database, recomputing both outer hashes.
            // The independent original native plan/selection must still reject it.
            using var connection=OpenCurrentCompletionDatabase(database);using var transaction=connection.BeginTransaction();
            using var query=connection.CreateCommand();query.Transaction=transaction;
            query.CommandText="SELECT plan_hash FROM catalog_generation_publication WHERE id=$id";query.Parameters.AddWithValue("$id",owned.Capture.Scope.GenerationId.ToString("D"));
            var planHash=(string)query.ExecuteScalar()!;var capture=owned.Capture;
            var encoded=Encoding.UTF8.GetString(new CanonicalJson(49152).Encode(JsonSerializer.SerializeToUtf8Bytes(value,SelectionReceiptTestJson)));
            var digest=Convert.ToHexStringLower(SHA256.HashData(new CanonicalJson(65536).Encode(JsonSerializer.SerializeToUtf8Bytes(
                new {capture.Scope,capture.SnapshotId,capture.CommitIntentId,planHash,encoded}))));
            using var update=connection.CreateCommand();update.Transaction=transaction;
            update.CommandText="UPDATE catalog_generation_verification SET document=$value,document_hash=$hash WHERE id=$id";
            update.Parameters.AddWithValue("$id",capture.Scope.GenerationId.ToString("D"));update.Parameters.AddWithValue("$value",encoded);update.Parameters.AddWithValue("$hash",digest);
            Assert.AreEqual(1,update.ExecuteNonQuery());update.CommandText="SELECT value FROM processor_meta WHERE key='catalog_published_head_v1'";
            var pointer=JsonNode.Parse((string)update.ExecuteScalar()!)!.AsObject();pointer["VerificationSha256"]=digest;
            update.CommandText="UPDATE processor_meta SET value=$value WHERE key='catalog_published_head_v1'";
            update.Parameters["$value"].Value=Encoding.UTF8.GetString(new CanonicalJson(1024).Encode(Encoding.UTF8.GetBytes(pointer.ToJsonString())));
            Assert.AreEqual(1,update.ExecuteNonQuery());transaction.Commit();return true;
        },CancellationToken.None);

    private static Task<bool> SetCurrentCompletionHeadAsync(CurrentReaderFixture fixture,string value)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
    {
        using var connection=OpenCurrentCompletionDatabase(database);using var update=connection.CreateCommand();
        update.CommandText="UPDATE processor_meta SET value=$value WHERE key='catalog_published_head_v1'";update.Parameters.AddWithValue("$value",value);
        Assert.AreEqual(1,update.ExecuteNonQuery());return true;
    },CancellationToken.None);

    private static SqliteConnection OpenCurrentCompletionDatabase(ProcessorStateDatabase database)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=Path.Combine(database.StateDirectory,"processor.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());
        try {connection.Open();return connection;} catch {connection.Dispose();throw;}
    }

    private sealed class CurrentCompletionFixture(CurrentReaderFixture fixture,LocalCurrentStagePool pool,LocalCurrentSqliteStage stage,
        CatalogGenerationCapture capture,string snapshotPath) : IAsyncDisposable
    {
        internal CurrentReaderFixture Fixture {get;}=fixture;
        internal CatalogGenerationCapture Capture {get;}=capture;
        internal string SnapshotPath {get;}=snapshotPath;
        internal Task<CurrentCatalogGenerationSelection?> SelectionAsync()=>Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CurrentCatalogGenerationSelectionRepository(database,Fixture.Data.Budget).Read(Capture.Scope.GenerationId),CancellationToken.None);
        internal Task<PublishedCatalogHead> CompleteAsync()=>Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CatalogGenerationCaptureRepository(database,Fixture.Data.Budget).CompleteSelectedCurrentGeneration(Capture.Scope.GenerationId),CancellationToken.None);
        internal static async Task<CurrentCompletionFixture> CreateAsync(bool activate=true)
        {
            var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);LocalCurrentStagePool? pool=null;LocalCurrentSqliteStage? stage=null;
            try
            {
                var data=fixture.Data;
                var inputs=await data.Setup.Accounts.State.ExecuteAsync(database=>
                {
                    var authority=new LibraryAuthorityRepository(database,data.Budget);var local=authority.CaptureLocalPublicationAuthority();
                    var approved=authority.CurrentSourceConfiguration()!;var source=new LibrarySourceRepository(database).Snapshot().Sources.Single();
                    new DiscoveredCatalogRepository(database).ApplyBatch(source.Id,source.Revision,1,[new("read-only-original",1,"Original.jpg","image/jpeg",12,8,null,null,null,0)]);
                    var scope=new WorkspacePublicationScope(local.LibraryId,local.ActorId,local.ActorEpoch,Guid.NewGuid(),local.AccountId,
                        local.AccountRevision,local.FolderId,local.ConfigurationRevision,local.AuthoritySha256);
                    var journal=new CatalogGenerationCaptureRepository(database,data.Budget);var capture=journal.Begin(scope,Guid.NewGuid(),Guid.NewGuid());
                    // This fixture owns a REAL retained publication snapshot as well
                    // as the real canonical index/native plan/Root transport below.
                    using(var preparation=new CatalogPreparationDirectory(database.StateDirectory,database.InstallationId))
                    using(var frozen=FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(database),preparation,data.Budget))
                    using(var store=new CatalogGenerationSnapshotStore(database.StateDirectory,database,data.Budget))
                    {
                        Assert.AreEqual(1L,frozen.Count);Assert.AreEqual("read-only-original",frozen.ReadPage("").Assets.Single().FileId);
                        capture=journal.SealSnapshot(scope.GenerationId,store.Save(frozen,capture,CancellationToken.None));
                    }
                    var basis=new CapturedCatalogBasis(capture.Snapshot!,null,0,approved.EnvelopeSha256,0,1);
                    journal.CapturePublicationBasis(scope.GenerationId,basis);
                    return (Capture:capture,Approved:approved,Basis:basis,Path:Path.Combine(database.StateDirectory,"catalog-generation-snapshots","snapshot-"+capture.SnapshotId.ToString("N")+".sqlite"));
                },CancellationToken.None).ConfigureAwait(false);
                var retained=await data.Setup.Accounts.State.ExecuteAsync(database=>
                {
                    var store=new CatalogGenerationSnapshotStore(database.StateDirectory,database,data.Budget);
                    try {return (Store:store,Frozen:store.Open(new CatalogGenerationCaptureRepository(database,data.Budget).Read(inputs.Capture.Scope.GenerationId)!,CancellationToken.None));}
                    catch {store.Dispose();throw;}
                },CancellationToken.None).ConfigureAwait(false);
                PreparedCatalogDependencies dependencies;
                using(retained.Store) using(retained.Frozen)
                    dependencies=await new CatalogGenerationDependencyBuilder(new(inputs.Capture.Scope.AccountId,inputs.Capture.Scope.FolderId),
                        new CurrentCompletionFixtureSink(fixture,inputs.Capture.Scope)).BuildAsync(retained.Frozen,inputs.Approved,null,CancellationToken.None).ConfigureAwait(false);
                await data.Setup.Accounts.State.ExecuteAsync(database=>
                {new CatalogGenerationCaptureRepository(database,data.Budget).SealDependencies(inputs.Capture.Scope.GenerationId,new(inputs.Basis,dependencies.RootIndex,dependencies.SourceCoverage));return true;},CancellationToken.None).ConfigureAwait(false);
                var plan=new CatalogCommitPlan(inputs.Capture.Scope,dependencies,inputs.Approved,null,0,1);var writer=new CatalogCommitDocumentWriter(plan);
                var intent=await new JournaledProtocolSigner(data.Setup.Accounts.State,data.Setup.Keys,data.Budget)
                    .SignCatalogAsync(inputs.Capture.CommitIntentId,writer.Encode,CancellationToken.None).ConfigureAwait(false);
                await data.Setup.Accounts.State.ExecuteAsync(database=>
                {new CatalogGenerationCaptureRepository(database,data.Budget).ConfirmCommitSealed(inputs.Capture.Scope.GenerationId);return true;},CancellationToken.None).ConfigureAwait(false);
                await new CurrentCompletionFixtureSink(fixture,inputs.Capture.Scope).StoreCommitAsync(intent).ConfigureAwait(false);
                var transition=await PrepareTransitionAsync(fixture,intent.Id).ConfigureAwait(false);
                if(activate) _=await fixture.Reader.PublishTransitionAsync(transition.Id,CancellationToken.None).ConfigureAwait(false);
                InstallCurrentInventoryTransport(fixture);
                pool=new(data.Setup.Accounts.State,data.Budget);var prepared=await PrepareReconciledGraphAsync(fixture,pool).ConfigureAwait(false);stage=prepared.Stage;
                var catalog=await fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None).ConfigureAwait(false);
                if(activate) _=await fixture.Reader.ActivateReconciledAsync(catalog,stage,CancellationToken.None).ConfigureAwait(false);
                var capture=await ReadCompletionCaptureAsync(data,inputs.Capture.Scope.GenerationId).ConfigureAwait(false);
                Assert.AreEqual("commit-sealed",capture.Phase);Assert.IsTrue(File.Exists(inputs.Path));
                return new(fixture,pool,stage,capture,inputs.Path);
            }
            catch
            {
                await fixture.Reader.DisposeAsync().ConfigureAwait(false);if(stage is not null) await stage.DisposeAsync().ConfigureAwait(false);
                if(pool is not null) await pool.DisposeAsync().ConfigureAwait(false);await fixture.DisposeAsync().ConfigureAwait(false);throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            await Fixture.Reader.DisposeAsync().ConfigureAwait(false);await stage.DisposeAsync().ConfigureAwait(false);
            await pool.DisposeAsync().ConfigureAwait(false);await Fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Isolated synthetic metadata transport, not a real Google upload.
    /// It journals original bytes/identity before the fixture cloud allocation.
    /// The actual current inventory later rereads every object through its owner.</summary>
    private sealed class CurrentCompletionFixtureSink(CurrentReaderFixture fixture,WorkspacePublicationScope scope) : ICatalogGenerationObjectSink
    {
        public Task<CatalogObjectReference> StoreAsync(string role,string mimeType,ReadOnlyMemory<byte> payload,CancellationToken cancellationToken)=>
            StoreAsync(Guid.NewGuid(),role,mimeType,payload,cancellationToken);
        internal Task<CatalogObjectReference> StoreCommitAsync(ProtocolSigningIntent intent)=>
            StoreAsync(intent.Id,"catalog-commit","application/json",intent.Envelope!.Value,CancellationToken.None);
        private async Task<CatalogObjectReference> StoreAsync(Guid id,string role,string mimeType,ReadOnlyMemory<byte> payload,CancellationToken token)
        {
            var bytes=payload.ToArray();var data=fixture.Data;
            var work=await data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,data.Budget).Prepare(id,scope,role,mimeType,bytes),token).ConfigureAwait(false);
            var file="current-physical-"+id.ToString("N");
            await data.Setup.Accounts.State.ExecuteAsync(database=>
            {new WorkspacePublicationRepository(database,data.Budget).AssignFileId(work.Id,work.Sha256,file);return true;},token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();data.Cloud.Add(file,new(scope.AccountId,file,scope.FolderId,7,bytes));data.MetadataMimeTypes.Add(file,mimeType);
            await data.Setup.Accounts.State.ExecuteAsync(database=>
            {new WorkspacePublicationRepository(database,data.Budget).ConfirmVerified(work.Id,scope.AccountId,scope.FolderId,file,work.Sha256,bytes.Length,7);return true;},token).ConfigureAwait(false);
            return new(scope.AccountId,file,scope.FolderId,bytes.Length,work.Sha256);
        }
    }

    private sealed class CurrentCompletionBlockingSpace(string directory,Guid generation) : IStorageSpaceReader,IDisposable
    {
        private int _armed=1;
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release {get;}=new(false);
        public StorageSpaceSnapshot Read()
        {
            if(Volatile.Read(ref _armed)!=0 && Phase()=="snapshot-retiring" && Interlocked.Exchange(ref _armed,0)==1)
            {Entered.TrySetResult();Release.Wait();}
            return new(100*StorageBudgetManager.Gibibyte,20*StorageBudgetManager.Gibibyte,0);
        }
        private string Phase()
        {
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=Path.Combine(directory,"processor.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
            connection.Open();using var query=connection.CreateCommand();query.CommandText="SELECT phase FROM catalog_generation_capture WHERE id=$id";query.Parameters.AddWithValue("$id",generation.ToString("D"));
            return query.ExecuteScalar() as string ?? throw new AssertFailedException("Original physical generation disappeared.");
        }
        public void Dispose()=>Release.Dispose();
    }
}
