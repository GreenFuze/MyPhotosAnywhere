using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void LocalFenceBindsCurrentPoliciesAndEveryCapturedScopeFieldWithoutWriting()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var current = fixture.Store.CaptureLocalPublicationAuthority();
        var scope = Scope(current); fixture.Store.AssertLocalPublicationScope(scope); fixture.Space.Available = 1;
        Assert.AreEqual(current, new LibraryAuthorityRepository(fixture.Database).CaptureLocalPublicationAuthority()); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        foreach (var changed in new[] { scope with { LibraryId = Guid.NewGuid() }, scope with { ActorId = Guid.NewGuid() }, scope with { ActorEpoch = 2 },
            scope with { AccountId = "different" }, scope with { AccountRevision = 2 }, scope with { FolderId = "different" },
            scope with { ConfigurationRevision = 3 }, scope with { AuthoritySha256 = new string('0', 64) }, scope with { GenerationId = Guid.Empty } })
            Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.AssertLocalPublicationScope(changed));
    }

    [TestMethod]
    public void MissingOrRevokedPublisherCannotUseAnEarlierLocalScope()
    {
        using var fixture = new Fixture(); Assert.ThrowsExactly<CatalogPublicationNotAuthorizedException>(() => fixture.Store.CaptureLocalPublicationAuthority());
        fixture.Bootstrap(); Assert.ThrowsExactly<CatalogPublicationNotAuthorizedException>(() => fixture.Store.CaptureLocalPublicationAuthority());
        fixture.PrepareLocalPublication(); var scope = Scope(fixture.Store.CaptureLocalPublicationAuthority());
        fixture.Store.SelectPublisherPolicy(fixture.Policy(3, revoked: true));
        Assert.ThrowsExactly<CatalogPublicationNotAuthorizedException>(() => fixture.Store.AssertLocalPublicationScope(scope));
    }

    [TestMethod]
    public void SourceChangesPauseLocalAdmissionUntilTheirExactSignedPolicyIsSelected()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var before = fixture.Store.CaptureLocalPublicationAuthority();
        fixture.DisablePublicationSource(); Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.CaptureLocalPublicationAuthority());
        fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(4, 3, enabled: false, sourceRevision: 2));
        var after = fixture.Store.CaptureLocalPublicationAuthority(); Assert.AreNotEqual(before.AuthoritySha256, after.AuthoritySha256);
        Assert.AreEqual(3L, after.ConfigurationRevision); Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.AssertLocalPublicationScope(Scope(before)));
        fixture.Store.AssertLocalPublicationScope(Scope(after));
    }

    [TestMethod]
    public void ReconnectionRequiresNewAccessScopeAndPreservesTheHumanAuthorityDigest()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var before = fixture.Store.CaptureLocalPublicationAuthority();
        var accounts = new DriveAccountRepository(fixture.Database); accounts.Disconnect("account-A", 1);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.CaptureLocalPublicationAuthority());
        var pending = accounts.Begin(Guid.NewGuid(), "account-A"); var connected = accounts.Promote(pending.Id, "account-A", "Reconnected", null);
        var after = fixture.Store.CaptureLocalPublicationAuthority(); Assert.AreEqual(before.AuthoritySha256, after.AuthoritySha256);
        Assert.AreEqual(connected.Revision, after.AccountRevision); Assert.AreNotEqual(before.AccountRevision, after.AccountRevision);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.AssertLocalPublicationScope(Scope(before)));
        fixture.Store.AssertLocalPublicationScope(Scope(after));
    }

    [TestMethod]
    public void DamagedSourceProofCannotBecomeAReplacementAuthorityDigest()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var scope = Scope(fixture.Store.CaptureLocalPublicationAuthority());
        fixture.Sql("UPDATE processor_meta SET value='corrupt' WHERE key='controller_source_document_v1:2:sha256'");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.AssertLocalPublicationScope(scope));
    }

    private static WorkspacePublicationScope Scope(LocalCatalogPublicationAuthority current) => new(current.LibraryId, current.ActorId, current.ActorEpoch,
        Guid.NewGuid(), current.AccountId, current.AccountRevision, current.FolderId, current.ConfigurationRevision, current.AuthoritySha256);
    private sealed partial class Fixture
    {
        public void PrepareLocalPublication()
        {
            var sources = new LibrarySourceRepository(Database); sources.Add(SourceIdentity, "account-A", 1, "photo-folder", "Photos", true, 1);
            Bootstrap(new(2, false)); Store.SelectPublisherPolicy(Policy(1)); Store.SelectSourceConfiguration(SourcePolicy(2, 2, sourceRevision: 1));
        }
        public void DisablePublicationSource() => new LibrarySourceRepository(Database).Update(SourceIdentity, false, true, 1, 2);
    }
}
