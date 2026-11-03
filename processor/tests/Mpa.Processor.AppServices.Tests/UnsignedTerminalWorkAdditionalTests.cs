using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CurrentUnsignedTerminalExcludesOnlyValidatedOriginalWorkAndRejectsAllFurtherMutations(int materialization)
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(materialization).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var snapshot=await File.ReadAllBytesAsync(owned.Partial.Path).ConfigureAwait(false);
        var calls=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var outbox=new WorkspacePublicationRepository(database,fixture.Data.Budget);
            Assert.IsFalse(outbox.Pending(1).Contains(owned.Work.Id));
            Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(()=>outbox.AssertCurrent(owned.Work.Id));
            Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(()=>outbox.AssignFileId(owned.Work.Id,owned.Work.Sha256,owned.Work.FileId??"late-unsigned-id"));
            if(owned.Work.FileId is not null)
                Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(()=>outbox.ConfirmVerified(owned.Work.Id,owned.Work.Scope.AccountId,
                    owned.Work.Scope.FolderId,owned.Work.FileId,owned.Work.Sha256,owned.Work.Payload.Length,9));
            Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(()=>outbox.Prepare(Guid.NewGuid(),owned.Work.Scope,
                owned.Work.Role,owned.Work.MimeType,owned.Work.Payload.Span));
            // An exact idempotent read returns historical bytes, without a write grant.
            var historical=outbox.Prepare(owned.Work.Id,owned.Work.Scope,owned.Work.Role,owned.Work.MimeType,owned.Work.Payload.Span);
            Assert.AreEqual(owned.Work.Scope,historical.Scope);Assert.AreEqual(owned.Work.FileId,historical.FileId);
            Assert.AreEqual(owned.Work.VerifiedVersion,historical.VerifiedVersion);return true;
        },CancellationToken.None).ConfigureAwait(false);
        await AssertUnsignedOriginalAsync(owned).ConfigureAwait(false);Assert.AreEqual(receipt,await ReadUnsignedDispositionAsync(owned).ConfigureAwait(false));
        CollectionAssert.AreEqual(snapshot,await File.ReadAllBytesAsync(owned.Partial.Path).ConfigureAwait(false));
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(calls,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentUnsignedTerminalCannotHideAnIncompleteDispositionOrChangedOriginalFromPendingEnumeration(bool changeOriginal)
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(0).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        _=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            using(var connection=OpenCurrentCompletionDatabase(database))
            using(var command=connection.CreateCommand())
            {
                command.CommandText=changeOriginal?"UPDATE workspace_publication SET file_id='corrupted-terminal-id' WHERE id=$id"
                    :"DELETE FROM processor_meta WHERE key=$key";
                if(changeOriginal) command.Parameters.AddWithValue("$id",owned.Work.Id.ToString("D"));
                else command.Parameters.AddWithValue("$key","current_unsigned_generation_disposition_v1:"+owned.Work.Scope.GenerationId.ToString("D")+":sha256");
                Assert.AreEqual(1,command.ExecuteNonQuery());
            }
            var outbox=new WorkspacePublicationRepository(database,fixture.Data.Budget);
            Assert.ThrowsExactly<InvalidDataException>(()=>outbox.Pending());
            Assert.ThrowsExactly<InvalidDataException>(()=>outbox.AssignFileId(owned.Work.Id,owned.Work.Sha256,"late-unsigned-id"));
            return true;
        },CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(owned.Partial.Path));Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentUnsignedTerminalAllowsTheActualNextCurrentCaptureAndBasisWithoutReallocatingDisposedWork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await AddCurrentSnapshotFactAsync(fixture,"unsigned-original").ConfigureAwait(false);
        CurrentPreparedCatalogBasis original;await using(var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false))
            original=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var work=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var outbox=new WorkspacePublicationRepository(database,fixture.Data.Budget);var scope=original.Publication.Capture.Scope;
            var row=outbox.Prepare(Guid.NewGuid(),scope,"coverage-root","application/json",new CatalogCoverageDocumentWriter(new(scope.AccountId,scope.FolderId))
                .EncodeRoot(scope.LibraryId,scope.ConfigurationRevision,1,0,[]));
            var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
            sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return row;
        },CancellationToken.None).ConfigureAwait(false);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,work.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        await using(var retirement=new CurrentUnsignedGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
            Assert.AreEqual(receipt,await retirement.FinishAsync(work.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
        var sourcesPolicy=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
        var current=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(current,CancellationToken.None).ConfigureAwait(false);
        var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);
        var fence=await fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None).ConfigureAwait(false);
        var transition=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget)
            .Prepare(Guid.NewGuid(),fence.Current.Attempt,new(current.Head.Publishers,sourcesPolicy,null)),CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(transition.Id,CancellationToken.None).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var reconciled=await PrepareReconciledGraphAsync(fixture,pool).ConfigureAwait(false);
        await using(reconciled.Stage)
        {
            var observed=await fixture.Reader.ObserveSelectedInventoryAsync(reconciled.Graph,CancellationToken.None).ConfigureAwait(false);
            _=await fixture.Reader.ActivateReconciledAsync(observed,reconciled.Stage,CancellationToken.None).ConfigureAwait(false);
        }
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var calls=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        await using var next=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);var prepared=await next.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(work.Scope.GenerationId,prepared.Publication.Capture.Scope.GenerationId);
        Assert.AreEqual("publication-planned",prepared.Publication.Capture.Phase);Assert.IsNull(prepared.Publication.Basis.Parent);
        Assert.AreEqual(0L,prepared.Publication.Basis.ParentSequence);Assert.AreEqual(sourcesPolicy.Reference.Sha256,prepared.Sources.EnvelopeSha256);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);
        Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var outbox=new WorkspacePublicationRepository(database,fixture.Data.Budget);Assert.IsFalse(outbox.Pending().Contains(work.Id));
            var retained=outbox.Read(work.Id)!;Assert.AreEqual(work.Scope,retained.Scope);Assert.IsNull(retained.FileId);Assert.IsNull(retained.VerifiedVersion);
            CollectionAssert.AreEqual(work.Payload.ToArray(),retained.Payload.ToArray());
            Assert.AreEqual(receipt,new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).UnsignedDisposition(work.Scope.GenerationId));return true;
        },CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(calls,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));Assert.IsNull(await LineageHeadAsync(fixture).ConfigureAwait(false));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("unsigned-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
