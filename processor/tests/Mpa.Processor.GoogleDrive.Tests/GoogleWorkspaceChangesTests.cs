using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspacePublicationTests
{
    [TestMethod]
    public async Task OpaqueChangeCursorsRetainTheirAccountDriveAndExplicitPageBoundary()
    {
        using var fixture = new Fixture { ChangeStartBody = "{\"startPageToken\":\"start token&x\"}", ChangeBody = "{\"changes\":[],\"nextPageToken\":\"next token&x\"}" };
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false); Assert.IsInstanceOfType<IGoogleWorkspaceChanges>(connection);
        var cursor = await connection.GetChangeCursorAsync("shared-drive", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("account-A", cursor.AccountId); Assert.AreEqual("shared-drive", cursor.SharedDriveId); Assert.AreEqual("start token&x", cursor.Token);
        var page = await connection.ReadChangesAsync(cursor, CancellationToken.None).ConfigureAwait(false); Assert.IsNull(page.AtEnd); Assert.IsNotNull(page.Next);
        Assert.AreEqual(cursor with { Token = "next token&x" }, page.Next); Assert.AreEqual(0, page.Changes.Count);
        StringAssert.Contains(fixture.ChangeQueries[1], "pageToken=start token&x"); StringAssert.Contains(fixture.ChangeQueries[1], "driveId=shared-drive");
        StringAssert.Contains(fixture.ChangeQueries[1], "includeRemoved=true&includeCorpusRemovals=true"); Assert.AreEqual(0, fixture.BlobReads);
    }

    [TestMethod]
    public async Task FileCorpusAndDriveRemovalsRemainObservationsWithoutDownloadingOrDeletingMedia()
    {
        var image = Changed(root => root["mimeType"] = "image/jpeg");
        using var fixture = new Fixture { ChangeBody = "{\"changes\":[{\"changeType\":\"file\",\"fileId\":\"allocated-file\",\"removed\":false,\"file\":" + image +
            "},{\"changeType\":\"file\",\"fileId\":\"lost-file\",\"removed\":true},{\"changeType\":\"drive\",\"driveId\":\"shared-drive\",\"removed\":true}],\"newStartPageToken\":\"at-end\"}" };
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var cursor = await connection.GetChangeCursorAsync(null, CancellationToken.None).ConfigureAwait(false); var page = await connection.ReadChangesAsync(cursor, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(page.Next); Assert.AreEqual(cursor with { Token = "at-end" }, page.AtEnd); Assert.AreEqual(3, page.Changes.Count);
        Assert.AreEqual("image/jpeg", page.Changes[0].File!.MimeType); CollectionAssert.AreEqual(Parents, page.Changes[0].File!.Parents.ToArray());
        Assert.IsTrue(page.Changes[1].Removed); Assert.IsNull(page.Changes[1].File); Assert.AreEqual("drive", page.Changes[2].Kind);
        Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.BlobReads); Assert.AreEqual(0, fixture.Allocations);
        Assert.IsTrue(fixture.Responses.All(response => response.Disposed));
    }

    [TestMethod]
    public async Task MalformedAmbiguousOrNonadvancingChangesNeverYieldAnAcceptedCursor()
    {
        var invalid = new[] { "{}", "{\"changes\":[]}", "{\"changes\":[],\"nextPageToken\":\"next\",\"newStartPageToken\":\"end\"}",
            "{\"changes\":[],\"nextPageToken\":\"start\"}", "{\"changes\":[],\"newStartPageToken\":\"\"}",
            "{\"changes\":[{\"changeType\":\"future-kind\",\"removed\":false}],\"newStartPageToken\":\"end\"}",
            "{\"changes\":[{\"changeType\":\"file\",\"fileId\":\"allocated-file\",\"removed\":false}],\"newStartPageToken\":\"end\"}",
            "{\"changes\":[{\"changeType\":\"file\",\"fileId\":\"different\",\"removed\":false,\"file\":" + Metadata + "}],\"newStartPageToken\":\"end\"}",
            "{\"changes\":[{\"changeType\":\"file\",\"fileId\":\"allocated-file\",\"removed\":false,\"file\":" + Changed(root => root["size"] = "-1") + "}],\"newStartPageToken\":\"end\"}" };
        foreach (var body in invalid)
        {
            using var fixture = new Fixture { ChangeBody = body }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadChangesAsync(new("account-A", null, "start"), CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0, fixture.BlobReads); Assert.AreEqual(0, fixture.Creates);
        }
    }

    [TestMethod]
    public async Task ForeignOrInvalidCursorScopeIsRejectedBeforeNetworkAndMalformedStartIsNotReplaced()
    {
        using var fixture = new Fixture(); await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => connection.GetChangeCursorAsync("invalid/drive", CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadChangesAsync(new("account-B", null, "start"), CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadChangesAsync(new("account-A", "invalid/drive", "start"), CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadChangesAsync(new("account-A", null, "bad\ntoken"), CancellationToken.None)); Assert.AreEqual(0, fixture.Calls);
        foreach (var body in new[] { "{}", "{\"startPageToken\":1}", "{\"startPageToken\":\"\"}" })
        {
            using var malformed = new Fixture { ChangeStartBody = body }; await using var malformedConnection = await malformed.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => malformedConnection.GetChangeCursorAsync(null, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ChangeReadShutdownJoinsTheActualRequestBeforeReleasingTheAccountConnection()
    {
        using var fixture = new Fixture { BlockChanges = true }; var connection = await fixture.ConnectAsync().ConfigureAwait(false); Task? stopping = null;
        var reading = connection.ReadChangesAsync(new("account-A", null, "start"), CancellationToken.None);
        try { await fixture.ChangesEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = connection.DisposeAsync().AsTask(); Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(reading.IsCompleted); }
        finally { fixture.ChangesRelease.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false); Assert.IsNotNull(stopping); await stopping.ConfigureAwait(false);
        Assert.IsTrue(fixture.Responses.All(response => response.Disposed));
    }

    private sealed partial class Fixture
    {
        public string ChangeStartBody { get; init; } = "{\"startPageToken\":\"start\"}";
        public string ChangeBody { get; init; } = "{\"changes\":[],\"newStartPageToken\":\"at-end\"}";
        public List<string> ChangeQueries { get; } = [];
        public bool BlockChanges { get; init; }
        public TaskCompletionSource ChangesEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChangesRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
