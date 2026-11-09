using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class WorkspaceHistoryReplayReaderTests
{
    private static readonly string[] OrderedReplayCalls = ["read:retained", "observe:second", "read:second", "observe:end"];
    [TestMethod]
    public async Task ReplayStartsAtRetainedHistoryAndJoinsEveryPageBeforeAdvancingToTheFinalFence()
    {
        var source = new Changes(); var observer = new Observer(source.Calls);
        source.Pages.Enqueue(Page([Change("known", 1)], "second"));
        source.Pages.Enqueue(Page([new("file", "unrelated", false, new("image/jpeg", new(["outside"]), false, 1, 20, true))], "end", atEnd: true));
        var inventory = Inventory(new DriveWorkspaceDocumentCandidate("known", 3, 2));
        var result = await new WorkspaceHistoryReplayReader(source).ReplayAsync(inventory, Cursor("retained"), observer, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(OrderedReplayCalls, source.Calls);
        Assert.AreEqual(3L, observer.Pages[0].Candidates.Single().Version); Assert.HasCount(1, observer.Pages[0].Events); Assert.HasCount(0, observer.Pages[1].Events);
        Assert.AreSame(inventory.Documents, result.Documents); Assert.AreEqual("end", result.Cursor.Token);
    }

    [TestMethod]
    public async Task UnknownRemovalIsObservedBeforeRecoveryWithoutAssumingItBelongedToAnotherFolder()
    {
        var source = new Changes(); var observer = new Observer(source.Calls); source.Pages.Enqueue(Page([new("file", "unclassifiable", true, null)], "end", true));
        await Assert.ThrowsExactlyAsync<WorkspaceHistoryRecoveryRequiredException>(() => new WorkspaceHistoryReplayReader(source)
            .ReplayAsync(Inventory(), Cursor("earliest"), observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(observer.Pages.Single().Events.Single().RecoveryRequired); Assert.AreEqual("unclassifiable", observer.Pages.Single().Events.Single().Id);
        Assert.AreEqual("observe:end", source.Calls[^1]);
    }

    [TestMethod]
    public async Task NewMetadataIsRetainedBeforeRequiringAFreshListingAndOriginalsAreNeverDownloaded()
    {
        var source = new Changes(); var observer = new Observer(source.Calls); source.Pages.Enqueue(Page([Change("new-policy", 2)], "end", true));
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => new WorkspaceHistoryReplayReader(source)
            .ReplayAsync(Inventory(), Cursor("earliest"), observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("new-policy", observer.Pages.Single().Candidates.Single().FileId); Assert.IsTrue(observer.Pages.Single().InventoryChanged);
        Assert.IsFalse(observer.Pages.Single().Events.Single().RecoveryRequired); Assert.AreEqual(1, source.Reads);
    }

    [TestMethod]
    public async Task FailedPersistenceAndContinuationCyclesCannotReadAnotherUnobservedPage()
    {
        var source = new Changes(); var observer = new Observer(source.Calls) { Fail = true }; source.Pages.Enqueue(Page([], "next"));
        await Assert.ThrowsExactlyAsync<IOException>(() => new WorkspaceHistoryReplayReader(source).ReplayAsync(Inventory(), Cursor("start"), observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, source.Reads);
        source = new(); observer = new(source.Calls); source.Pages.Enqueue(Page([], "next")); source.Pages.Enqueue(Page([], "start"));
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => new WorkspaceHistoryReplayReader(source).ReplayAsync(Inventory(), Cursor("start"), observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2, source.Reads); Assert.HasCount(2, observer.Pages); Assert.AreEqual("observe:start", source.Calls[^1]);
    }

    [TestMethod]
    public async Task CanceledUncooperativePersistenceStaysJoinedAndRejectsItsLateContinuation()
    {
        var source = new Changes(); var observer = new Observer(source.Calls) { Hold = true }; source.Pages.Enqueue(Page([], "next")); using var cancel = new CancellationTokenSource();
        var reading = new WorkspaceHistoryReplayReader(source).ReplayAsync(Inventory(), Cursor("start"), observer, cancel.Token);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try { await cancel.CancelAsync().ConfigureAwait(false); Assert.IsFalse(reading.IsCompleted); Assert.AreEqual(1, source.Reads); }
        finally { observer.Release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false); Assert.AreEqual(1, source.Reads);
    }

    [TestMethod]
    public async Task PageBudgetsAndForeignCursorsFailWithoutInventingACompletedReplay()
    {
        var source = new Changes(); var observer = new Observer(source.Calls);
        for (var number = 0; number < 128; number++) source.Pages.Enqueue(Page([], "page-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await Assert.ThrowsExactlyAsync<WorkspaceHistoryReplayPendingException>(() => new WorkspaceHistoryReplayReader(source).ReplayAsync(Inventory(), Cursor("start"), observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(128, source.Reads); Assert.HasCount(128, observer.Pages); Assert.IsFalse(observer.Pages.Any(page => page.AtEnd));
        source = new(); observer = new(source.Calls); source.Pages.Enqueue(new([], null, new("foreign", null, "end")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new WorkspaceHistoryReplayReader(source).ReplayAsync(Inventory(), Cursor("start"), observer, CancellationToken.None)).ConfigureAwait(false);
        Assert.HasCount(0, observer.Pages);
    }

    private static WorkspaceDocumentInventory Inventory(params DriveWorkspaceDocumentCandidate[] candidates) => new("account", "workspace", null, new(candidates.ToList()), new([]), Cursor("fresh-listing-end"));
    private static DriveChangeCursor Cursor(string token) => new("account", null, token);
    private static DriveObservedChange Change(string id, long version) => new("file", id, false, new("application/json", new(["workspace"]), false, version, 2, true));
    private static DriveChangePage Page(DriveObservedChange[] changes, string next, bool atEnd = false) => new(new(changes.ToList()), atEnd ? null : Cursor(next), atEnd ? Cursor(next) : null);
    private sealed class Changes : IGoogleWorkspaceChanges
    {
        public Queue<DriveChangePage> Pages { get; } = new();
        public List<string> Calls { get; } = [];
        public int Reads { get; private set; }
        public Task<DriveChangeCursor> GetChangeCursorAsync(string? sharedDriveId, CancellationToken cancellationToken) => throw new InvalidOperationException("A new cursor cannot replace retained history.");
        public Task<DriveChangePage> ReadChangesAsync(DriveChangeCursor cursor, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; Calls.Add("read:" + cursor.Token); return Task.FromResult(Pages.Dequeue()); }
    }
    private sealed class Observer(List<string> calls) : IWorkspaceHistoryObserver
    {
        public List<WorkspaceHistoryPageEvidence> Pages { get; } = [];
        public bool Fail { get; init; }
        public bool Hold { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PageAsync(WorkspaceHistoryPageEvidence page, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Pages.Add(page); calls.Add("observe:" + page.Next.Token); Entered.TrySetResult();
            if (Fail) throw new IOException("Synthetic failed change persistence");
            if (Hold) await Release.Task.ConfigureAwait(false);
        }
    }
}
