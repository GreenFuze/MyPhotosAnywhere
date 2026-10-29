using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CanonicalViewerCompositionUsesTheActualRuntimeSourceAndRejectsForeignOwnersBeforeCapture()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var selection=(await SelectCanonicalAccountFixtureAsync(fixture,runtime).ConfigureAwait(false)).Selection;
        var page=await runtime.ReadPageAsync(selection.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);var asset=page.Page.Assets.Single();
        var state=fixture.Data.Setup.Accounts.State;var location=await state.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        var source=new CurrentCanonicalViewerSource(runtime);Assert.IsTrue(source.Borrows(state,fixture.Data.Budget));var foreign=new StorageBudgetManager(new CanonicalViewerCallbackSpace());Assert.IsFalse(source.Borrows(state,foreign));
        Assert.ThrowsExactly<ArgumentException>(()=>new CatalogViewerSessions(state,location.StateDirectory,location.InstallationId,foreign,canonical:source));
        var calls=(fixture.Http.MetadataReads,fixture.Http.BodyReads,fixture.Http.Puts);var reserved=fixture.Data.Budget.Status().ReservedBytes;
        await using var viewers=new CatalogViewerSessions(state,location.StateDirectory,location.InstallationId,fixture.Data.Budget,canonical:source);
        var browser=Guid.NewGuid();var request=new CatalogViewerRequest(Guid.NewGuid(),asset.Id,asset.AccountId,selection.StageId);
        var window=await viewers.OpenAsync(browser,request,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(selection.StageId,window.StageId);Assert.AreEqual(asset.AccountId,window.AccountId);
        Assert.AreEqual(1L,window.Count);Assert.AreEqual(asset.Id,window.Entries.Single().AssetId);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>viewers.OpenAsync(browser,request with {Id=Guid.NewGuid(),StageId=Guid.NewGuid()},CancellationToken.None)).ConfigureAwait(false);
        await viewers.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(viewers.ExitConfirmed);Assert.AreEqual(reserved,fixture.Data.Budget.Status().ReservedBytes);
        Assert.AreEqual(calls,(fixture.Http.MetadataReads,fixture.Http.BodyReads,fixture.Http.Puts));Assert.AreEqual(selection,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }
}
