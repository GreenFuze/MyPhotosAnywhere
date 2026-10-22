using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly DriveWorkspaceDocumentCandidate DocumentCandidate = new("object-A", 1, 2);

    [TestMethod]
    public async Task DocumentReconciliationUsesTheSameCapturedAccountOwnerAndNoOriginalReads()
    {
        await using var fixture = new Fixture(); var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        var source = DocumentSource(fixture, account.Revision); ConfigureDocuments(fixture);
        var inventory = await source.CaptureAsync(Array.Empty<string>(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(DocumentCandidate, inventory.Documents.Single()); Assert.AreEqual("at-end", inventory.Cursor.Token);
        Assert.AreEqual(WorkspaceResult(), await source.ReadAsync(inventory.Documents.Single(), CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("at-end", (await source.CheckUnchangedAsync(inventory, CancellationToken.None).ConfigureAwait(false)).Token);
        Assert.AreEqual(1, fixture.Readers.Opened.Count); Assert.AreEqual(account.ConnectionId, fixture.Readers.Opened.Single());
        Assert.AreEqual(5, fixture.Readers.DocumentCalls);
    }

    [TestMethod]
    public async Task ReconnectedAccountCannotRetargetAnOldDocumentInventoryOrRead()
    {
        await using var fixture = new Fixture(); var first = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        var source = DocumentSource(fixture, first.Revision); await fixture.Service.ConnectAsync(first.PermissionId, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => source.CaptureAsync(Array.Empty<string>(), CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => source.ReadAsync(DocumentCandidate, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Readers.DocumentCalls);
    }

    [TestMethod]
    public async Task DisconnectDuringHeldInventoryRejectsLateResultsAndCredentialCleanupWaits()
    {
        await using var fixture = new Fixture(); var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false); ConfigureDocuments(fixture);
        fixture.Readers.DocumentInventory = async (_, _, _, _) =>
        { fixture.Readers.Started.TrySetResult(); await fixture.Readers.ReleaseRead.Task.ConfigureAwait(false); return new(new([DocumentCandidate]), null, false); };
        var reading = DocumentSource(fixture, account.Revision).CaptureAsync(Array.Empty<string>(), CancellationToken.None);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await fixture.Service.DisconnectAsync(account.PermissionId, account.Revision, CancellationToken.None).ConfigureAwait(false);
        var cleaning = fixture.Service.CleanCredentialsAsync(CancellationToken.None);
        try { Assert.IsFalse(cleaning.IsCompleted); Assert.AreEqual(0, fixture.Vault.Deleted.Count); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => reading).ConfigureAwait(false);
        Assert.AreEqual(1, await cleaning.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)); Assert.AreEqual(1, fixture.Readers.DisposalAttempts);
    }

    [TestMethod]
    public async Task ExitJoinsHeldDocumentTransportAndRejectsItsLateBytes()
    {
        await using var fixture = new Fixture(); var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false); ConfigureDocuments(fixture);
        fixture.Readers.DocumentRead = async (_, _, _) =>
        { fixture.Readers.Started.TrySetResult(); await fixture.Readers.ReleaseRead.Task.ConfigureAwait(false); return WorkspaceResult(); };
        var reading = DocumentSource(fixture, account.Revision).ReadAsync(DocumentCandidate, CancellationToken.None);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var stopping = fixture.Service.DisposeAsync().AsTask();
        try { Assert.IsFalse(stopping.IsCompleted); Assert.AreEqual(0, fixture.Readers.DisposalAttempts); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); Assert.AreEqual(1, fixture.Readers.DisposalAttempts);
    }

    [TestMethod]
    public async Task WrongDocumentIdentityRevisionOrLengthAndCallerCancellationNeverSucceed()
    {
        await using var fixture = new Fixture(); var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false); ConfigureDocuments(fixture);
        var source = DocumentSource(fixture, account.Revision);
        foreach (var wrong in new[] { WorkspaceResult() with { AccountId = "other" }, WorkspaceResult() with { FolderId = "other" },
            WorkspaceResult() with { FileId = "other" }, WorkspaceResult() with { DriveVersion = 2 }, WorkspaceResult() with { Bytes = new byte[3] } })
        {
            fixture.Readers.DocumentRead = (_, _, _) => Task.FromResult(wrong);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => source.ReadAsync(DocumentCandidate, CancellationToken.None)).ConfigureAwait(false);
        }
        using var cancel = new CancellationTokenSource(); fixture.Readers.DocumentRead = (_, _, _) => { cancel.Cancel(); return Task.FromResult(WorkspaceResult()); };
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.ReadAsync(DocumentCandidate, cancel.Token)).ConfigureAwait(false);
        var calls = fixture.Readers.DocumentCalls; Assert.ThrowsExactly<ArgumentException>(() => source.ReadAsync(DocumentCandidate with { Bytes = 0 }, CancellationToken.None));
        Assert.AreEqual(calls, fixture.Readers.DocumentCalls);
    }

    private static CatalogWorkspaceDocumentSource DocumentSource(Fixture fixture, long revision) => new(fixture.State, fixture.Service, new("permission-A", "workspace-A"), null, revision);
    private static void ConfigureDocuments(Fixture fixture)
    {
        fixture.Readers.DocumentStart = (_, _) => Task.FromResult(new DriveChangeCursor("permission-A", null, "start"));
        fixture.Readers.DocumentChanges = (_, _) => Task.FromResult(new DriveChangePage([], null, new("permission-A", null, "at-end")));
        fixture.Readers.DocumentInventory = (_, _, _, _) => Task.FromResult(new DriveWorkspaceDocumentPage(new([DocumentCandidate]), null, false));
        fixture.Readers.DocumentRead = (_, _, _) => Task.FromResult(WorkspaceResult());
    }
    private sealed partial class LibraryFactory
    {
        public int DocumentCalls { get; private set; }
        public Func<string?, CancellationToken, Task<DriveChangeCursor>>? DocumentStart { get; set; }
        public Func<DriveChangeCursor, CancellationToken, Task<DriveChangePage>>? DocumentChanges { get; set; }
        public Func<string, string?, string?, CancellationToken, Task<DriveWorkspaceDocumentPage>>? DocumentInventory { get; set; }
        public Func<string, DriveWorkspaceDocumentCandidate, CancellationToken, Task<DriveWorkspaceObject>>? DocumentRead { get; set; }
        private sealed partial class Connection : IGoogleWorkspaceDiscovery, IGoogleWorkspaceChanges
        {
            public Task<DriveChangeCursor> GetChangeCursorAsync(string? sharedDriveId, CancellationToken cancellationToken)
            { owner.DocumentCalls++; return owner.DocumentStart?.Invoke(sharedDriveId, cancellationToken) ?? throw new InvalidOperationException("Unexpected change start request."); }
            public Task<DriveChangePage> ReadChangesAsync(DriveChangeCursor cursor, CancellationToken cancellationToken)
            { owner.DocumentCalls++; return owner.DocumentChanges?.Invoke(cursor, cancellationToken) ?? throw new InvalidOperationException("Unexpected changes request."); }
            public Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken)
            { owner.DocumentCalls++; return owner.DocumentInventory?.Invoke(folderId, sharedDriveId, pageToken, cancellationToken) ?? throw new InvalidOperationException("Unexpected document inventory request."); }
            public Task<DriveWorkspaceObject> ReadWorkspaceDocumentAsync(string folderId, DriveWorkspaceDocumentCandidate candidate, CancellationToken cancellationToken)
            { owner.DocumentCalls++; return owner.DocumentRead?.Invoke(folderId, candidate, cancellationToken) ?? throw new InvalidOperationException("Unexpected document request."); }
        }
    }
}
