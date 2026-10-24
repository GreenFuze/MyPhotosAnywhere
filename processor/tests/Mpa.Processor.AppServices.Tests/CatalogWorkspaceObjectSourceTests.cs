using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly byte[] WorkspacePayload = "{}"u8.ToArray();
    private static readonly CatalogObjectReference WorkspaceReference = new("permission-A", "object-A", "workspace-A", 2,
        "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a");
    private static readonly CatalogObjectReference[] InvalidWorkspaceReferences =
    [
        WorkspaceReference with { AccountId = "foreign" }, WorkspaceReference with { WorkspaceId = "foreign" },
        WorkspaceReference with { FileId = "https://example.invalid/object" }, WorkspaceReference with { FileId = "workspace-A" },
        WorkspaceReference with { FileId = "root" }, WorkspaceReference with { Bytes = 0 },
        WorkspaceReference with { Bytes = GoogleAccountConnection.MaximumWorkspaceObjectBytes + 1L },
        WorkspaceReference with { Sha256 = "short" }, WorkspaceReference with { Sha256 = new string('A', 64) }
    ];

    [TestMethod]
    public async Task CatalogWorkspaceReadsReuseTheExistingCapturedAccountOwner()
    {
        await using var fixture = new Fixture();
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        var source = WorkspaceSource(fixture, account.Revision);
        var calls = 0;
        fixture.Readers.WorkspaceRead = (folder, file, bytes, digest, token) =>
        {
            token.ThrowIfCancellationRequested(); calls++;
            Assert.AreEqual(WorkspaceReference.WorkspaceId, folder); Assert.AreEqual(WorkspaceReference.FileId, file);
            Assert.AreEqual(WorkspaceReference.Bytes, bytes); Assert.AreEqual(WorkspaceReference.Sha256, digest);
            return Task.FromResult(WorkspaceResult());
        };
        CollectionAssert.AreEqual(WorkspacePayload, (await source.ReadAsync(WorkspaceReference, CancellationToken.None).ConfigureAwait(false)).ToArray());
        await source.ReadAsync(WorkspaceReference, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, calls); Assert.AreEqual(1, fixture.Readers.Opened.Count);
        Assert.AreEqual(account.ConnectionId, fixture.Readers.Opened.Single());
    }

    [TestMethod]
    public async Task ForeignOrUnboundedCatalogReferencesFailBeforeBorrowingAnyCredentialOwner()
    {
        await using var fixture = new Fixture();
        var source = WorkspaceSource(fixture, 1);
        foreach (var reference in InvalidWorkspaceReferences)
            await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => source.ReadAsync(reference, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Readers.Opened.Count);
    }

    [TestMethod]
    public async Task ReconnectionBeforeCatalogReadRejectsTheCapturedAccountRevision()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        var source = WorkspaceSource(fixture, first.Revision);
        await fixture.Service.ConnectAsync(first.PermissionId, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        // WorkspaceRead remains unset: any Drive object read would fail this test.
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => source.ReadAsync(WorkspaceReference, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LateDisconnectedCatalogReadCannotReturnBytesAndCleanupJoinsItsOwner()
    {
        await using var fixture = new Fixture();
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        HoldWorkspaceRead(fixture);
        var reading = WorkspaceSource(fixture, account.Revision).ReadAsync(WorkspaceReference, CancellationToken.None);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await fixture.Service.DisconnectAsync(account.PermissionId, account.Revision, CancellationToken.None).ConfigureAwait(false);
        var cleanup = fixture.Service.CleanCredentialsAsync(CancellationToken.None);
        try { Assert.IsFalse(cleanup.IsCompleted); Assert.AreEqual(0, fixture.Vault.Deleted.Count); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => reading).ConfigureAwait(false);
        Assert.AreEqual(1, await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        Assert.AreEqual(1, fixture.Readers.DisposalAttempts);
    }

    [TestMethod]
    public async Task ExitJoinsAnUnresponsiveCatalogReadAndRejectsItsLateBytes()
    {
        await using var fixture = new Fixture();
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        HoldWorkspaceRead(fixture);
        var reading = WorkspaceSource(fixture, account.Revision).ReadAsync(WorkspaceReference, CancellationToken.None);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var stopping = fixture.Service.DisposeAsync().AsTask();
        await fixture.Readers.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try { Assert.IsFalse(stopping.IsCompleted); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Readers.DisposalAttempts);
    }

    [TestMethod]
    public async Task CallerCancellationAndWrongReturnedIdentityNeverBecomeSuccessfulCatalogReads()
    {
        await using var fixture = new Fixture();
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        var source = WorkspaceSource(fixture, account.Revision);
        fixture.Readers.WorkspaceRead = (_, _, _, _, _) => Task.FromResult(WorkspaceResult() with { AccountId = "foreign" });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => source.ReadAsync(WorkspaceReference, CancellationToken.None)).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        fixture.Readers.WorkspaceRead = (_, _, _, _, _) => { cancellation.Cancel(); return Task.FromResult(WorkspaceResult()); };
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.ReadAsync(WorkspaceReference, cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(account, (await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Accounts.Single());
    }

    private static CatalogWorkspaceObjectSource WorkspaceSource(Fixture fixture, long revision) =>
        new(fixture.State, fixture.Service, new("permission-A", "workspace-A"), revision);
    private static DriveWorkspaceObject WorkspaceResult() => new("permission-A", "object-A", "workspace-A", 1, WorkspacePayload);
    private static void HoldWorkspaceRead(Fixture fixture) => fixture.Readers.WorkspaceRead = async (_, _, _, _, token) =>
    {
        fixture.Readers.Started.TrySetResult();
        using var registration = token.Register(() => fixture.Readers.Canceled.TrySetResult());
        await fixture.Readers.ReleaseRead.Task.ConfigureAwait(false); // Actual owner must join, even without cooperative cancellation.
        return WorkspaceResult();
    };
}
