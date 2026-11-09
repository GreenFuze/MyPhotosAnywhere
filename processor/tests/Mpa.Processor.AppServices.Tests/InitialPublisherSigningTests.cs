using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class JournaledProtocolSignerTests
{
    [TestMethod]
    public async Task PreparedInitialPoliciesUseTheirFixedNativeJournaledIntentsAndReplayExactly()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var plan = await fixture.Signer.PrepareInitialPolicyPlanAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(plan, await fixture.Signer.PrepareInitialPolicyPlanAsync(CancellationToken.None).ConfigureAwait(false));
        var publisher = await fixture.Signer.SignInitialPublisherAuthorizationAsync(plan.PublisherIntentId, CancellationToken.None).ConfigureAwait(false);
        var sources = await fixture.Signer.SignInitialSourceConfigurationAsync(plan.SourceIntentId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, publisher.Sequence); Assert.AreEqual(2L, sources.Sequence);
        var observer = PolicyObserver(fixture); await observer.ObserveAsync(PolicyRead(publisher), CancellationToken.None).ConfigureAwait(false); await observer.ObserveAsync(PolicyRead(sources), CancellationToken.None).ConfigureAwait(false);
        var authority = await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).CaptureLocalPublicationAuthority(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(plan.PublisherId, authority.ActorId); Assert.AreEqual(plan.ConfigurationRevision, authority.ConfigurationRevision);
        CollectionAssert.AreEqual(publisher.Envelope!.Value.ToArray(), (await fixture.Signer.SignInitialPublisherAuthorizationAsync(plan.PublisherIntentId, CancellationToken.None).ConfigureAwait(false)).Envelope!.Value.ToArray());
        CollectionAssert.AreEqual(sources.Envelope!.Value.ToArray(), (await fixture.Signer.SignInitialSourceConfigurationAsync(plan.SourceIntentId, CancellationToken.None).ConfigureAwait(false)).Envelope!.Value.ToArray());
        var wrong = Guid.NewGuid(); await Assert.ThrowsExactlyAsync<LibraryInitialPolicyConflictException>(() => fixture.Signer.SignInitialSourceConfigurationAsync(wrong, CancellationToken.None)).ConfigureAwait(false); Assert.IsNull(await fixture.ReadAsync(wrong).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InitialPublisherNeedsPreparedHumanApprovalAndNeverReactivatesAfterRevocation()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var arbitrary = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Signer.PrepareInitialPolicyPlanAsync(CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Signer.SignInitialPublisherAuthorizationAsync(arbitrary, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.ReadAsync(arbitrary).ConfigureAwait(false)); await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var plan = await fixture.Signer.PrepareInitialPolicyPlanAsync(CancellationToken.None).ConfigureAwait(false);
        var publisher = await fixture.Signer.SignInitialPublisherAuthorizationAsync(plan.PublisherIntentId, CancellationToken.None).ConfigureAwait(false);
        await PolicyObserver(fixture).ObserveAsync(PolicyRead(publisher), CancellationToken.None).ConfigureAwait(false);
        var revoked = await fixture.Signer.SignControllerAsync(Guid.NewGuid(), "library-publisher-authorization", (identity, sequence) => ObservedPublisherBody(identity, sequence, true), CancellationToken.None).ConfigureAwait(false);
        await PolicyObserver(fixture).ObserveAsync(PolicyRead(revoked), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<LibraryInitialPolicyConflictException>(() => fixture.Signer.SignInitialPublisherAuthorizationAsync(plan.PublisherIntentId, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<LibraryInitialPolicyConflictException>(() => fixture.Signer.SignInitialSourceConfigurationAsync(plan.SourceIntentId, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.ReadAsync(plan.SourceIntentId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ChangedHumanSourceApprovalBlocksPublisherBeforeCounterReservation()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var plan = await fixture.Signer.PrepareInitialPolicyPlanAsync(CancellationToken.None).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(database => new LibrarySourceRepository(database).Add(Guid.NewGuid(), "account", 1, "photos", "New human folder", true, 1), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => fixture.Signer.SignInitialPublisherAuthorizationAsync(plan.PublisherIntentId, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.ReadAsync(plan.PublisherIntentId).ConfigureAwait(false));
        Assert.AreEqual(plan, await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).InitialPolicyPlan(), CancellationToken.None).ConfigureAwait(false));
    }
}
