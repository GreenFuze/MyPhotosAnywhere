using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspacePublicationTests
{
    [TestMethod]
    public async Task GeneralMetadataInventoryCoversEverySupportedEncodingWithoutNameOrPropertyFilters()
    {
        var json = Changed(root => root["id"] = "json-policy");
        var gzip = Changed(root => { root["id"] = "gzip-facts"; root["mimeType"] = "application/gzip"; root["size"] = "4194304"; });
        var binary = Changed(root => { root["id"] = "binary-facts"; root["mimeType"] = "application/octet-stream"; root["size"] = "4194304"; });
        using var fixture = new Fixture { DocumentsBody = "{\"files\":[" + json + "," + gzip + "," + binary + "],\"nextPageToken\":\"next token&x\",\"incompleteSearch\":true}" };
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false); Assert.IsInstanceOfType<IGoogleWorkspaceMetadataDiscovery>(connection);
        var page = await connection.ReadWorkspaceMetadataPageAsync("workspace", "shared-drive", "previous token&x", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3, page.Objects.Count); Assert.AreEqual("application/json", page.Objects[0].MimeType); Assert.AreEqual(4194304L, page.Objects[1].Bytes);
        Assert.AreEqual("application/octet-stream", page.Objects[2].MimeType); Assert.IsTrue(page.IncompleteSearch); Assert.AreEqual("next token&x", page.NextPageToken);
        var query = fixture.DocumentQueries.Single(); Assert.IsFalse(query.Contains("mimeType =", StringComparison.Ordinal)); Assert.IsFalse(query.Contains("mpaRole", StringComparison.Ordinal));
        StringAssert.Contains(query, "'workspace' in parents"); StringAssert.Contains(query, "corpora=drive&driveId=shared-drive"); StringAssert.Contains(query, "pageToken=previous token&x");
        Assert.AreEqual(0, fixture.BlobReads); Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
        Assert.IsTrue(fixture.Responses.All(response => response.Disposed));
    }

    [TestMethod]
    public async Task BinaryCandidatesHaveTheirOwnBoundWhileJsonAndInitialBootstrapKeepTheirSmallerBound()
    {
        foreach (var mime in new[] { "application/gzip", "application/octet-stream" })
        {
            var blob = System.Text.Encoding.UTF8.GetBytes(new string('x', GoogleAccountConnection.MaximumWorkspaceDocumentBytes + 1));
            var metadata = Changed(root => { root["mimeType"] = mime; root["size"] = blob.Length.ToString(System.Globalization.CultureInfo.InvariantCulture); });
            using var fixture = new Fixture { Blob = blob, Before = metadata, DocumentsBody = "{\"files\":[" + metadata + "]}" };
            await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            var candidate = (await connection.ReadWorkspaceMetadataPageAsync("workspace", null, null, CancellationToken.None).ConfigureAwait(false)).Objects.Single();
            var result = await connection.ReadWorkspaceMetadataCandidateAsync("workspace", candidate, CancellationToken.None).ConfigureAwait(false);
            CollectionAssert.AreEqual(blob, result.Bytes.ToArray()); Assert.AreEqual(7L, result.DriveVersion); Assert.AreEqual(1, fixture.BlobReads);
            // Transport bytes remain untrusted; this test grants no gzip decoding,
            // signed role, graph validity or generation activation.
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadInitialWorkspaceDocumentsAsync("workspace", null, null, CancellationToken.None)).ConfigureAwait(false);
        }
        foreach (var mime in new[] { "application/json", "application/gzip", "application/octet-stream" })
        {
            var maximum = mime == "application/json" ? 262144 : 4194304;
            var metadata = Changed(root => { root["mimeType"] = mime; root["size"] = (maximum + 1).ToString(System.Globalization.CultureInfo.InvariantCulture); });
            using var fixture = new Fixture { DocumentsBody = "{\"files\":[" + metadata + "]}" }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadWorkspaceMetadataPageAsync("workspace", null, null, CancellationToken.None)).ConfigureAwait(false);
            Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceMetadataCandidateAsync("workspace", new("allocated-file", 7, maximum + 1, mime), CancellationToken.None));
            Assert.AreEqual(0, fixture.BlobReads);
        }
    }

    [TestMethod]
    public async Task UnsupportedOriginalsFoldersShortcutsAndGoogleDocumentsNeverDownloadOrDisappearFromAClaimedInventory()
    {
        foreach (var mime in new[] { "image/jpeg", "video/mp4", "application/vnd.google-apps.folder", "application/vnd.google-apps.shortcut", "application/vnd.google-apps.document" })
        {
            using var fixture = new Fixture { DocumentsBody = "{\"files\":[" + Changed(root => root["mimeType"] = mime) + "]}" };
            await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadWorkspaceMetadataPageAsync("workspace", null, null, CancellationToken.None)).ConfigureAwait(false);
            Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceMetadataCandidateAsync("workspace", new("allocated-file", 7, Content.Length, mime), CancellationToken.None));
            Assert.AreEqual(0, fixture.BlobReads); Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
        }
    }

    [TestMethod]
    public async Task CapturedBinaryEncodingVersionAndMembershipMustRemainExactBeforeAndAfterRead()
    {
        var gzip = Changed(root => root["mimeType"] = "application/gzip");
        foreach (var late in new[] { false, true })
        {
            var changed = Changed(root => root["mimeType"] = "application/octet-stream");
            using var fixture = new Fixture { Before = late ? gzip : changed, After = changed };
            await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(() => connection.ReadWorkspaceMetadataCandidateAsync("workspace",
                new("allocated-file", 7, Content.Length, "application/gzip"), CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(late ? 1 : 0, fixture.BlobReads); Assert.IsTrue(fixture.Responses.All(response => response.Disposed));
        }
        using var moved = new Fixture { Before = Changed(root => { root["mimeType"] = "application/gzip"; root["parents"] = new System.Text.Json.Nodes.JsonArray("elsewhere"); }) };
        await using var movedConnection = await moved.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(() => movedConnection.ReadWorkspaceMetadataCandidateAsync("workspace",
            new("allocated-file", 7, Content.Length, "application/gzip"), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, moved.BlobReads);
    }

    [TestMethod]
    public async Task GeneralInventoryKeepsStrictIdentityContinuationAndCompletePageValidation()
    {
        foreach (var body in new[] { "{}", "{\"files\":null}", "{\"files\":[" + Metadata + "," + Metadata + "]}", "{\"files\":[],\"nextPageToken\":\"\"}",
            "{\"files\":[],\"incompleteSearch\":\"true\"}" })
        {
            using var fixture = new Fixture { DocumentsBody = body }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadWorkspaceMetadataPageAsync("workspace", null, null, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0, fixture.BlobReads); Assert.AreEqual(0, fixture.Creates);
        }
        using var invalid = new Fixture(); await using var invalidConnection = await invalid.ConnectAsync().ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => invalidConnection.ReadWorkspaceMetadataPageAsync("root", null, null, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => invalidConnection.ReadWorkspaceMetadataPageAsync("workspace", null, "bad\npage", CancellationToken.None));
        Assert.AreEqual(0, invalid.Calls);
    }
}
