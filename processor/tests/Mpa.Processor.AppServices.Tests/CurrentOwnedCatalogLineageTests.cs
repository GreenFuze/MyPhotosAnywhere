using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentOwnedLineageRootPreparationRollsBackItsExactNativeCounterAndPlanTogether()
    {
        await using var owned=await OwnedLineageFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Selected.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        _=await fixture.Reader.CaptureCurrentPublicationBasisAsync(inventory,owned.Next.Capture.Scope.GenerationId,owned.NextBasis,CancellationToken.None).ConfigureAwait(false);
        var intent=await BuildLineagePublicationAsync(fixture,owned.Next,owned.NextBasis).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var target=new CurrentControlTransitionTarget(capture.Head.Publishers,owned.Sources,intent.Id);
        inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var before=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Data.Budget)
            .Inventory(owned.Next.Capture.Scope.LibraryId),CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {CurrentStageSql(database,"CREATE TRIGGER reject_owned_lineage_root BEFORE INSERT ON processor_meta WHEN NEW.key GLOB 'current_control_transition_v1:*' BEGIN SELECT RAISE(ABORT,'owned lineage Root atomicity'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        var id=Guid.NewGuid();var puts=fixture.Http.Puts;
        try
        {
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(()=>fixture.Reader.PrepareCurrentCatalogTransitionAsync(id,inventory,target,CancellationToken.None)).ConfigureAwait(false);
            var after=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Data.Budget)
                .Inventory(owned.Next.Capture.Scope.LibraryId),CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(before.Sha256,after.Sha256);Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
            Assert.AreEqual(puts,fixture.Http.Puts);Assert.IsTrue(File.Exists(owned.Next.Path));
        }
        finally
        {
            await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER reject_owned_lineage_root");return true;},CancellationToken.None).ConfigureAwait(false);
        }
        var root=await fixture.Reader.PrepareCurrentCatalogTransitionAsync(id,inventory,target,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(id,root.Id);Assert.AreEqual(before.Actors.Single(v=>v.Role=="controller").Sequence+1,root.Sequence);
        Assert.AreEqual(puts,fixture.Http.Puts);Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentOwnedLineageRepeatedSourceChangesRetireOnlyOwnedSnapshotsAndKeepTheSameActualSelectedAnchor()
    {
        await using var owned=await OwnedLineageFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Selected.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        _=await fixture.Reader.CaptureCurrentPublicationBasisAsync(inventory,owned.Next.Capture.Scope.GenerationId,owned.NextBasis,CancellationToken.None).ConfigureAwait(false);
        var intent=await BuildLineagePublicationAsync(fixture,owned.Next,owned.NextBasis).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var root=await fixture.Reader.PrepareCurrentCatalogTransitionAsync(Guid.NewGuid(),inventory,new(capture.Head.Publishers,owned.Sources,intent.Id),CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
            sources.Update(source.Id,true,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;
        },CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.WithdrawTransitionAsync(root.Id,CurrentControlTransitionWithdrawalReason.ConfigurationChanged,CancellationToken.None).ConfigureAwait(false);
        inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);var puts=fixture.Http.Puts;
        var third=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Next.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Head.Commit,third.SelectedAnchor);Assert.AreEqual(3L,third.Sequence);Assert.AreEqual(4L,third.HumanConfigurationRevision);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Next.Path));
        await using var retirement=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(third,await retirement.FinishAsync(third.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));Assert.IsFalse(File.Exists(owned.Next.Path));
        _=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
        var fourth=await CreateLineageFrozenCaptureAsync(fixture).ConfigureAwait(false);
        inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var basis=new CapturedCatalogBasis(fourth.Capture.Snapshot!,third.OriginalCommit,third.Sequence,fourth.Approved.EnvelopeSha256,0,1);
        var next=await fixture.Reader.CaptureCurrentPublicationBasisAsync(inventory,fourth.Capture.Scope.GenerationId,basis,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(third.OriginalCommit,next.Basis.Parent);Assert.AreEqual(3L,next.Basis.ParentSequence);Assert.AreEqual(4L,next.Capture.Scope.ConfigurationRevision);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.AreEqual(puts,fixture.Http.Puts);
        Assert.IsTrue(File.Exists(fourth.Path));Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentOwnedLineageSelectsTheNextActualGenerationThroughRetiredAncestryWithoutInventingTheIntermediateHead()
    {
        await using var owned=await OwnedLineageFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Selected.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        _=await fixture.Reader.CaptureCurrentPublicationBasisAsync(inventory,owned.Next.Capture.Scope.GenerationId,owned.NextBasis,CancellationToken.None).ConfigureAwait(false);
        var intent=await BuildLineagePublicationAsync(fixture,owned.Next,owned.NextBasis).ConfigureAwait(false);Assert.AreEqual(3L,intent.Sequence);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);
        var fence=await fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None).ConfigureAwait(false);
        var target=new CurrentControlTransitionTarget(capture.Head.Publishers,owned.Sources,intent.Id);var puts=fixture.Http.Puts;
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CurrentControlTransitionJournal(database,fixture.Data.Budget).Prepare(Guid.NewGuid(),fence.Current.Attempt,target),CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(puts,fixture.Http.Puts);Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var root=await fixture.Reader.PrepareCurrentCatalogTransitionAsync(Guid.NewGuid(),inventory,target,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Next.Capture.Scope.GenerationId,root.GenerationId);Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(puts+1,fixture.Http.Puts);Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        var prepared=await PrepareReconciledGraphAsync(fixture,owned.Selected.Pool).ConfigureAwait(false);
        await using var stage=prepared.Stage;
        var observed=await fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.ActivateReconciledAsync(observed,stage,CancellationToken.None).ConfigureAwait(false);
        var selected=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentCatalogGenerationSelectionRepository(database,fixture.Data.Budget)
            .Read(owned.Next.Capture.Scope.GenerationId),CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(selected);Assert.AreEqual(3L,selected.Sequence);Assert.AreEqual(0L,selected.Assets);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var head=await completion.FinishAsync(owned.Next.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3L,head.Sequence);Assert.AreEqual(owned.Next.Capture.Scope.GenerationId,head.GenerationId);Assert.AreEqual(0L,head.Assets);
        Assert.AreEqual(head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsFalse(File.Exists(owned.Next.Path));
        Assert.AreEqual(owned.Superseded,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget)
            .Supersession(owned.Unselected.Capture.Scope.GenerationId),CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentOwnedLineageBasisKeepsActualSelectionSeparateFromTheExactRetiredUnselectedNativeParent()
    {
        await using var owned=await OwnedLineageFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Selected.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);var reads=fixture.Http.BodyReads;var puts=fixture.Http.Puts;
        var actual=await fixture.Reader.CaptureCurrentPublicationBasisAsync(inventory,owned.Next.Capture.Scope.GenerationId,owned.NextBasis,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Superseded.OriginalCommit,actual.Basis.Parent);Assert.AreEqual(2L,actual.Basis.ParentSequence);
        Assert.AreEqual(owned.Head.Commit,owned.Superseded.SelectedAnchor);Assert.AreEqual(1L,owned.Head.Sequence);
        Assert.AreEqual(3L,actual.Capture.Scope.ConfigurationRevision);Assert.AreEqual("publication-planned",actual.Capture.Phase);
        Assert.AreEqual(0L,actual.Capture.Snapshot!.Assets,"Newly disabled source facts cannot enter the new capture.");
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(puts,fixture.Http.Puts);
        Assert.IsTrue(File.Exists(owned.Next.Path));Assert.IsFalse(File.Exists(owned.Unselected.Path));Assert.IsFalse(File.Exists(owned.Selected.SnapshotPath));
        var recovered=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget)
            .Supersession(owned.Unselected.Capture.Scope.GenerationId),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Superseded,recovered);Assert.IsNull(actual.Plan);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentOwnedLineageBasisRejectsAnotherReaderAndAnInvalidatedNonceWithoutChangingTheNewCapture()
    {
        await using var owned=await OwnedLineageFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Selected.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.CaptureCurrentPublicationBasisAsync(
            inventory,owned.Next.Capture.Scope.GenerationId,owned.NextBasis,CancellationToken.None)).ConfigureAwait(false);
        _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.CaptureCurrentPublicationBasisAsync(
            inventory,owned.Next.Capture.Scope.GenerationId,owned.NextBasis,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(owned.Next.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Next.Capture.Scope.GenerationId).ConfigureAwait(false));
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Next.Path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentOwnedLineageBasisRejectsSubstitutedNativeSequenceOrReferenceAndPreservesTheSameRecoverableWork(bool wrongHash)
    {
        await using var owned=await OwnedLineageFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Selected.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var bad=owned.NextBasis with {Parent=wrongHash ? owned.Superseded.OriginalCommit with {Sha256=new string('f',64)} : owned.Head.Commit};
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Reader.CaptureCurrentPublicationBasisAsync(
            inventory,owned.Next.Capture.Scope.GenerationId,bad,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(owned.Next.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Next.Capture.Scope.GenerationId).ConfigureAwait(false));
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Next.Path));
        var valid=await fixture.Reader.CaptureCurrentPublicationBasisAsync(inventory,owned.Next.Capture.Scope.GenerationId,owned.NextBasis,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.NextBasis,valid.Basis);
    }

    private sealed record LineageFrozenCapture(CatalogGenerationCapture Capture,AcceptedSourceConfiguration Approved,string Path);
    private sealed class OwnedLineageFixture(CurrentCompletionFixture selected,PublishedCatalogHead head,LineageFrozenCapture unselected,
        CurrentCatalogGenerationSupersession superseded,LineageFrozenCapture next,LibraryPolicyReference sources) : IAsyncDisposable
    {
        internal CurrentCompletionFixture Selected {get;}=selected;
        internal PublishedCatalogHead Head {get;}=head;
        internal LineageFrozenCapture Unselected {get;}=unselected;
        internal CurrentCatalogGenerationSupersession Superseded {get;}=superseded;
        internal LineageFrozenCapture Next {get;}=next;
        internal LibraryPolicyReference Sources {get;}=sources;
        internal CapturedCatalogBasis NextBasis=>new(Next.Capture.Snapshot!,Superseded.OriginalCommit,Superseded.Sequence,Next.Approved.EnvelopeSha256,0,1);
        internal static async Task<OwnedLineageFixture> CreateAsync()
        {
            var selected=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=selected.Fixture;
            try
            {
                await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
                var head=await completion.FinishAsync(selected.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
                var second=await CreateLineageFrozenCaptureAsync(fixture).ConfigureAwait(false);
                var basis=new CapturedCatalogBasis(second.Capture.Snapshot!,head.Commit,head.Sequence,second.Approved.EnvelopeSha256,0,1);
                await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget)
                    .CapturePublicationBasis(second.Capture.Scope.GenerationId,basis),CancellationToken.None).ConfigureAwait(false);
                var intent=await BuildLineagePublicationAsync(fixture,second,basis).ConfigureAwait(false);
                var root=await PrepareTransitionAsync(fixture,intent.Id).ConfigureAwait(false);
                await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
                {
                    var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
                    sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;
                },CancellationToken.None).ConfigureAwait(false);
                _=await fixture.Reader.WithdrawTransitionAsync(root.Id,CurrentControlTransitionWithdrawalReason.ConfigurationChanged,CancellationToken.None).ConfigureAwait(false);
                var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
                var superseded=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,second.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
                await using var retirement=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
                Assert.AreEqual(superseded,await retirement.FinishAsync(second.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
                Assert.AreEqual(head,await LineageHeadAsync(fixture).ConfigureAwait(false));
                var sources=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
                var next=await CreateLineageFrozenCaptureAsync(fixture).ConfigureAwait(false);
                return new(selected,head,second,superseded,next,sources);
            }
            catch {await selected.DisposeAsync().ConfigureAwait(false);throw;}
        }
        public ValueTask DisposeAsync()=>Selected.DisposeAsync();
    }

    private static Task<PublishedCatalogHead?> LineageHeadAsync(CurrentReaderFixture fixture)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(
        database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).PublishedHead(),CancellationToken.None);
    private static async Task<LocalCurrentWorkspaceInventory> FreshLineageInventoryAsync(CurrentReaderFixture fixture)
    {
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        return await fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
    }
    private static Task<LineageFrozenCapture> CreateLineageFrozenCaptureAsync(CurrentReaderFixture fixture)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
    {
        var authority=new LibraryAuthorityRepository(database,fixture.Data.Budget);var local=authority.CaptureLocalPublicationAuthority();
        var approved=authority.CurrentSourceConfiguration()!;
        var scope=new WorkspacePublicationScope(local.LibraryId,local.ActorId,local.ActorEpoch,Guid.NewGuid(),local.AccountId,local.AccountRevision,
            local.FolderId,local.ConfigurationRevision,local.AuthoritySha256);
        var journal=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget);var capture=journal.Begin(scope,Guid.NewGuid(),Guid.NewGuid());
        using(var preparation=new CatalogPreparationDirectory(database.StateDirectory,database.InstallationId))
        using(var frozen=FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(database),preparation,fixture.Data.Budget))
        using(var store=new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget))
            capture=journal.SealSnapshot(scope.GenerationId,store.Save(frozen,capture,CancellationToken.None));
        return new LineageFrozenCapture(capture,approved,Path.Combine(database.StateDirectory,"catalog-generation-snapshots","snapshot-"+capture.SnapshotId.ToString("N")+".sqlite"));
    },CancellationToken.None);

    private static async Task<ProtocolSigningIntent> BuildLineagePublicationAsync(CurrentReaderFixture fixture,LineageFrozenCapture owned,CapturedCatalogBasis basis)
    {
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var store=new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget);
            try {return (Store:store,Frozen:store.Open(new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Read(owned.Capture.Scope.GenerationId)!,CancellationToken.None));}
            catch {store.Dispose();throw;}
        },CancellationToken.None).ConfigureAwait(false);
        PreparedCatalogDependencies dependencies;
        using(retained.Store) using(retained.Frozen)
            dependencies=await new CatalogGenerationDependencyBuilder(new(owned.Capture.Scope.AccountId,owned.Capture.Scope.FolderId),
                new CurrentCompletionFixtureSink(fixture,owned.Capture.Scope)).BuildAsync(retained.Frozen,owned.Approved,null,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).SealDependencies(owned.Capture.Scope.GenerationId,new(basis,dependencies.RootIndex,dependencies.SourceCoverage));return true;},CancellationToken.None).ConfigureAwait(false);
        var plan=new CatalogCommitPlan(owned.Capture.Scope,dependencies,owned.Approved,basis.Parent,basis.SettingsRevision,basis.MinimumHistoryGeneration);
        var writer=new CatalogCommitDocumentWriter(plan);
        var intent=await new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget)
            .SignCatalogAsync(owned.Capture.CommitIntentId,writer.Encode,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).ConfirmCommitSealed(owned.Capture.Scope.GenerationId);return true;},CancellationToken.None).ConfigureAwait(false);
        await new CurrentCompletionFixtureSink(fixture,owned.Capture.Scope).StoreCommitAsync(intent).ConfigureAwait(false);return intent;
    }

    private static async Task<LibraryPolicyReference> SignLineageCurrentSourcesAsync(CurrentReaderFixture fixture)
    {
        var values=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var initial=new LibraryAuthorityRepository(database,fixture.Data.Budget).InitialPolicyPlan()!;
            return (Snapshot:new LibrarySourceRepository(database).Snapshot(),Body:new ProtocolSigningJournal(database,fixture.Data.Budget).Read(initial.SourceIntentId)!.Body.ToArray());
        },CancellationToken.None).ConfigureAwait(false);
        var policy=await new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget)
            .SignControllerAsync(Guid.NewGuid(),"library-source-configuration",(_,sequence)=>
            {
                var body=JsonNode.Parse(values.Body)!.AsObject();body["sequence"]=sequence.ToString(CultureInfo.InvariantCulture);
                body["payload"]!["configurationRevision"]=values.Snapshot.Revision.ToString(CultureInfo.InvariantCulture);
                var source=values.Snapshot.Sources.Single();var item=body["payload"]!["sources"]!.AsArray().Single()!;
                item["revision"]=source.Revision.ToString(CultureInfo.InvariantCulture);item["enabled"]=source.Enabled;item["includeDescendants"]=source.IncludeDescendants;
                return new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(body));
            },CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {new LibraryAuthorityRepository(database,fixture.Data.Budget).SelectSourceConfiguration(policy.Envelope!.Value.Span);return true;},CancellationToken.None).ConfigureAwait(false);
        var bytes=policy.Envelope!.Value.ToArray();var file="owned-lineage-sources-"+policy.Sequence.ToString(CultureInfo.InvariantCulture);
        fixture.Data.Cloud[file]=new("permission-A",file,"metadata",8,bytes);fixture.Data.MetadataMimeTypes[file]="application/json";
        return new(new("permission-A",file,"metadata",bytes.Length,policy.EnvelopeSha256!),policy.Sequence);
    }
}
