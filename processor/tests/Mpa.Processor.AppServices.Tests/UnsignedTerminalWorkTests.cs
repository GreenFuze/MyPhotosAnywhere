using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentUnsignedTerminalRejectsLateAllocationBeforeItsFrozenOriginalWorkCanChange()
    {
        await using var owned=await UnsignedGenerationFixture.CreateAsync(0).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeUnsignedGenerationAsync(inventory,owned.Partial.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(owned.Work.FileId);await AssertUnsignedOriginalAsync(owned).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new WorkspacePublicationRepository(database,fixture.Data.Budget).AssignFileId(owned.Work.Id,owned.Work.Sha256,"late-unsigned-returned-id"),CancellationToken.None)).ConfigureAwait(false);
        await AssertUnsignedOriginalAsync(owned).ConfigureAwait(false);
        Assert.AreEqual(receipt,await ReadUnsignedDispositionAsync(owned).ConfigureAwait(false));
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
