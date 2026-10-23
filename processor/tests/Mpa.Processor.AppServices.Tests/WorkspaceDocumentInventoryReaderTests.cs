using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class WorkspaceDocumentInventoryReaderTests
{
    private static readonly string[] PriorInventory = ["z", "missing"];
    private static readonly string[] ExpectedCalls = ["start", "list:first", "list:page-2", "changes:start", "changes:more"];
    private static readonly string[] ExpectedIds = ["a", "z"];
    private static readonly string[] MissingIds = ["missing"];
    private static readonly string[] KnownIds = ["known"];

    [TestMethod]
    public async Task InitialInventoryChangeFenceIncludesUnsupportedChildrenButStillIgnoresOtherFolders()
    {
        foreach (var mime in new[] { "image/jpeg", "application/vnd.google-apps.folder", "application/gzip" })
        {
            var fixture = new Fixture(); fixture.Documents.Enqueue(Page([]));
            fixture.Changes.Enqueue(Changes([new("file", "unexpected", false, File(mime, "workspace"))], end: "later"));
            await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => new WorkspaceDocumentInventoryReader(fixture, fixture,
                "account", "workspace", null, allChildChanges: true).CaptureAsync(Array.Empty<string>(), CancellationToken.None)).ConfigureAwait(false);
        }
        var unrelated = new Fixture(); unrelated.Documents.Enqueue(Page([]));
        unrelated.Changes.Enqueue(Changes([new("file", "original", false, File("image/jpeg", "source"))], end: "later"));
        Assert.AreEqual(0, (await new WorkspaceDocumentInventoryReader(unrelated, unrelated, "account", "workspace", null, allChildChanges: true)
            .CaptureAsync(Array.Empty<string>(), CancellationToken.None).ConfigureAwait(false)).Documents.Count);
    }
    [TestMethod]
    public async Task InventoryIsBracketedByChangesAndRetainsMissingKnownIdentities()
    {
        var fixture = new Fixture(); fixture.Documents.Enqueue(Page([new("z", 2, 12)], "page-2")); fixture.Documents.Enqueue(Page([new("a", 1, 10)]));
        fixture.Changes.Enqueue(Changes([new("file", "original", false, File("image/jpeg", "source"))], next: "more"));
        fixture.Changes.Enqueue(Changes([], end: "end")); fixture.Changes.Enqueue(Changes([], end: "later"));
        var reader = fixture.Reader(); var inventory = await reader.CaptureAsync(PriorInventory, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(ExpectedCalls, fixture.Calls.ToArray());
        CollectionAssert.AreEqual(ExpectedIds, inventory.Documents.Select(item => item.FileId).ToArray());
        CollectionAssert.AreEqual(MissingIds, inventory.MissingKnownIds.ToArray()); Assert.AreEqual("end", inventory.Cursor.Token);
        var after = await reader.CheckUnchangedAsync(inventory, CancellationToken.None).ConfigureAwait(false); Assert.AreEqual("later", after.Token);
    }

    [TestMethod]
    public async Task PriorIdsStayRelevantAfterRemovalMoveOrMimeChangeAndNewPoliciesInvalidateInventory()
    {
        DriveObservedChange[] relevant = [new("file", "known", true, null), new("file", "known", false, File("image/jpeg", "elsewhere")),
            new("file", "new-policy", false, File("application/json", "workspace")), new("file", "workspace", false, File("application/vnd.google-apps.folder", "root")),
            new("drive", "shared", true, null)];
        foreach (var change in relevant)
        {
            var fixture = new Fixture(); fixture.Documents.Enqueue(Page([])); fixture.Changes.Enqueue(Changes([change], end: "end"));
            await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => fixture.Reader().CaptureAsync(KnownIds, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(3, fixture.Calls.Count);
        }
        // Unknown original-photo activity outside the metadata scope is not a policy.
        var unrelated = new Fixture(); unrelated.Documents.Enqueue(Page([])); unrelated.Changes.Enqueue(Changes([
            new("file", "image", false, File("image/jpeg", "workspace")), new("file", "elsewhere", true, null)], end: "end"));
        Assert.AreEqual(0, (await unrelated.Reader().CaptureAsync(Array.Empty<string>(), CancellationToken.None).ConfigureAwait(false)).Documents.Count);
    }

    [TestMethod]
    public async Task PartialDuplicateAndCyclicInventoriesNeverReturnACompletedObservation()
    {
        foreach (var mode in new[] { "partial", "duplicate", "cycle", "oversized-page" })
        {
            var fixture = new Fixture();
            if (mode == "partial") fixture.Documents.Enqueue(new([], null, true));
            if (mode == "duplicate") fixture.Documents.Enqueue(Page([new("same", 1, 1), new("same", 1, 1)]));
            if (mode == "cycle") { fixture.Documents.Enqueue(Page([], "repeat")); fixture.Documents.Enqueue(Page([], "repeat")); }
            if (mode == "oversized-page") fixture.Documents.Enqueue(Page(Enumerable.Range(0, 101).Select(i => new DriveWorkspaceDocumentCandidate("id" + i, 1, 1)).ToArray()));
            if (mode == "oversized-page")
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader().CaptureAsync(Array.Empty<string>(), CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => fixture.Reader().CaptureAsync(Array.Empty<string>(), CancellationToken.None)).ConfigureAwait(false);
            Assert.IsFalse(fixture.Calls.Any(call => call.StartsWith("changes:", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public async Task ForeignAmbiguousAndCyclicChangePagesFailWithoutReturningTheirCursor()
    {
        foreach (var mode in new[] { "foreign-account", "foreign-drive", "both", "neither", "cycle", "start-scope" })
        {
            var fixture = new Fixture(); fixture.Documents.Enqueue(Page([]));
            if (mode == "start-scope") fixture.Start = new("other", null, "start");
            else if (mode == "foreign-account") fixture.Changes.Enqueue(new([], null, new("other", null, "end")));
            else if (mode == "foreign-drive") fixture.Changes.Enqueue(new([], null, new("account", "other", "end")));
            else if (mode == "both") fixture.Changes.Enqueue(new([], Cursor("next"), Cursor("end")));
            else if (mode == "neither") fixture.Changes.Enqueue(new([], null, null));
            else { fixture.Changes.Enqueue(Changes([], next: "next")); fixture.Changes.Enqueue(Changes([], next: "start")); }
            if (mode == "cycle") await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => fixture.Reader().CaptureAsync(Array.Empty<string>(), CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Reader().CaptureAsync(Array.Empty<string>(), CancellationToken.None)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task FinalCheckRetainsMissingIdentitiesAndRejectsSubstitutedContextBeforeReading()
    {
        var fixture = new Fixture(); fixture.Documents.Enqueue(Page([new("present", 1, 1)])); fixture.Changes.Enqueue(Changes([], end: "end"));
        var reader = fixture.Reader(); var inventory = await reader.CaptureAsync(MissingIds, CancellationToken.None).ConfigureAwait(false);
        var before = fixture.Calls.Count;
        Assert.ThrowsExactly<ArgumentException>(() => reader.CheckUnchangedAsync(inventory with { FolderId = "different" }, CancellationToken.None));
        Assert.ThrowsExactly<InvalidDataException>(() => reader.CheckUnchangedAsync(inventory with { Cursor = Cursor("end") with { AccountId = "other" } }, CancellationToken.None));
        Assert.ThrowsExactly<InvalidDataException>(() => reader.CheckUnchangedAsync(inventory with { MissingKnownIds = new(["present"]) }, CancellationToken.None));
        Assert.AreEqual(before, fixture.Calls.Count);
        fixture.Changes.Enqueue(Changes([new("file", "missing", true, null)], end: "later"));
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => reader.CheckUnchangedAsync(inventory, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CancellationJoinsAnActualHeldReadAndNeverAdmitsItsLateInventory()
    {
        var fixture = new Fixture { HoldDocuments = true }; fixture.Documents.Enqueue(Page([])); using var cancel = new CancellationTokenSource();
        var reading = fixture.Reader().CaptureAsync(Array.Empty<string>(), cancel.Token);
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try { await cancel.CancelAsync().ConfigureAwait(false); Assert.IsFalse(reading.IsCompleted); }
        finally { fixture.Release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false);
        Assert.IsFalse(fixture.Calls.Any(call => call.StartsWith("changes:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TotalInventoryAndContinuationBoundsFailExplicitlyRatherThanTruncating()
    {
        var fixture = new Fixture(); var count = WorkspaceDocumentInventoryReader.MaximumDocuments + 1;
        for (var offset = 0; offset < count; offset += 100)
            fixture.Documents.Enqueue(Page(Enumerable.Range(offset, Math.Min(100, count - offset)).Select(i => new DriveWorkspaceDocumentCandidate("id" + i, 1, 1)).ToArray(), "page" + offset));
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => fixture.Reader().CaptureAsync(Array.Empty<string>(), CancellationToken.None)).ConfigureAwait(false);
        var tooMany = new Fixture();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => tooMany.Reader().CaptureAsync(Enumerable.Range(0, count).Select(i => "id" + i).ToArray(), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, tooMany.Calls.Count);
        var endless = new Fixture(); endless.Documents.Enqueue(Page([]));
        for (var i = 0; i < WorkspaceDocumentInventoryReader.MaximumPages; i++) endless.Changes.Enqueue(Changes([], next: "next" + i));
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => endless.Reader().CaptureAsync(Array.Empty<string>(), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(WorkspaceDocumentInventoryReader.MaximumPages, endless.Calls.Count(call => call.StartsWith("changes:", StringComparison.Ordinal)));
    }

    private static DriveChangeCursor Cursor(string token) => new("account", null, token);
    private static DriveChangedFile File(string mime, string parent) => new(mime, new([parent]), false, 1, 1, true);
    private static DriveWorkspaceDocumentPage Page(DriveWorkspaceDocumentCandidate[] documents, string? next = null) => new(new(documents), next, false);
    private static DriveChangePage Changes(DriveObservedChange[] changes, string? next = null, string? end = null) => new(new(changes), next is null ? null : Cursor(next), end is null ? null : Cursor(end));

    private sealed class Fixture : IGoogleWorkspaceDiscovery, IGoogleWorkspaceChanges
    {
        public Queue<DriveWorkspaceDocumentPage> Documents { get; } = [];
        public Queue<DriveChangePage> Changes { get; } = [];
        public List<string> Calls { get; } = [];
        public DriveChangeCursor Start { get; set; } = Cursor("start");
        public bool HoldDocuments { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkspaceDocumentInventoryReader Reader() => new(this, this, "account", "workspace", null);
        public Task<DriveChangeCursor> GetChangeCursorAsync(string? sharedDriveId, CancellationToken cancellationToken)
        { Assert.IsNull(sharedDriveId); Calls.Add("start"); return Task.FromResult(Start); }
        public Task<DriveChangePage> ReadChangesAsync(DriveChangeCursor cursor, CancellationToken cancellationToken)
        { Calls.Add("changes:" + cursor.Token); return Task.FromResult(Changes.Dequeue()); }
        public async Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual("workspace", folderId); Assert.IsNull(sharedDriveId); Calls.Add("list:" + (pageToken ?? "first")); Entered.TrySetResult();
            if (HoldDocuments) await Release.Task.ConfigureAwait(false);
            return Documents.Dequeue();
        }
        public Task<DriveWorkspaceObject> ReadWorkspaceDocumentAsync(string folderId, DriveWorkspaceDocumentCandidate candidate, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inventory must never download a candidate or original.");
    }
}
