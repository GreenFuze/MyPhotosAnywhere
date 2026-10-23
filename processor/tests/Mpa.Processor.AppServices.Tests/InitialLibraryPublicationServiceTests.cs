using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task InitialPublicationUsesActualJoinedAccountScansAndObservesBothPoliciesAfterWriting()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => fixture.Owner.PublishAsync(CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.AreEqual(2, fixture.Allocations); Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Cloud.Count);
        Assert.IsTrue(results.All(result => result.Plan == results[0].Plan));
        Assert.IsTrue(results.All(result => result.Publisher.VerifiedVersion == 7 && result.Sources.VerifiedVersion == 7 && result.Observation.Documents == 2));
        Assert.IsTrue(results.All(result => result.Completion == results[0].Completion));
        Assert.AreEqual(results[0].Plan, results[0].Completion.Plan);
        Assert.AreEqual(results[0].Completion, await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).InitialPublicationCompletion(), CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue(fixture.InitialInventories >= 5); Assert.AreEqual(1, fixture.Setup.Accounts.Readers.Opened.Count);
        Assert.IsNull(fixture.Owner.Failure); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        await fixture.Owner.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(fixture.Owner.ExitConfirmed);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InitialPublicationRecoversLostCreateReplyUsingOnlyTheOriginallyAllocatedIdentity()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); fixture.LoseFirstReply = true;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        var pending = await fixture.PreparedAsync().ConfigureAwait(false); Assert.IsNotNull(pending.Publisher.FileId); Assert.IsNull(pending.Publisher.VerifiedVersion);
        Assert.AreEqual(1, fixture.Cloud.Count); Assert.AreEqual(1, fixture.Allocations);
        await fixture.Owner.DisposeAsync().ConfigureAwait(false); fixture.ReopenOwner();
        var resumed = await fixture.Owner.PublishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.AreEqual(pending.Plan, resumed.Plan); Assert.AreEqual(pending.Publisher.FileId, resumed.Publisher.FileId);
        Assert.AreEqual(2, fixture.Allocations); Assert.AreEqual(3, fixture.Creates); Assert.AreEqual(2, resumed.Observation.Documents);
        CollectionAssert.AreEqual(pending.Publisher.Payload.ToArray(), fixture.Cloud[pending.Publisher.FileId].Bytes.ToArray());
    }

    [TestMethod]
    public async Task InitialPublicationCannotAdoptAnUnallocatedCloudCopyEvenWithTheExactSealedPolicyBytes()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); var prepared = await fixture.PreparedAsync().ConfigureAwait(false);
        fixture.Cloud.Add("unapproved-copy", new("permission-A", "unapproved-copy", "metadata", 7, prepared.Publisher.Payload));
        await Assert.ThrowsExactlyAsync<LibraryInitialPublicationConflictException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations); Assert.AreEqual(1, fixture.Cloud.Count);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspaceObservationRepository(database, fixture.Budget)
            .Known(new(prepared.Plan.Scope.LibraryId, "permission-A", "metadata", 1, null)), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("unapproved-copy", retained.Single().FileId); // A rejected actual read is retained, not erased to enable fallback.
    }

    [TestMethod]
    public async Task MissingVerifiedButNotYetObservedPolicyCannotBeTreatedAsAnEmptyNewWorkspace()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); fixture.FailInventoryAfterFirstCreate = true;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        var prepared = await fixture.PreparedAsync().ConfigureAwait(false); Assert.AreEqual(7L, prepared.Publisher.VerifiedVersion);
        var previous = await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspaceObservationRepository(database, fixture.Budget).LastCheckpoint(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(previous); Assert.AreEqual(0, previous.Documents);
        fixture.Cloud.Clear(); fixture.FailInventoryAfterFirstCreate = false;
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Creates); Assert.AreEqual(1, fixture.Allocations); Assert.IsNull((await fixture.PreparedAsync().ConfigureAwait(false)).Sources.FileId);
        Assert.IsNull(await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).InitialPublicationCompletion(), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RewrittenOrMissingObservedPoliciesNeverTriggerAReplacementUpload()
    {
        foreach (var rewrite in new[] { false, true })
        {
            await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
            var result = await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var id = result.Publisher.FileId!;
            if (rewrite) fixture.Cloud[id] = fixture.Cloud[id] with { DriveVersion = 8, Bytes = "{}"u8.ToArray() }; else fixture.Cloud.Remove(id);
            await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
        }
    }

    [TestMethod]
    public async Task UnapprovedSharingOrUnsupportedChildrenCloseInitialPublicationBeforeAllocation()
    {
        foreach (var shared in new[] { false, true })
        {
            await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
            if (shared) fixture.Setup.Shared = true; else fixture.UnsupportedChild = true;
            if (shared) await Assert.ThrowsExactlyAsync<LibraryWorkspaceApprovalException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0, fixture.Allocations); Assert.AreEqual(0, fixture.Creates);
        }
    }

    [TestMethod]
    public async Task InitialAdmissionRechecksLocalSourceAuthorityAfterAllocationWithoutReenteringTheAccount()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        fixture.Setup.Accounts.Readers.WorkspaceAllocate = async _ =>
        {
            fixture.Allocations++; await fixture.Setup.AddSourceAsync("new-photos").ConfigureAwait(false); return "fixed-pending-id";
        };
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => fixture.Owner.PublishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Creates);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var plan = new LibraryAuthorityRepository(database).InitialPublicationPlan()!;
            return new WorkspacePublicationRepository(database, fixture.Budget).Read(plan.PublisherObjectId)!;
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("fixed-pending-id", retained.FileId); Assert.IsNull(retained.VerifiedVersion);
    }

    [TestMethod]
    public async Task InitialPublicationRefusesAnUnreconciledReconnectRatherThanChangingTheCapturedScope()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); var first = await fixture.PreparedAsync().ConfigureAwait(false);
        await fixture.Setup.Accounts.Service.ConnectAsync("permission-A", new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).InitialPublicationPlan(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Plan, retained); Assert.AreEqual(0, fixture.Allocations); Assert.AreEqual(0, fixture.Creates);
    }

    [TestMethod]
    public async Task InterruptedCompletionCheckpointResumesAfterRealObservationWithoutReuploadingEitherPolicy()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await InitialPublicationSqlAsync(fixture, "CREATE TRIGGER fail_completion BEFORE INSERT ON processor_meta WHEN NEW.key='initial_library_publication_complete_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic full'); END").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        var prepared = await fixture.PreparedAsync().ConfigureAwait(false);
        Assert.AreEqual(7L, prepared.Publisher.VerifiedVersion); Assert.AreEqual(7L, prepared.Sources.VerifiedVersion);
        Assert.AreEqual(2, fixture.Cloud.Count); Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
        Assert.IsNull(await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).InitialPublicationCompletion(), CancellationToken.None).ConfigureAwait(false));
        await InitialPublicationSqlAsync(fixture, "DROP TRIGGER fail_completion").ConfigureAwait(false);
        await fixture.Owner.DisposeAsync().ConfigureAwait(false); fixture.ReopenOwner();
        var resumed = await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(prepared.Plan, resumed.Completion.Plan); Assert.AreEqual(2, resumed.Completion.Observation.Documents);
        Assert.AreEqual(prepared.Publisher.FileId, resumed.Completion.Publisher.FileId); Assert.AreEqual(prepared.Sources.FileId, resumed.Completion.Sources.FileId);
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations); Assert.IsNull(fixture.Owner.Failure);
    }

    private static Task<int> InitialPublicationSqlAsync(InitialPublicationFixture fixture, string sql) => fixture.Setup.Accounts.State.ExecuteAsync(database =>
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(database.StateDirectory, "processor.db"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteNonQuery();
    }, CancellationToken.None);

    [TestMethod]
    public async Task InitialPublicationCannotIgnoreNonJsonChangesAfterItsUnfilteredInventory()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        fixture.Setup.Accounts.Readers.DocumentChanges = (_, _) => Task.FromResult(new DriveChangePage(new([
            new("file", "unexpected-original", false, new("image/jpeg", new(["metadata"]), false, 1, 1, true))]), null, new("permission-A", null, "later")));
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
    }

    [TestMethod]
    public async Task InitialPublicationChecksChangesThatArriveDuringItsFinalFolderAccessInspection()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); var inspections = 0;
        fixture.Setup.Accounts.Readers.WorkspaceInspect = (folder, _) =>
        {
            if (++inspections == 2)
                fixture.Setup.Accounts.Readers.DocumentChanges = (_, _) => Task.FromResult(new DriveChangePage(new([
                    new("file", "new-child", false, new("image/jpeg", new(["metadata"]), false, 1, 1, true))]), null, new("permission-A", null, "later")));
            return Task.FromResult(new DriveWorkspaceAccess("permission-A", folder, "Metadata", 7, false, true));
        };
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2, inspections); Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
        Assert.IsNull(await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspaceObservationRepository(database, fixture.Budget).LastCheckpoint(), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InitialPublicationPersistsAnActuallyObservedNewRevocationBeforeClosingRatherThanRestoringOldPermission()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); await fixture.PreparedAsync().ConfigureAwait(false);
        var revoked = await new JournaledProtocolSigner(fixture.Setup.Accounts.State, fixture.Setup.Keys, fixture.Budget).SignControllerAsync(Guid.NewGuid(),
            "library-publisher-authorization", (identity, sequence) => new CanonicalJson().Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "mpa", major = 1, minor = 0, libraryId = identity.LibraryId.ToString("D"), actorId = identity.ControllerId.ToString("D"),
                epoch = identity.ControllerEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), sequence = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                kind = "library-publisher-authorization", requiredCapabilities = new[] { CatalogPublisherAuthorization.Capability },
                payload = new { workspace = new { accountId = "permission-A", folderId = "metadata" }, publisher = (object?)null }
            })), CancellationToken.None).ConfigureAwait(false);
        fixture.Cloud.Add("new-revocation", new("permission-A", "new-revocation", "metadata", 7, revoked.Envelope!.Value));
        await Assert.ThrowsExactlyAsync<LibraryInitialPublicationConflictException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        var selected = await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).CurrentPublisherPolicy(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(selected); Assert.IsNull(selected.Publisher); Assert.AreEqual(revoked.EnvelopeSha256, selected.EnvelopeSha256);
        Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
    }

    [TestMethod]
    public async Task ExplicitlyApprovedSharedWorkspaceKeepsItsActualSharedDriveContextThroughBothUploads()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(shared: true).ConfigureAwait(false);
        var result = await fixture.Owner.PublishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.AreEqual("shared-drive", result.Observation.Context.SharedDriveId); Assert.AreEqual(2, result.Observation.Documents);
        Assert.AreEqual(2, fixture.Allocations); Assert.AreEqual(2, fixture.Creates);
    }

    [TestMethod]
    public async Task InitialPublicationRefusesAnIncompleteOutboxInventoryInsteadOfIgnoringAnotherProducer()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); var work = await fixture.PreparedAsync().ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, fixture.Budget)
            .Prepare(Guid.NewGuid(), work.Plan.Scope, "facts-index", "application/json", "{}"u8), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitialPublicationExitOrCallerCancellationJoinsUncooperativeCreateAndRejectsLateReceipt(bool exit)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, _, bytes, _, _) =>
        {
            fixture.Creates++; entered.TrySetResult(); await release.Task.ConfigureAwait(false);
            if (!fixture.Cloud.TryGetValue(file, out var result))
            { result = new DriveWorkspaceObject("permission-A", file, folder, 7, bytes.ToArray()); fixture.Cloud.Add(file, result); }
            CollectionAssert.AreEqual(bytes.ToArray(), result.Bytes.ToArray()); return result;
        };
        var publishing = fixture.Owner.PublishAsync(caller.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Task stopping = Task.CompletedTask;
        if (exit) stopping = fixture.Owner.DisposeAsync().AsTask(); else await caller.CancelAsync().ConfigureAwait(false);
        try { Assert.IsFalse(publishing.IsCompleted); if (exit) Assert.IsFalse(stopping.IsCompleted); Assert.AreEqual(0, fixture.Setup.Accounts.Readers.DisposalAttempts); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => publishing).ConfigureAwait(false); await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var retained = await fixture.PreparedAsync().ConfigureAwait(false);
        Assert.IsNotNull(retained.Publisher.FileId); Assert.IsNull(retained.Publisher.VerifiedVersion); Assert.IsNull(retained.Sources.FileId);
        Assert.AreEqual(1, fixture.Creates); Assert.AreEqual(1, fixture.Cloud.Count);
        if (exit) { Assert.IsTrue(fixture.Owner.ExitConfirmed); fixture.ReopenOwner(); }
        var recovered = await fixture.Owner.PublishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.AreEqual(retained.Publisher.FileId, recovered.Publisher.FileId); Assert.AreEqual(2, recovered.Observation.Documents);
    }

    private sealed class InitialPublicationFixture : IAsyncDisposable
    {
        public SetupFixture Setup { get; }
        public StorageBudgetManager Budget { get; } = PublicationBudget();
        public InitialLibraryPublicationService Owner { get; private set; }
        public Dictionary<string, DriveWorkspaceObject> Cloud { get; } = new(StringComparer.Ordinal);
        public int Allocations { get; set; }
        public int Creates { get; set; }
        public int InitialInventories { get; private set; }
        public bool LoseFirstReply { get; set; }
        public bool FailInventoryAfterFirstCreate { get; set; }
        public bool UnsupportedChild { get; set; }
        private readonly string? _sharedDriveId;
        private InitialPublicationFixture(SetupFixture setup)
        {
            Setup = setup; Owner = new(setup.Accounts.State, setup.Accounts.Service, setup.Keys, Budget); _sharedDriveId = setup.Shared ? "shared-drive" : null;
            var readers = setup.Accounts.Readers;
            readers.WorkspaceInspect = (folder, token) =>
            { token.ThrowIfCancellationRequested(); return Task.FromResult(new DriveWorkspaceAccess("permission-A", folder, "Metadata", setup.Version, setup.Shared, Cloud.Count == 0 && !UnsupportedChild)); };
            readers.FolderRead = (folder, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new DriveFolder(folder, "Metadata", true, ["parent"], _sharedDriveId)); };
            readers.DocumentStart = (drive, _) => Task.FromResult(new DriveChangeCursor("permission-A", drive, "current"));
            readers.DocumentChanges = (cursor, _) => Task.FromResult(new DriveChangePage([], null, new("permission-A", cursor.SharedDriveId, "current")));
            readers.DocumentInventory = (_, _, _, _) => throw new InvalidOperationException("Initial setup cannot use JSON-filtered discovery.");
            readers.InitialDocumentInventory = (folder, drive, next, token) =>
            {
                token.ThrowIfCancellationRequested(); InitialInventories++;
                Assert.AreEqual("metadata", folder); Assert.AreEqual(_sharedDriveId, drive); Assert.IsNull(next);
                if (UnsupportedChild) throw new InvalidDataException("Synthetic unfiltered inventory found an original/nested folder; no content was downloaded.");
                if (FailInventoryAfterFirstCreate && Creates > 0) throw new HttpRequestException("Synthetic inventory outage after receipt persistence.");
                return Task.FromResult(new DriveWorkspaceDocumentPage(new(Cloud.Values.Select(item => new DriveWorkspaceDocumentCandidate(item.FileId, item.DriveVersion, item.Bytes.Length)).ToList()), null, false));
            };
            readers.DocumentRead = (folder, candidate, token) =>
            { token.ThrowIfCancellationRequested(); Assert.AreEqual("metadata", folder); return Task.FromResult(Cloud[candidate.FileId]); };
            readers.WorkspaceAllocate = token => { token.ThrowIfCancellationRequested(); return Task.FromResult("captured-" + (++Allocations).ToString(System.Globalization.CultureInfo.InvariantCulture)); };
            readers.WorkspaceCreate = (folder, file, mime, bytes, _, token) =>
            {
                token.ThrowIfCancellationRequested(); Creates++; Assert.AreEqual("metadata", folder); Assert.AreEqual("application/json", mime);
                if (!Cloud.TryGetValue(file, out var result)) { result = new("permission-A", file, folder, 7, bytes.ToArray()); Cloud.Add(file, result); }
                CollectionAssert.AreEqual(bytes.ToArray(), result.Bytes.ToArray());
                if (LoseFirstReply && Creates == 1) throw new HttpRequestException("Synthetic lost completion reply after storage.");
                return Task.FromResult(result);
            };
        }
        public static async Task<InitialPublicationFixture> CreateAsync(bool shared = false)
        {
            var setup = await SetupFixture.CreateAsync().ConfigureAwait(false);
            try
            {
                setup.Shared = shared;
                var inspection = await setup.Setup.InspectAsync("permission-A", "metadata", CancellationToken.None).ConfigureAwait(false);
                await setup.Setup.ApproveAsync(Approval(inspection) with { AcceptSharedWorkspace = shared }, CancellationToken.None).ConfigureAwait(false);
                return new(setup);
            }
            catch { await setup.DisposeAsync().ConfigureAwait(false); throw; }
        }
        public Task<PreparedInitialLibraryPublication> PreparedAsync() => new InitialLibraryPublicationPreparation(Setup.Accounts.State, Setup.Keys, Budget).PrepareAsync(CancellationToken.None);
        public void ReopenOwner() => Owner = new(Setup.Accounts.State, Setup.Accounts.Service, Setup.Keys, Budget);
        public async ValueTask DisposeAsync() { await Owner.DisposeAsync().ConfigureAwait(false); await Setup.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed partial class LibraryFactory
    {
        public Func<string, string?, string?, CancellationToken, Task<DriveWorkspaceDocumentPage>>? InitialDocumentInventory { get; set; }
        private sealed partial class Connection : IGoogleInitialWorkspaceDiscovery
        {
            public Task<DriveWorkspaceDocumentPage> ReadInitialWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken)
            { owner.DocumentCalls++; return owner.InitialDocumentInventory?.Invoke(folderId, sharedDriveId, pageToken, cancellationToken) ?? throw new InvalidOperationException("Unexpected initial unfiltered inventory request."); }
        }
    }
}
