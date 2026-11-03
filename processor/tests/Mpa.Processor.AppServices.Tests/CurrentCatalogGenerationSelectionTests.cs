using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly JsonSerializerOptions SelectionReceiptTestJson=new()
        {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};
    [TestMethod]
    public async Task CurrentGenerationSelectionRecordsActualAdmissionWithoutCompletingTheCaptureAndRemainsHistoricalOffline()
    {
        await using var owned=await SelectedGenerationFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        Assert.IsNull(await owned.ReadAsync().ConfigureAwait(false));var before=await ReadCompletionCaptureAsync(fixture.Data,owned.GenerationId).ConfigureAwait(false);
        var active=await owned.ActivateAsync().ConfigureAwait(false);var selected=await owned.ReadAsync().ConfigureAwait(false);Assert.IsNotNull(selected);
        Assert.AreEqual(owned.GenerationId,selected.GenerationId);Assert.AreEqual(before.Scope,selected.Scope);
        Assert.AreEqual(before.SnapshotId,selected.SnapshotId);Assert.AreEqual(before.CommitIntentId,selected.CommitIntentId);
        Assert.AreEqual(1L,selected.Sequence);Assert.AreEqual(1L,selected.Assets);Assert.AreEqual(owned.Catalog.Checkpoint,selected.Inventory);
        Assert.AreEqual(active,selected.Activation);Assert.AreEqual(active.Catalog!.FileId,selected.Commit.FileId);
        Assert.AreEqual(before,await ReadCompletionCaptureAsync(fixture.Data,owned.GenerationId).ConfigureAwait(false));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget);
            Assert.IsNull(journal.Verification(owned.GenerationId));Assert.IsNull(journal.PublishedHead());return true;
        },CancellationToken.None).ConfigureAwait(false);
        // Another scan replaces the latest slot; the original selection must keep
        // its exact historical nonce, original scope and original inventory.
        _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",selected.Inventory.Context.AccountRevision,CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;var metadata=fixture.Http.MetadataReads;
        Assert.AreEqual(selected,await owned.ReadAsync().ConfigureAwait(false));Assert.AreEqual(reads,fixture.Http.BodyReads);
        Assert.AreEqual(metadata,fixture.Http.MetadataReads);Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentGenerationSelectionHashFailureRollsBackReceiptRootTerminalAndPointerAndRetriesOriginalIdentity()
    {
        await using var owned=await SelectedGenerationFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,"CREATE TRIGGER reject_generation_selection BEFORE INSERT ON processor_meta WHEN NEW.key='current_catalog_generation_selection_v1:"+
                owned.GenerationId.ToString("D")+":sha256' BEGIN SELECT RAISE(ABORT,'selection paired hash'); END");return true;
        },CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>owned.ActivateAsync()).ConfigureAwait(false);
        Assert.IsNull(await owned.ReadAsync().ConfigureAwait(false));Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(CurrentRootSigningDisposition.Pending,await owned.DispositionAsync().ConfigureAwait(false));
        Assert.AreEqual("commit-sealed",(await ReadCompletionCaptureAsync(fixture.Data,owned.GenerationId).ConfigureAwait(false)).Phase);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER reject_generation_selection");return true;},CancellationToken.None).ConfigureAwait(false);
        var active=await owned.ActivateAsync().ConfigureAwait(false);var receipt=await owned.ReadAsync().ConfigureAwait(false);Assert.IsNotNull(receipt);
        Assert.AreEqual(active,receipt.Activation);Assert.AreEqual(owned.GenerationId,receipt.GenerationId);
        Assert.AreEqual(CurrentRootSigningDisposition.Activated,await owned.DispositionAsync().ConfigureAwait(false));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentGenerationSelectionRejectsSemanticCorruptionEvenWithARecomputedOuterIntegrityHash()
    {
        await using var owned=await SelectedGenerationFixture.CreateAsync().ConfigureAwait(false);_=await owned.ActivateAsync().ConfigureAwait(false);
        var original=await owned.ReadAsync().ConfigureAwait(false);Assert.IsNotNull(original);var fixture=owned.Fixture;
        var corruptions=new[]
        {
            original with {Major=2},original with {GenerationId=Guid.NewGuid()},original with {CommitIntentId=Guid.NewGuid()},
            original with {Scope=original.Scope with {AccountId="another-account"}},original with {Assets=original.Assets+1},
            original with {Inventory=original.Inventory with {RootSha256=new string('f',64)}},
            original with {Activation=original.Activation with {ConfigurationRevision=original.Activation.ConfigurationRevision+1}}
        };
        foreach(var changed in corruptions)
        {
            try
            {
                await ReplaceSelectedGenerationReceiptAsync(fixture,owned.GenerationId,changed).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>owned.ReadAsync()).ConfigureAwait(false);
            }
            finally {await ReplaceSelectedGenerationReceiptAsync(fixture,owned.GenerationId,original).ConfigureAwait(false);}
            Assert.AreEqual(original,await owned.ReadAsync().ConfigureAwait(false));Assert.AreEqual(original.Activation,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        }
        Assert.AreEqual("commit-sealed",(await ReadCompletionCaptureAsync(fixture.Data,owned.GenerationId).ConfigureAwait(false)).Phase);
    }

    [TestMethod]
    public async Task CurrentGenerationSelectionKeepsItsFirstActualStageAfterRefreshAndJoinedPrivateIndexRetirement()
    {
        await using var owned=await SelectedGenerationFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var first=await owned.ActivateAsync().ConfigureAwait(false);var original=await owned.ReadAsync().ConfigureAwait(false);Assert.IsNotNull(original);
        var next=await PrepareReconciledGraphAsync(fixture,owned.Pool).ConfigureAwait(false);await using var nextStage=next.Stage;
        var nextCatalog=await fixture.Reader.ObserveSelectedInventoryAsync(next.Graph,CancellationToken.None).ConfigureAwait(false);
        var second=await fixture.Reader.ActivateReconciledAsync(nextCatalog,nextStage,CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(first.StageId,second.StageId);Assert.AreEqual(first.RootSha256,second.RootSha256);
        Assert.AreNotEqual(original.Inventory.RootAttemptId,nextCatalog.Checkpoint.RootAttemptId);
        Assert.AreEqual(original,await owned.ReadAsync().ConfigureAwait(false));Assert.AreEqual(second,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(1,(await owned.Stage.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Count);
        await owned.Stage.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owned.Stage.ExitConfirmed);
        await owned.Pool.RetireAsync(first.StageId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(second.StageId,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Id);
        Assert.IsFalse(File.Exists(await StagePathAsync(fixture,first.StageId).ConfigureAwait(false)));
        Assert.AreEqual(original,await owned.ReadAsync().ConfigureAwait(false));Assert.AreEqual(second,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    private static Task<bool> ReplaceSelectedGenerationReceiptAsync(CurrentReaderFixture fixture,Guid id,CurrentCatalogGenerationSelection value)=>
        fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            // Test corruption uses a second connection on the actual private DB;
            // production never exposes a JSON-based receipt admission API.
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=Path.Combine(database.StateDirectory,"processor.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());
            connection.Open();using var transaction=connection.BeginTransaction();using var query=connection.CreateCommand();query.Transaction=transaction;
            query.CommandText="SELECT plan_hash FROM catalog_generation_publication WHERE id=$id";query.Parameters.AddWithValue("$id",id.ToString("D"));
            var plan=query.ExecuteScalar() as string ?? throw new AssertFailedException("Original native publication plan disappeared.");
            var encoded=Encoding.UTF8.GetString(new CanonicalJson(49152).Encode(JsonSerializer.SerializeToUtf8Bytes(value,SelectionReceiptTestJson)));
            var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("mpa-current-generation-selection-v1\n"+plan+"\n"+encoded)));
            using var update=connection.CreateCommand();update.Transaction=transaction;update.CommandText="UPDATE processor_meta SET value=$value WHERE key=$key";
            update.Parameters.AddWithValue("$key","current_catalog_generation_selection_v1:"+id.ToString("D"));update.Parameters.AddWithValue("$value",encoded);
            Assert.AreEqual(1,update.ExecuteNonQuery());update.Parameters["$key"].Value="current_catalog_generation_selection_v1:"+id.ToString("D")+":sha256";
            update.Parameters["$value"].Value=hash;Assert.AreEqual(1,update.ExecuteNonQuery());transaction.Commit();return true;
        },CancellationToken.None);

    private sealed class SelectedGenerationFixture(CurrentReaderFixture fixture,LocalCurrentStagePool pool,LocalCurrentSqliteStage stage,
        Guid generationId,Guid rootPlanId,LocalCurrentReconciledCatalog catalog) : IAsyncDisposable
    {
        internal CurrentReaderFixture Fixture {get;}=fixture;
        internal LocalCurrentStagePool Pool {get;}=pool;
        internal LocalCurrentSqliteStage Stage {get;}=stage;
        internal Guid GenerationId {get;}=generationId;
        internal LocalCurrentReconciledCatalog Catalog {get;}=catalog;
        internal Task<CurrentCatalogGenerationSelection?> ReadAsync()=>Fixture.Data.Setup.Accounts.State.ExecuteAsync(
            database=>new CurrentCatalogGenerationSelectionRepository(database,Fixture.Data.Budget).Read(GenerationId),CancellationToken.None);
        internal Task<CurrentCatalogActivation> ActivateAsync()=>Fixture.Reader.ActivateReconciledAsync(Catalog,Stage,CancellationToken.None);
        internal Task<CurrentRootSigningDisposition> DispositionAsync()=>Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var current=new LocalCurrentControlObservations(database,Fixture.Data.Budget).Current()!;
            return new CurrentControlTransitionJournal(database,Fixture.Data.Budget).CaptureSigningInventory(current).Roots.Single(root=>root.Intent.Id==rootPlanId).Disposition;
        },CancellationToken.None);
        internal static async Task<SelectedGenerationFixture> CreateAsync()
        {
            var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);LocalCurrentStagePool? pool=null;LocalCurrentSqliteStage? stage=null;
            try
            {
                // Actual native plan/outbox/Root and real canonical SQLite file;
                // the publication snapshot descriptor is explicitly synthetic.
                // This tests selection, never publication snapshot completion.
                var catalog=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
                var plan=await PrepareTransitionAsync(fixture,catalog.Intent.Id).ConfigureAwait(false);
                _=await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
                pool=new(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
                var prepared=await PrepareReconciledGraphAsync(fixture,pool).ConfigureAwait(false);stage=prepared.Stage;
                var combined=await fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None).ConfigureAwait(false);
                return new(fixture,pool,stage,catalog.Plan.Scope.GenerationId,plan.Id,combined);
            }
            catch
            {
                await fixture.Reader.DisposeAsync().ConfigureAwait(false);if(stage is not null) await stage.DisposeAsync().ConfigureAwait(false);
                if(pool is not null) await pool.DisposeAsync().ConfigureAwait(false);await fixture.DisposeAsync().ConfigureAwait(false);throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            await Fixture.Reader.DisposeAsync().ConfigureAwait(false);await Stage.DisposeAsync().ConfigureAwait(false);
            await Pool.DisposeAsync().ConfigureAwait(false);await Fixture.DisposeAsync().ConfigureAwait(false);
        }
    }
}
