using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentDependencySelectedNativeBasisRemainsTheExactOriginalParentDuringLocalPreparation()
    {
        await using var selected=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=selected.Fixture;
        PublishedCatalogHead head;
        await using(var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
            head=await completion.FinishAsync(selected.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        CurrentPreparedCatalogBasis basis;
        await using(var preparation=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false))
            basis=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(head.Commit,basis.Publication.Basis.Parent);Assert.AreEqual(head.Sequence,basis.Publication.Basis.ParentSequence);
        var scope=basis.Publication.Capture.Scope;
        var payload=new CatalogCoverageDocumentWriter(new(scope.AccountId,scope.FolderId)).EncodeRoot(scope.LibraryId,scope.ConfigurationRevision,1,0,[]);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var writes=(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts);
        var path=await CurrentSnapshotPathAsync(fixture,basis.Publication.Capture).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var result=await fixture.Reader.PrepareCurrentDependencyAsync(scope.GenerationId,"coverage-root","application/json",payload,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(scope,result.Scope);Assert.IsNull(result.FileId);Assert.IsNull(result.VerifiedVersion);CollectionAssert.AreEqual(payload,result.Payload.ToArray());
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(before.Captures,after.Captures);
        Assert.AreEqual(head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget)
            .Publication(scope.GenerationId),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(basis.Publication,retained);CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        Assert.AreEqual(writes,(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts));Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
