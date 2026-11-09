using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class JournaledProtocolSignerTests
{
    private static readonly string[] InitialApprovedAccounts = ["account"];
    [TestMethod]
    public async Task InitialSourceSigningRequiresActualApprovalAndRetainsItsExactEnvelopeAcrossDisconnect()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var id = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Signer.SignInitialSourceConfigurationAsync(id, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.ReadAsync(id).ConfigureAwait(false)); await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var first = await fixture.Signer.SignInitialSourceConfigurationAsync(id, CancellationToken.None).ConfigureAwait(false);
        var accepted = await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget)
            .SelectSourceConfiguration(first.Envelope!.Value.Span), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, accepted.Sequence); Assert.AreEqual(1L, accepted.Configuration.Revision);
        Assert.AreEqual(0, accepted.Configuration.Sources.Count); CollectionAssert.AreEqual(InitialApprovedAccounts, accepted.Configuration.Accounts.ToArray());
        await fixture.State.ExecuteAsync(database => { new DriveAccountRepository(database).Disconnect("account", 1); return true; }, CancellationToken.None).ConfigureAwait(false);
        var replay = await fixture.Signer.SignInitialSourceConfigurationAsync(id, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(first.Envelope!.Value.ToArray(), replay.Envelope!.Value.ToArray()); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task ChangedSourcesCannotBeSignedOrReplayedAsTheInitialApproval()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var snapshot = await fixture.State.ExecuteAsync(database => new LibrarySourceRepository(database)
            .Add(Guid.NewGuid(), "account", 1, "photos", "Human folder label", true, 1), CancellationToken.None).ConfigureAwait(false);
        await fixture.BootstrapAsync(new(snapshot.Revision, false)).ConfigureAwait(false); var id = Guid.NewGuid();
        var first = await fixture.Signer.SignInitialSourceConfigurationAsync(id, CancellationToken.None).ConfigureAwait(false);
        var accepted = await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget)
            .SelectSourceConfiguration(first.Envelope!.Value.Span), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("photos", accepted.Configuration.Sources.Single().FolderId);
        await fixture.State.ExecuteAsync(database => new LibrarySourceRepository(database)
            .Update(snapshot.Sources.Single().Id, false, true, 1, snapshot.Revision), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => fixture.Signer.SignInitialSourceConfigurationAsync(id, CancellationToken.None)).ConfigureAwait(false);
        var newId = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => fixture.Signer.SignInitialSourceConfigurationAsync(newId, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.ReadAsync(newId).ConfigureAwait(false));
        CollectionAssert.AreEqual(first.Envelope!.Value.ToArray(), (await fixture.ReadAsync(id).ConfigureAwait(false))!.Envelope!.Value.ToArray());
    }
}
