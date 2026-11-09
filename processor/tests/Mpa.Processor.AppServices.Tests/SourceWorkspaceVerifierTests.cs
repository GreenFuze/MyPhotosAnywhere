using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task SourceAncestryRejectsWorkspaceDescendantsAndAllowsUnrelatedFolders()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false); await PinDiscoveryWorkspaceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.FolderRead = (id, _) => Task.FromResult(new DriveFolder(id, "Folder", true, id == "middle" ? ["metadata-workspace"] : [], null));
        await Assert.ThrowsExactlyAsync<ManagedWorkspaceSourceException>(() => VerifySourceAsync(fixture, new("source", "Photos", true, ["middle"], null))).ConfigureAwait(false);
        await VerifySourceAsync(fixture, new("unrelated", "metadata-workspace", true, ["another-root"], null)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SourceAncestryNeverTreatsCyclesWrongIdentitiesOrExcessiveDepthAsVerified()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false); await PinDiscoveryWorkspaceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.FolderRead = (id, _) => Task.FromResult(new DriveFolder(id, "Folder", true, ["source"], null));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VerifySourceAsync(fixture, new("source", "Photos", true, ["parent"], null))).ConfigureAwait(false);
        fixture.Readers.FolderRead = (_, _) => Task.FromResult(new DriveFolder("different", "Folder", true, [], null));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VerifySourceAsync(fixture, new("source", "Photos", true, ["parent"], null))).ConfigureAwait(false);
        var depth = 0;
        fixture.Readers.FolderRead = (id, _) => Task.FromResult(new DriveFolder(id, "Folder", true, ["ancestor-" + (++depth).ToString(System.Globalization.CultureInfo.InvariantCulture)], null));
        await Assert.ThrowsExactlyAsync<SourceWorkspaceLimitException>(() => VerifySourceAsync(fixture, new("source", "Photos", true, ["parent"], null))).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task NoWorkspaceNeedsNoAncestorReadAndCanceledVerificationJoinsItsActualRead()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.FolderRead = (_, _) => throw new InvalidOperationException("No parent read is allowed before a workspace is configured.");
        await VerifySourceAsync(fixture, new("source", "Photos", true, ["parent"], null)).ConfigureAwait(false);
        await PinDiscoveryWorkspaceAsync(fixture).ConfigureAwait(false); using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Readers.FolderRead = async (id, _) => { entered.TrySetResult(); await release.Task.ConfigureAwait(false); return new(id, "Folder", true, [], null); };
        var checking = VerifySourceAsync(fixture, new("source", "Photos", true, ["parent"], null), caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); await caller.CancelAsync().ConfigureAwait(false);
        try { Assert.IsFalse(checking.IsCompleted); } finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => checking).ConfigureAwait(false);
    }

    private static async Task VerifySourceAsync(Fixture fixture, DriveFolder folder, CancellationToken token = default)
    {
        var boundary = await fixture.State.ExecuteAsync(ManagedWorkspaceBoundary.Capture, token).ConfigureAwait(false);
        await fixture.Service.ReadDriveAsync("permission-A", async (reader, _, readToken) =>
        { await new SourceWorkspaceVerifier(reader, boundary).AssertOutsideAsync(folder, readToken).ConfigureAwait(false); return true; }, token).ConfigureAwait(false);
    }
}
