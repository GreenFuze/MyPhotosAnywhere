using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class CatalogGenerationCaptureServiceTests
{
    private static readonly string[] BothScanPolicies = ["policy-A", "policy-B"];
    private static readonly string[] NewScanRevocation = ["revocation-B"];

    [TestMethod]
    public async Task SupersededHeldScanCannotAdvanceOrReplaceAnotherOwnersCompletedEvidence()
    {
        await using var fixture = new Fixture(); var context = ScanContext(fixture); var held = new ScanSource(); held.Add("policy-A");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        held.BeforeRead = async () => { entered.TrySetResult(); await release.Task.ConfigureAwait(false); };
        await using var first = new WorkspaceObservationService(fixture.State, fixture.Budget, context, held);
        var running = first.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None); WorkspaceInventoryCheckpoint current;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); var actual = new ScanSource(); actual.Add("policy-A");
            await using var second = new WorkspaceObservationService(fixture.State, fixture.Budget, context, actual);
            current = await second.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(running.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => running).ConfigureAwait(false);
        var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); repository.AssertCompletedScan(current);
        Assert.AreEqual(current, repository.LastCheckpoint()); Assert.IsNotNull(first.Failure);
    }

    [TestMethod]
    public async Task EveryCapturedIdentityIsRetainedBeforeTheFirstDownloadEvenAfterFailureAndRestart()
    {
        await using var fixture = new Fixture(); var context = ScanContext(fixture); var source = new ScanSource(); source.Add("policy-A"); source.Add("policy-B");
        source.BeforeRead = () => throw new IOException("Synthetic unreadable first policy");
        await using (var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, context, source))
            await Assert.ThrowsExactlyAsync<IOException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
        var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        Assert.AreEqual(0, repository.Known(context).Count); Assert.AreEqual(2, repository.KnownIdentities(context).Count); Assert.IsNull(repository.LastAttempt()!.Completed);
        source.BeforeRead = null; source.Documents.Remove("policy-B");
        await using var restarted = new WorkspaceObservationService(fixture.State, fixture.Budget, context, source);
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => restarted.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(BothScanPolicies, source.LastKnown.ToArray()); Assert.IsNull(repository.LastCheckpoint()); Assert.AreEqual(0, source.FinalChecks);
        source.Add("policy-B"); var recovered = await restarted.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None).ConfigureAwait(false);
        repository.AssertCompletedScan(recovered); Assert.AreEqual(2, recovered.Documents);
    }

    [TestMethod]
    public async Task FailedCandidateBatchPreventsAllDownloadsAndPolicyCallbacks()
    {
        await using var fixture = new Fixture(); var context = ScanContext(fixture); var source = new ScanSource(); source.Add("policy-A"); source.Add("policy-B");
        ObservationSql(fixture, "CREATE TRIGGER fail_candidate_batch BEFORE INSERT ON processor_meta WHEN NEW.key='workspace_observed_candidate_v1:policy-B' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        await using var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, context, source);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => scanner.ScanAsync((_, _) => throw new InvalidOperationException("Verifier must not run"), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, source.Captures); Assert.AreEqual(0, source.Reads); Assert.AreEqual(0, source.FinalChecks);
        var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        Assert.IsNull(repository.LastAttempt()!.Completed); Assert.AreEqual(0, repository.KnownIdentities(context).Count); Assert.IsNull(repository.LastCheckpoint());
    }

    [TestMethod]
    public async Task FailedScanStartCannotInspectCloudOrReuseAnEarlierCompletedAttempt()
    {
        await using var fixture = new Fixture(); var context = ScanContext(fixture); var source = new ScanSource(); source.Add("policy-A");
        await using var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, context, source);
        var completed = await scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None).ConfigureAwait(false);
        var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); repository.AssertCompletedScan(completed);
        ObservationSql(fixture, "CREATE TRIGGER fail_scan_start BEFORE INSERT ON processor_meta WHEN NEW.key='workspace_observation_attempt_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, source.Captures); Assert.AreEqual(1, source.Reads); Assert.IsNotNull(scanner.Failure);
        ObservationSql(fixture, "DROP TRIGGER fail_scan_start");
        source.FailFinal = true;
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(repository.LastAttempt()!.Completed); Assert.AreEqual(completed, repository.LastCheckpoint());
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.AssertCompletedScan(completed));
        source.FailFinal = false; var fresh = await scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None).ConfigureAwait(false);
        repository.AssertCompletedScan(fresh); Assert.IsNull(scanner.Failure); Assert.AreEqual(3, source.Captures);
    }

    [TestMethod]
    public async Task FinalAccessInspectionRunsBeforeTheLastChangeFenceAndCannotAdvanceAFailedScan()
    {
        await using var fixture = new Fixture(); var source = new ScanSource(); source.Add("policy-A");
        await using var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, ScanContext(fixture), source);
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None, _ =>
        {
            Assert.AreEqual(1, source.Reads); Assert.AreEqual(0, source.FinalChecks); source.FailFinal = true; return Task.CompletedTask;
        })).ConfigureAwait(false);
        Assert.IsNull(new WorkspaceObservationRepository(fixture.Database, fixture.Budget).LastCheckpoint());
        Assert.AreEqual(1, new WorkspaceObservationRepository(fixture.Database, fixture.Budget).Known(ScanContext(fixture)).Count);
        source.FailFinal = false;
        Assert.AreEqual(1, (await scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None, _ => Task.CompletedTask).ConfigureAwait(false)).Documents);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObservationRetirementJoinsFinalInspectionAndCannotAcceptItsLateAccessApproval(bool exit)
    {
        await using var fixture = new Fixture(); var source = new ScanSource(); source.Add("policy-A");
        await using var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, ScanContext(fixture), source);
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanning = scanner.ScanAsync((_, _) => Task.CompletedTask, caller.Token, async _ => { entered.TrySetResult(); await release.Task.ConfigureAwait(false); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); Task stopping = Task.CompletedTask;
        if (exit) stopping = scanner.DisposeAsync().AsTask(); else await caller.CancelAsync().ConfigureAwait(false);
        try { Assert.IsFalse(scanning.IsCompleted); if (exit) Assert.IsFalse(stopping.IsCompleted); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => scanning).ConfigureAwait(false); await stopping.ConfigureAwait(false);
        Assert.AreEqual(0, source.FinalChecks); Assert.IsNull(new WorkspaceObservationRepository(fixture.Database, fixture.Budget).LastCheckpoint());
        Assert.AreEqual(1, new WorkspaceObservationRepository(fixture.Database, fixture.Budget).Known(ScanContext(fixture)).Count);
    }

    [TestMethod]
    public async Task EveryScanReadsCurrentDocumentsAndPersistsObservationBeforePolicyCallback()
    {
        await using var fixture = new Fixture(); var context = ScanContext(fixture); var source = new ScanSource(); source.Add("policy-A"); source.Add("policy-B");
        WorkspaceInventoryCheckpoint first;
        await using (var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, context, source))
        {
            first = await scanner.ScanAsync(async (document, token) =>
            {
                var retained = await fixture.State.ExecuteAsync(database => new WorkspaceObservationRepository(database, fixture.Budget).Known(context), token).ConfigureAwait(false);
                Assert.IsTrue(retained.Any(observation => observation.FileId == document.FileId));
            }, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, first.Documents); Assert.IsNull(scanner.Failure);
        }
        await using var restarted = new WorkspaceObservationService(fixture.State, fixture.Budget, context, source);
        var second = await restarted.ScanAsync((_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, source.Captures); Assert.AreEqual(4, source.Reads); Assert.AreEqual(2, source.FinalChecks);
        CollectionAssert.AreEquivalent(BothScanPolicies, source.LastKnown.ToArray()); Assert.AreEqual(first.Revision + 1, second.Revision);
    }

    [TestMethod]
    public async Task FailedDurableObservationNeverReachesVerifierOrAdvancesCursor()
    {
        await using var fixture = new Fixture(); var source = new ScanSource(); source.Add("policy-A"); var callbacks = 0;
        ObservationSql(fixture, "CREATE TRIGGER fail_read BEFORE INSERT ON workspace_document_observation BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        await using var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, ScanContext(fixture), source);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => scanner.ScanAsync((_, _) => { callbacks++; return Task.CompletedTask; }, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, callbacks); Assert.AreEqual(0, source.FinalChecks); Assert.IsNotNull(scanner.Failure);
        Assert.IsNull(new WorkspaceObservationRepository(fixture.Database, fixture.Budget).LastCheckpoint());
        ObservationSql(fixture, "DROP TRIGGER fail_read");
        var completed = await scanner.ScanAsync((_, _) => { callbacks++; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, callbacks); Assert.AreEqual(1, completed.Documents); Assert.IsNull(scanner.Failure); Assert.AreEqual(2, source.Captures);
    }

    [TestMethod]
    public async Task PolicyPersistenceAndFinalChangeFailuresKeepEarlierCursorAndNewKnownEvidence()
    {
        await using var fixture = new Fixture(); var source = new ScanSource(); source.Add("policy-A");
        await using var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, ScanContext(fixture), source);
        var first = await scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None).ConfigureAwait(false); source.Add("policy-B");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scanner.ScanAsync((_, _) => throw new InvalidOperationException("Synthetic failed accepted-policy write"), CancellationToken.None)).ConfigureAwait(false);
        var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); Assert.AreEqual(first, repository.LastCheckpoint());
        source.FailFinal = true;
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2, repository.Known(ScanContext(fixture)).Count); Assert.AreEqual(first, repository.LastCheckpoint());
        source.FailFinal = false; var recovered = await scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Revision + 1, recovered.Revision); Assert.AreEqual(2, recovered.Documents); Assert.IsNull(scanner.Failure);
    }

    [TestMethod]
    public async Task MissingOldDocumentsDoNotHideANewRevocationOrResetAcceptedInventory()
    {
        await using var fixture = new Fixture(); var source = new ScanSource(); source.Add("policy-A");
        await using var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, ScanContext(fixture), source);
        var first = await scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None).ConfigureAwait(false);
        source.Documents.Remove("policy-A"); source.Add("revocation-B"); var seen = new List<string>();
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => scanner.ScanAsync((document, _) => { seen.Add(document.FileId); return Task.CompletedTask; }, CancellationToken.None)).ConfigureAwait(false);
        CollectionAssert.AreEqual(NewScanRevocation, seen); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        Assert.AreEqual(2, repository.Known(ScanContext(fixture)).Count); Assert.AreEqual(first, repository.LastCheckpoint()); Assert.AreEqual(1, source.FinalChecks);
    }

    [TestMethod]
    public async Task ObservationExitJoinsUncooperativeReadBeforeAccountAndStateMayRetire()
    {
        await using var fixture = new Fixture(); var source = new ScanSource(); source.Add("policy-A");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.BeforeRead = async () => { entered.TrySetResult(); await release.Task.ConfigureAwait(false); };
        await using var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, ScanContext(fixture), source);
        var running = scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None); Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = scanner.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(scanner.ExitConfirmed);
            Assert.AreEqual(0, (await fixture.State.ExecuteAsync(database => new WorkspaceObservationRepository(database, fixture.Budget).Known(ScanContext(fixture)), CancellationToken.None).ConfigureAwait(false)).Count);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => running).ConfigureAwait(false); Assert.IsNotNull(stopping); await stopping.ConfigureAwait(false);
        Assert.IsTrue(scanner.ExitConfirmed); Assert.IsNull(new WorkspaceObservationRepository(fixture.Database, fixture.Budget).LastCheckpoint());
        Assert.AreEqual(0, new WorkspaceObservationRepository(fixture.Database, fixture.Budget).Known(ScanContext(fixture)).Count);
        Assert.AreEqual("policy-A", new WorkspaceObservationRepository(fixture.Database, fixture.Budget).KnownIdentities(ScanContext(fixture)).Single());
        Assert.IsNull(new WorkspaceObservationRepository(fixture.Database, fixture.Budget).LastAttempt()!.Completed);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SubstitutedDocumentAndFinalCursorCannotBecomeAcceptedObservations()
    {
        await using var fixture = new Fixture(); var source = new ScanSource(); source.Add("policy-A"); var called = false;
        await using var scanner = new WorkspaceObservationService(fixture.State, fixture.Budget, ScanContext(fixture), source);
        source.SubstituteAccount = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scanner.ScanAsync((_, _) => { called = true; return Task.CompletedTask; }, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsFalse(called); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); Assert.AreEqual(0, repository.Known(ScanContext(fixture)).Count);
        source.SubstituteAccount = false; source.ForeignCursor = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, repository.Known(ScanContext(fixture)).Count); Assert.IsNull(repository.LastCheckpoint());
    }

    private static WorkspaceObservationContext ScanContext(Fixture fixture) => new(fixture.Scope.LibraryId, fixture.Scope.AccountId, fixture.Scope.FolderId, fixture.Scope.AccountRevision, null);
    private static void ObservationSql(Fixture fixture, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(fixture.Path, "processor.db"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private sealed class ScanSource : IWorkspaceDocumentSource
    {
        public Dictionary<string, DriveWorkspaceObject> Documents { get; } = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> LastKnown { get; private set; } = [];
        public Func<Task>? BeforeRead { get; set; }
        public bool FailFinal { get; set; }
        public bool SubstituteAccount { get; set; }
        public bool ForeignCursor { get; set; }
        public int Captures { get; private set; }
        public int Reads { get; private set; }
        public int FinalChecks { get; private set; }
        public void Add(string id) => Documents.Add(id, new("account-A", id, "metadata-workspace", 1, "{}"u8.ToArray()));
        public Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Captures++; LastKnown = previouslyKnownIds.ToArray();
            var candidates = Documents.Values.OrderBy(value => value.FileId, StringComparer.Ordinal).Select(value => new DriveWorkspaceDocumentCandidate(value.FileId, value.DriveVersion, value.Bytes.Length)).ToList().AsReadOnly();
            return Task.FromResult(new WorkspaceDocumentInventory("account-A", "metadata-workspace", null, candidates,
                previouslyKnownIds.Except(Documents.Keys, StringComparer.Ordinal).ToList().AsReadOnly(), new("account-A", null, "start-" + Captures)));
        }
        public Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); FinalChecks++; Assert.AreEqual("metadata-workspace", inventory.FolderId);
            if (FailFinal) throw new WorkspaceInventoryIncompleteException(); return Task.FromResult(new DriveChangeCursor(ForeignCursor ? "foreign" : "account-A", null, "end-" + Captures));
        }
        public async Task<DriveWorkspaceObject> ReadAsync(DriveWorkspaceDocumentCandidate candidate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++; if (BeforeRead is not null) await BeforeRead().ConfigureAwait(false);
            var result = Documents[candidate.FileId]; return SubstituteAccount ? result with { AccountId = "foreign" } : result;
        }
    }
}
