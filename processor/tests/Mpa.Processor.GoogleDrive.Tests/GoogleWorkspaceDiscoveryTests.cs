using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspacePublicationTests
{
    [TestMethod]
    public async Task InitialWorkspaceInventoryDoesNotFilterAwayUnknownOriginalsOrNestedFolders()
    {
        foreach (var mime in new[] { "image/jpeg", "application/vnd.google-apps.folder", "application/gzip" })
        {
            using var fixture = new Fixture { DocumentsBody = "{\"files\":[" + Changed(root => root["mimeType"] = mime) + "]}" };
            await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadInitialWorkspaceDocumentsAsync("workspace", null, null, CancellationToken.None)).ConfigureAwait(false);
            var query = fixture.DocumentQueries.Single(); StringAssert.Contains(query, "trashed = false and 'workspace' in parents");
            Assert.IsFalse(query.Contains("mimeType =", StringComparison.Ordinal));
            Assert.AreEqual(0, fixture.BlobReads); Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
            Assert.IsTrue(fixture.Responses.All(response => response.Disposed));
        }
    }

    [TestMethod]
    public async Task InitialWorkspaceJsonCandidatesKeepSharedDriveAndContinuationBounds()
    {
        using var fixture = new Fixture { DocumentsBody = "{\"files\":[" + Metadata + "],\"nextPageToken\":\"next token&x\",\"incompleteSearch\":true}" };
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var page = await connection.ReadInitialWorkspaceDocumentsAsync("workspace", "shared-drive", "prior token&x", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("allocated-file", page.Documents.Single().FileId); Assert.IsTrue(page.IncompleteSearch); Assert.AreEqual("next token&x", page.NextPageToken);
        var query = fixture.DocumentQueries.Single(); StringAssert.Contains(query, "corpora=drive&driveId=shared-drive"); StringAssert.Contains(query, "pageToken=prior token&x");
        Assert.IsFalse(query.Contains("mimeType =", StringComparison.Ordinal)); Assert.AreEqual(0, fixture.BlobReads);
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadInitialWorkspaceDocumentsAsync("root", null, null, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadInitialWorkspaceDocumentsAsync("workspace", null, "bad\npage", CancellationToken.None));
    }

    [TestMethod]
    public async Task WorkspaceInventoryRetainsOpaqueContinuationsAndIncompleteStateWithoutReadingOriginals()
    {
        using var fixture = new Fixture { DocumentsBody = "{\"files\":[" + Metadata + "],\"nextPageToken\":\"next token&x\",\"incompleteSearch\":true}" };
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false); Assert.IsInstanceOfType<IGoogleWorkspaceDiscovery>(connection);
        var page = await connection.ReadWorkspaceDocumentsAsync("workspace", "shared-drive", "prior token&x", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("next token&x", page.NextPageToken); Assert.IsTrue(page.IncompleteSearch); Assert.AreEqual(7L, page.Documents.Single().Version);
        Assert.AreEqual((long)Content.Length, page.Documents.Single().Bytes); Assert.AreEqual(0, fixture.BlobReads); Assert.AreEqual(0, fixture.Creates);
        StringAssert.Contains(fixture.DocumentQueries.Single(), "mimeType = 'application/json'"); StringAssert.Contains(fixture.DocumentQueries.Single(), "'workspace' in parents");
        StringAssert.Contains(fixture.DocumentQueries.Single(), "corpora=drive&driveId=shared-drive"); StringAssert.Contains(fixture.DocumentQueries.Single(), "pageToken=prior token&x");
        Assert.IsFalse(fixture.DocumentQueries.Single().Contains("mpaRole", StringComparison.Ordinal)); Assert.IsTrue(fixture.Responses.All(response => response.Disposed));
    }

    [TestMethod]
    public async Task CandidateReadRetainsActualScopeVersionAndBoundsButGrantsNoSignatureAuthority()
    {
        using var fixture = new Fixture(); await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var page = await connection.ReadWorkspaceDocumentsAsync("workspace", null, null, CancellationToken.None).ConfigureAwait(false);
        var result = await connection.ReadWorkspaceDocumentAsync("workspace", page.Documents.Single(), CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(Content, result.Bytes.ToArray()); Assert.AreEqual("account-A", result.AccountId); Assert.AreEqual("workspace", result.FolderId);
        Assert.AreEqual(7L, result.DriveVersion); Assert.AreEqual(2, fixture.MetadataReads); Assert.AreEqual(1, fixture.BlobReads);
        Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations); Assert.IsTrue(fixture.Responses.All(response => response.Disposed));
    }

    [TestMethod]
    public async Task MalformedOversizedOrDuplicateInventoryCannotBeInterpretedAsAnEmptyWorkspace()
    {
        var invalid = new[] { "{}", "{\"files\":null}", "{\"files\":[" + Metadata + "," + Metadata + "]}",
            "{\"files\":[" + Changed(root => root["size"] = "262145") + "]}",
            "{\"files\":[" + Changed(root => root["mimeType"] = "image/jpeg") + "]}",
            "{\"files\":[" + Changed(root => root["version"] = "7.0") + "]}",
            "{\"files\":[],\"incompleteSearch\":\"true\"}", "{\"files\":[],\"nextPageToken\":\"\"}" };
        foreach (var body in invalid)
        {
            using var fixture = new Fixture { DocumentsBody = body }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadWorkspaceDocumentsAsync("workspace", null, null, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0, fixture.BlobReads); Assert.AreEqual(0, fixture.Creates);
        }
    }

    [TestMethod]
    public async Task MovedTrashedOrDownloadDeniedCandidateInventoryRemainsExplicitlyUnverified()
    {
        foreach (var file in new[] { Changed(root => root["parents"] = new JsonArray("outside")), Changed(root => root["trashed"] = true) })
        {
            using var fixture = new Fixture { DocumentsBody = "{\"files\":[" + file + "]}" }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(() => connection.ReadWorkspaceDocumentsAsync("workspace", null, null, CancellationToken.None)).ConfigureAwait(false);
        }
        using var denied = new Fixture { DocumentsBody = "{\"files\":[" + Changed(root => root["capabilities"]!["canDownload"] = false) + "]}" };
        await using var deniedConnection = await denied.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DriveWorkspaceDownloadDeniedException>(() => deniedConnection.ReadWorkspaceDocumentsAsync("workspace", null, null, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, denied.BlobReads);
    }

    [TestMethod]
    public async Task ChangedCandidateVersionOrMetadataRoleCannotDownloadOrCompleteAsCaptured()
    {
        foreach (var late in new[] { false, true })
        {
            using var fixture = new Fixture { Before = late ? Metadata : Changed(root => root["version"] = "8"), After = Changed(root => root["version"] = "8") };
            await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(() => connection.ReadWorkspaceDocumentAsync("workspace", new("allocated-file", 7, Content.Length), CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(late ? 1 : 0, fixture.BlobReads);
        }
        using var original = new Fixture { Before = Changed(root => root["mimeType"] = "image/jpeg") }; await using var originalConnection = await original.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => originalConnection.ReadWorkspaceDocumentAsync("workspace", new("allocated-file", 7, Content.Length), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, original.BlobReads);
    }

    [TestMethod]
    public async Task InvalidDiscoveryInputsCauseNoNetworkOrMutation()
    {
        using var fixture = new Fixture(); await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceDocumentsAsync("root", null, null, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceDocumentsAsync("workspace", "invalid/drive", null, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceDocumentsAsync("workspace", null, "bad\npage", CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceDocumentAsync("workspace", new("allocated-file", 0, Content.Length), CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceDocumentAsync("workspace", new("allocated-file", 7, 262145), CancellationToken.None));
        Assert.AreEqual(0, fixture.Calls);
    }

    [TestMethod]
    public async Task ConnectionShutdownJoinsTheActualInventoryReadBeforeReleasingItsNativeOwner()
    {
        using var fixture = new Fixture { BlockDocuments = true }; var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var reading = connection.ReadWorkspaceDocumentsAsync("workspace", null, null, CancellationToken.None); Task? stopping = null;
        try
        {
            await fixture.DocumentsEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = connection.DisposeAsync().AsTask();
            Assert.IsFalse(reading.IsCompleted); Assert.IsFalse(stopping.IsCompleted);
        }
        finally { fixture.DocumentsRelease.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false); Assert.IsNotNull(stopping); await stopping.ConfigureAwait(false);
        Assert.IsTrue(fixture.Responses.All(response => response.Disposed)); Assert.AreEqual(0, fixture.Creates);
    }

    private sealed partial class Fixture
    {
        public string DocumentsBody { get; init; } = "{\"files\":[" + Metadata + "],\"incompleteSearch\":false}";
        public List<string> DocumentQueries { get; } = [];
        public bool BlockDocuments { get; init; }
        public TaskCompletionSource DocumentsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DocumentsRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
