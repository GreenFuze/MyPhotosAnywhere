using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void AuthorityReaderRequiresNoStorageBudgetAndCannotMutateTrust()
    {
        using var fixture = new Fixture(); var reader = new LibraryAuthorityRepository(fixture.Database);
        Assert.IsNull(reader.Controller());
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.Bootstrap(fixture.ActorId, 1, Fixture.Workspace, 1, fixture.Controller.ExportSubjectPublicKeyInfo()));
        var pin = fixture.Bootstrap(new(1, false)); Assert.AreEqual(pin, reader.Controller());
        Assert.AreEqual(fixture.Store.BootstrapSources(), reader.AssertBootstrapSourcesCurrent());
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.SelectPublisherPolicy(fixture.Policy(1)));
    }

    [TestMethod]
    public void WorkspaceBoundaryUsesExactPinnedIdAndRechecksLaterBootstrap()
    {
        using var fixture = new Fixture(); var before = ManagedWorkspaceBoundary.Capture(fixture.Database);
        Assert.IsFalse(before.ContainsRoot(Fixture.Workspace.FolderId)); fixture.Bootstrap();
        Assert.IsFalse(before.ContainsRoot(Fixture.Workspace.FolderId)); var current = ManagedWorkspaceBoundary.Capture(fixture.Database);
        Assert.IsTrue(current.ContainsRoot(Fixture.Workspace.FolderId)); Assert.IsFalse(current.ContainsRoot("metadata-workspace-copy"));
        Assert.ThrowsExactly<ManagedWorkspaceSourceException>(() => current.AssertSourceRoot(Fixture.Workspace.FolderId));
    }

    [TestMethod]
    public void ReservedWorkspaceCannotBeAddedThroughAnotherAccountAndNamesDoNotHideOtherFolders()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var accounts = new DriveAccountRepository(fixture.Database);
        var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account-B", "Other account", null);
        var sources = new LibrarySourceRepository(fixture.Database);
        foreach (var account in new[] { "account-A", "account-B" })
            Assert.ThrowsExactly<ManagedWorkspaceSourceException>(() => sources.Add(Guid.NewGuid(), account, 1, Fixture.Workspace.FolderId, "Photos", true, 1));
        Assert.AreEqual(1L, sources.Snapshot().Revision); Assert.AreEqual(0, sources.Snapshot().Sources.Count);
        Assert.AreEqual(1, sources.Add(Guid.NewGuid(), "account-A", 1, "ordinary-photos", "metadata-workspace", true, 1).Sources.Count);
    }
}
