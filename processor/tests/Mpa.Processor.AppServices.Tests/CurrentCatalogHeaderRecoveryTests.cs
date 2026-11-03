using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderLocalRecoveryKeepsTheOriginalCounterAndFirstEnvelopeWithoutGrantingOldSourceUploads(bool initiallySealed)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(initiallySealed).ConfigureAwait(false);var fixture=owned.Fixture;
        if(initiallySealed)
            await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>FreshLineageInventoryAsync(fixture)).ConfigureAwait(false);
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        var inspection=await fixture.Reader.InspectCatalogHeaderAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Original.Id,inspection.CommitIntentId);Assert.AreEqual(2L,inspection.Sequence);Assert.AreEqual(owned.Original.BodySha256,inspection.BodySha256);
        Assert.AreEqual(owned.Head.Commit,inspection.SelectedAnchor);Assert.IsNull(inspection.OriginalFileId);
        var cloud=fixture.Data.Cloud.Keys.OrderBy(v=>v,StringComparer.Ordinal).ToArray();var puts=fixture.Http.Puts;
        var snapshot=SHA256.HashData(await File.ReadAllBytesAsync(owned.Pending.Path).ConfigureAwait(false));
        var pending=await fixture.Reader.RecoverCatalogHeaderPendingAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Original.Id,pending.Id);Assert.AreEqual(owned.Pending.Capture.Scope,pending.Scope);
        Assert.AreEqual("catalog-commit",pending.Role);Assert.AreEqual("application/json",pending.MimeType);Assert.IsNull(pending.FileId);Assert.IsNull(pending.VerifiedVersion);
        var actual=await ReadRecoveredHeaderIntentAsync(owned).ConfigureAwait(false);
        Assert.AreEqual(owned.Original.BodySha256,actual.BodySha256);Assert.AreEqual(2L,actual.Sequence);Assert.IsNotNull(actual.Envelope);
        CollectionAssert.AreEqual(actual.Envelope!.Value.ToArray(),pending.Payload.ToArray());
        if(initiallySealed) CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),actual.Envelope.Value.ToArray());
        var again=await fixture.Reader.RecoverCatalogHeaderPendingAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pending.Sha256,again.Sha256);CollectionAssert.AreEqual(pending.Payload.ToArray(),again.Payload.ToArray());
        Assert.AreEqual("commit-sealed",(await ReadCompletionCaptureAsync(fixture.Data,owned.Pending.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        CollectionAssert.AreEqual(snapshot,SHA256.HashData(await File.ReadAllBytesAsync(owned.Pending.Path).ConfigureAwait(false)));
        CollectionAssert.AreEqual(cloud,fixture.Data.Cloud.Keys.OrderBy(v=>v,StringComparer.Ordinal).ToArray());Assert.AreEqual(puts,fixture.Http.Puts);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        // A pending original header makes a new REAL complete inventory possible.
        // It is not a synthetic replacement for the preceding missing-row failure.
        _=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>new WorkspacePublicationRepository(database,fixture.Data.Budget)
                .Prepare(Guid.NewGuid(),pending.Scope,"facts-index","application/json","{}"u8));return true;
        },CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentHeaderLocalRecoveryInsertFailureRetainsTheFirstSignatureAndRetriesItsOriginalPendingRow()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(false).ConfigureAwait(false);var fixture=owned.Fixture;
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {CurrentStageSql(database,"CREATE TRIGGER fail_header_pending BEFORE INSERT ON workspace_publication WHEN NEW.role='catalog-commit' BEGIN SELECT RAISE(ABORT,'header pending insertion'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>fixture.Reader.RecoverCatalogHeaderPendingAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        var sealedIntent=await ReadRecoveredHeaderIntentAsync(owned).ConfigureAwait(false);Assert.IsNotNull(sealedIntent.Envelope);
        Assert.AreEqual(owned.Original.BodySha256,sealedIntent.BodySha256);Assert.AreEqual(2L,sealedIntent.Sequence);
        Assert.IsNull(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
        Assert.AreEqual("dependencies-ready",(await ReadCompletionCaptureAsync(fixture.Data,owned.Pending.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_header_pending");return true;},CancellationToken.None).ConfigureAwait(false);
        var row=await fixture.Reader.RecoverCatalogHeaderPendingAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(sealedIntent.Envelope!.Value.ToArray(),row.Payload.ToArray());
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentHeaderLocalRecoveryPhaseFailureRollsBackItsPendingRowWithoutLosingTheOriginalSignature()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {CurrentStageSql(database,"CREATE TRIGGER fail_header_phase BEFORE UPDATE OF phase ON catalog_generation_capture WHEN NEW.phase='commit-sealed' BEGIN SELECT RAISE(ABORT,'header atomic phase'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>fixture.Reader.RecoverCatalogHeaderPendingAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false));
        Assert.AreEqual("dependencies-ready",(await ReadCompletionCaptureAsync(fixture.Data,owned.Pending.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
        CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),(await ReadRecoveredHeaderIntentAsync(owned).ConfigureAwait(false)).Envelope!.Value.ToArray());
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_header_phase");return true;},CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.RecoverCatalogHeaderPendingAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderLocalRecoveryRejectsForeignReadersAndStaleFencesBeforeAnySignatureOrPendingRow(bool stale)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(false).ConfigureAwait(false);var fixture=owned.Fixture;
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        if(stale) _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var caller=stale ? fixture.Reader : other;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>caller.RecoverCatalogHeaderPendingAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull((await ReadRecoveredHeaderIntentAsync(owned).ConfigureAwait(false)).Envelope);Assert.IsNull(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false));
        Assert.IsTrue(File.Exists(owned.Pending.Path));Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentHeaderLocalPreparationLowStorageKeepsTheMissingRowRecoverableWithoutReauthorizingOldFacts()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var signing=new CurrentControlTransitionJournal(database,fixture.Data.Budget).CaptureSigningInventory(fence.Attempt);
            var low=new StorageBudgetManager(new RetirementNoSpace());
            Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>new CatalogGenerationCaptureRepository(database,low)
                .PrepareCurrentOriginalHeader(owned.Pending.Capture.Scope.GenerationId,signing,fence.Attempt));
            Assert.AreEqual(0L,low.Status().ReservedBytes);return true;
        },CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
        var row=await fixture.Reader.RecoverCatalogHeaderPendingAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),row.Payload.ToArray());Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentHeaderLocalRecoveryRefusesAHigherNativeReservationInsteadOfPromotingItsOriginalOrphan()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(false).ConfigureAwait(false);var fixture=owned.Fixture;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var publication=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Publication(owned.Pending.Capture.Scope.GenerationId)!;
            var scope=publication.Capture.Scope;
            var dependencies=new PreparedCatalogDependencies(scope.LibraryId,scope.ConfigurationRevision,publication.Basis.Snapshot.DiscoveryRevision,
                publication.Basis.Snapshot.Assets,publication.Basis.SourcePolicySha256,publication.Plan!.RootIndex,publication.Plan.SourceCoverage);
            var approved=new LibraryAuthorityRepository(database,fixture.Data.Budget).CurrentSourceConfiguration()!;
            var writer=new CatalogCommitDocumentWriter(new(scope with {GenerationId=Guid.NewGuid()},dependencies,approved,
                publication.Basis.Parent,publication.Basis.SettingsRevision,publication.Basis.MinimumHistoryGeneration));
            _=new ProtocolSigningJournal(database,fixture.Data.Budget).Reserve(Guid.NewGuid(),scope.ActorId,scope.ActorEpoch,"catalog-commit",
                sequence=>writer.Encode(fixture.Data.Setup.Keys.PublicIdentity,sequence));
            return true;
        },CancellationToken.None).ConfigureAwait(false);
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>fixture.Reader.RecoverCatalogHeaderPendingAsync(fence,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull((await ReadRecoveredHeaderIntentAsync(owned).ConfigureAwait(false)).Envelope);Assert.IsNull(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false));
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    private static async Task<LocalCurrentControlFence> HeaderRecoveryFenceAsync(CurrentReaderFixture fixture)
    {
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        return await fixture.Reader.FenceAsync(capture,CancellationToken.None).ConfigureAwait(false);
    }
    private static Task<ProtocolSigningIntent> ReadRecoveredHeaderIntentAsync(HeaderRecoveryFixture owned)=>owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        new ProtocolSigningJournal(database,owned.Fixture.Data.Budget).Read(owned.Original.Id) ?? throw new InvalidDataException("Fixture lost its original native intent."),CancellationToken.None);
    private static Task<WorkspacePublicationRecord?> ReadRecoveredHeaderRowAsync(HeaderRecoveryFixture owned)=>owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        new WorkspacePublicationRepository(database,owned.Fixture.Data.Budget).Read(owned.Original.Id),CancellationToken.None);

    private sealed class HeaderRecoveryFixture(CurrentCompletionFixture selected,PublishedCatalogHead head,LineageFrozenCapture pending,ProtocolSigningIntent original) : IAsyncDisposable
    {
        internal CurrentReaderFixture Fixture=>selected.Fixture;
        internal PublishedCatalogHead Head {get;}=head;
        internal LineageFrozenCapture Pending {get;}=pending;
        internal ProtocolSigningIntent Original {get;}=original;
        internal CurrentCompletionFixture Selected=>selected;
        internal static async Task<HeaderRecoveryFixture> CreateAsync(bool seal,
            Action<ProcessorStateDatabase,StorageBudgetManager,WorkspacePublicationScope>? beforeSourceChange=null)
        {
            var selected=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);
            try
            {
                var fixture=selected.Fixture;var data=fixture.Data;var head=await selected.CompleteAsync().ConfigureAwait(false);
                await using(var retirement=new CurrentCatalogGenerationCompletionService(data.Setup.Accounts.State,data.Budget))
                    _=await retirement.FinishAsync(selected.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
                var pending=await CreateLineageFrozenCaptureAsync(fixture).ConfigureAwait(false);
                var basis=new CapturedCatalogBasis(pending.Capture.Snapshot!,head.Commit,head.Sequence,pending.Approved.EnvelopeSha256,0,1);
                await data.Setup.Accounts.State.ExecuteAsync(database=>
                {new CatalogGenerationCaptureRepository(database,data.Budget).CapturePublicationBasis(pending.Capture.Scope.GenerationId,basis);return true;},CancellationToken.None).ConfigureAwait(false);
                var retained=await data.Setup.Accounts.State.ExecuteAsync(database=>
                {
                    var store=new CatalogGenerationSnapshotStore(database.StateDirectory,database,data.Budget);
                    try{return(Store:store,Frozen:store.Open(new CatalogGenerationCaptureRepository(database,data.Budget).Read(pending.Capture.Scope.GenerationId)!,CancellationToken.None));}
                    catch{store.Dispose();throw;}
                },CancellationToken.None).ConfigureAwait(false);
                PreparedCatalogDependencies dependencies;
                using(retained.Store) using(retained.Frozen)
                    dependencies=await new CatalogGenerationDependencyBuilder(new(pending.Capture.Scope.AccountId,pending.Capture.Scope.FolderId),
                        new CurrentCompletionFixtureSink(fixture,pending.Capture.Scope)).BuildAsync(retained.Frozen,pending.Approved,null,CancellationToken.None).ConfigureAwait(false);
                var intent=await data.Setup.Accounts.State.ExecuteAsync(database=>
                {
                    var captures=new CatalogGenerationCaptureRepository(database,data.Budget);var plan=new CapturedCatalogCommitPlan(basis,dependencies.RootIndex,dependencies.SourceCoverage);
                    captures.SealDependencies(pending.Capture.Scope.GenerationId,plan);
                    var journal=new ProtocolSigningJournal(database,data.Budget);
                    var work=journal.Reserve(pending.Capture.CommitIntentId,pending.Capture.Scope.ActorId,pending.Capture.Scope.ActorEpoch,"catalog-commit",sequence=>plan.EncodeBody(pending.Capture.Scope,sequence));
                    if(seal) work=journal.Seal(work.Id,work.BodySha256,data.Setup.Keys.SignCatalogDocument(work.Body.Span));
                    beforeSourceChange?.Invoke(database,data.Budget,pending.Capture.Scope);
                    var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
                    sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return work;
                },CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(2L,intent.Sequence);Assert.IsNull(await data.Setup.Accounts.State.ExecuteAsync(database=>
                    new WorkspacePublicationRepository(database,data.Budget).Read(intent.Id),CancellationToken.None).ConfigureAwait(false));
                return new(selected,head,pending,intent);
            }
            catch{await selected.DisposeAsync().ConfigureAwait(false);throw;}
        }
        public ValueTask DisposeAsync()=>selected.DisposeAsync();
    }
}
