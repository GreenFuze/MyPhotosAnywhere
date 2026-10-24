using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class WorkspaceIncrementalInventoryReaderTests
{
    private static readonly string[] ExpectedReads = ["retained-earlier", "page-two"];
    private static readonly string[] ExpectedFiles = ["known", "uploaded"];
    [TestMethod]
    public async Task ActualDeltaExtendsOnlyTheExpectedDependencyAndKeepsTheOriginalBaselineUnchanged()
    {
        var changes = new Changes(); var observer = new Observer(); var baseline = Baseline(); var expected = Expected();
        changes.Pages.Enqueue(new(new([new("file", "outside-photo", false, File("image/jpeg", "photos", 1, 10))]), Cursor("page-two"), null));
        changes.Pages.Enqueue(new(new([new("file", "uploaded", false, File("application/gzip", "workspace", 7, 3)),
            new("file", "known", false, File("application/json", "workspace", 1, 2))]), null, Cursor("actual-end")));
        var result = await new WorkspaceIncrementalInventoryReader(changes, baseline, expected).CaptureAsync(observer, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(ExpectedReads, changes.Reads.ToArray()); Assert.AreEqual("baseline-end", observer.Start!.Token);
        Assert.AreEqual(2, observer.Pages.Count); Assert.AreEqual("actual-end", result.Cursor.Token);
        CollectionAssert.AreEqual(ExpectedFiles, result.Documents.Select(item => item.FileId).ToArray());
        Assert.AreEqual(1, baseline.Documents.Count); Assert.AreEqual("baseline-end", baseline.Cursor.Token);
        Assert.IsFalse(observer.Pages.Any(page => page.InventoryChanged || page.Events.Any(item => item.RecoveryRequired)));
    }

    [TestMethod]
    public async Task MissingExpectedCreateEventCannotInventPresenceFromItsHistoricalReceipt()
    {
        var changes = new Changes(); var observer = new Observer(); changes.Pages.Enqueue(new([], null, Cursor("no-event-end")));
        await Assert.ThrowsExactlyAsync<WorkspaceIncrementalRefreshRequiredException>(() => new WorkspaceIncrementalInventoryReader(changes, Baseline(), Expected())
            .CaptureAsync(observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(observer.Pages.Single().AtEnd); Assert.AreEqual(0, observer.Pages.Single().Candidates.Count);
    }

    [TestMethod]
    [DataRow("folder")]
    [DataRow("foreign-metadata")]
    [DataRow("known-version")]
    public async Task AncestorNewMetadataAndChangedKnownVersionRetainActualEvidenceBeforeRequiringFullRead(string fault)
    {
        var change = fault switch
        {
            "folder" => new DriveObservedChange("file", "possible-ancestor", false, File("application/vnd.google-apps.folder", "outside", 3, 0)),
            "foreign-metadata" => new("file", "new-policy", false, File("application/json", "workspace", 1, 2)),
            _ => new("file", "known", false, File("application/json", "workspace", 2, 2))
        };
        var changes = new Changes(); var observer = new Observer(); changes.Pages.Enqueue(new(new([change]), null, Cursor("observed-end")));
        await Assert.ThrowsExactlyAsync<WorkspaceIncrementalRefreshRequiredException>(() => new WorkspaceIncrementalInventoryReader(changes, Baseline())
            .CaptureAsync(observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(change.Id, observer.Pages.Single().Events.Single().Id); Assert.IsFalse(observer.Pages.Single().Events.Single().RecoveryRequired);
        if (fault != "folder") Assert.AreEqual(change.Id, observer.Pages.Single().Candidates.Single().FileId);
    }

    [TestMethod]
    public async Task AllUnknownRemovalsRemainExplicitRecoveryBeforeAnyReturnedInventory()
    {
        var changes = new Changes(); var observer = new Observer(); changes.Pages.Enqueue(new(new([
            new("file", "first-removal", true, null), new("file", "second-removal", true, null)]), null, Cursor("removed-end")));
        await Assert.ThrowsExactlyAsync<WorkspaceHistoryRecoveryRequiredException>(() => new WorkspaceIncrementalInventoryReader(changes, Baseline())
            .CaptureAsync(observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2, observer.Pages.Single().Events.Count); Assert.IsTrue(observer.Pages.Single().Events.All(item => item.RecoveryRequired));
    }

    [TestMethod]
    public async Task IncrementalCancellationJoinsTheActualChangeReadAndRejectsItsLatePage()
    {
        var changes = new Changes { Hold = true }; var observer = new Observer(); changes.Pages.Enqueue(new([], null, Cursor("late-end")));
        using var cancel = new CancellationTokenSource();
        var pending = new WorkspaceIncrementalInventoryReader(changes, Baseline()).CaptureAsync(observer, cancel.Token);
        try
        {
            await changes.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await cancel.CancelAsync().ConfigureAwait(false); Assert.IsFalse(pending.IsCompleted);
        }
        finally { changes.Release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false); Assert.AreEqual(0, observer.Pages.Count);
    }

    [TestMethod]
    public async Task PageBudgetAndCorpusSubstitutionCannotManufactureACompleteInventory()
    {
        var changes = new Changes(); var observer = new Observer();
        for (var i = 0; i < WorkspaceDocumentInventoryReader.MaximumPages; i++) changes.Pages.Enqueue(new([], Cursor("page-" + i), null));
        await Assert.ThrowsExactlyAsync<WorkspaceHistoryReplayPendingException>(() => new WorkspaceIncrementalInventoryReader(changes, Baseline())
            .CaptureAsync(observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(WorkspaceDocumentInventoryReader.MaximumPages, observer.Pages.Count);
        changes = new Changes(); observer = new Observer(); changes.Pages.Enqueue(new([], null, new("foreign-account", null, "end")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new WorkspaceIncrementalInventoryReader(changes, Baseline())
            .CaptureAsync(observer, CancellationToken.None)).ConfigureAwait(false); Assert.AreEqual(0, observer.Pages.Count);
    }

    private static WorkspaceDocumentInventory Baseline() => new("account", "workspace", null, new([new("known", 1, 2)]), new([]), Cursor("baseline-end"));
    private static DriveChangeCursor Cursor(string token) => new("account", null, token);
    private static DriveChangedFile File(string mime, string parent, long version, long bytes) => new(mime, new([parent]), false, version, bytes, true);
    private static WorkspacePublicationExpectation Expected() => new(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
        "account", 1, "workspace", 1, new string('a', 64)), "facts-chunk", "application/gzip", 3, new string('b', 64), "uploaded", 7);

    private sealed class Observer : IWorkspaceIncrementalInventoryObserver
    {
        public DriveChangeCursor? Start { get; private set; }
        public List<WorkspaceHistoryPageEvidence> Pages { get; } = [];
        public Task StartedAsync(DriveChangeCursor cursor, CancellationToken token) { token.ThrowIfCancellationRequested(); Start = cursor; return Task.CompletedTask; }
        public Task<DriveChangeCursor> StartHistoryAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Cursor("retained-earlier")); }
        public Task PageAsync(WorkspaceHistoryPageEvidence page, CancellationToken token) { token.ThrowIfCancellationRequested(); Pages.Add(page); return Task.CompletedTask; }
        public Task PageAsync(IReadOnlyCollection<DriveWorkspaceDocumentCandidate> candidates, CancellationToken token) =>
            throw new InvalidOperationException("Incremental history must retain its whole actual page atomically.");
    }
    private sealed class Changes : IGoogleWorkspaceChanges
    {
        public Queue<DriveChangePage> Pages { get; } = [];
        public List<string> Reads { get; } = [];
        public bool Hold { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DriveChangeCursor> GetChangeCursorAsync(string? sharedDriveId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A new start cursor cannot replace actual retained history.");
        public async Task<DriveChangePage> ReadChangesAsync(DriveChangeCursor cursor, CancellationToken cancellationToken)
        { Reads.Add(cursor.Token); Entered.TrySetResult(); if (Hold) await Release.Task.ConfigureAwait(false); return Pages.Dequeue(); }
    }
}
