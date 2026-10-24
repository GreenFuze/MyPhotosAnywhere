using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    public async Task CompletedDependencyReuseRequiresActualReadAndPreservesItsOriginalScopeWithoutNewWork()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var reads = ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture); var first = await CompleteReusePredecessorAsync(fixture, current).ConfigureAwait(false);
        var next = await CaptureDependencyBasisAsync(fixture, current).ConfigureAwait(false);
        var before = (fixture.Allocations, fixture.Creates, Reads: reads.Count);
        var original = await ReuseOutboxAsync(fixture, first.Plan!.RootIndex.FileId).ConfigureAwait(false);
        var actual = await new ReconciledCatalogGenerationSink(current, next.Capture.Scope)
            .StoreAsync(original.Role, original.MimeType, original.Payload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Plan.RootIndex, actual); Assert.AreEqual(before.Allocations, fixture.Allocations); Assert.AreEqual(before.Creates, fixture.Creates);
        Assert.IsTrue(reads.Count > before.Reads); Assert.IsNull(current.Failure);
        var retained = await ReuseOutboxAsync(fixture, original.FileId!).ConfigureAwait(false);
        Assert.AreEqual(original.Scope, retained.Scope); Assert.AreEqual(original.Id, retained.Id); Assert.AreEqual(original.FileId, retained.FileId);
        CollectionAssert.AreEqual(original.Payload.ToArray(), retained.Payload.ToArray());
        await fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            Assert.IsFalse(new WorkspacePublicationRepository(db, fixture.Budget).Identities(4096)
                .Select(id => new WorkspacePublicationRepository(db, fixture.Budget).Read(id)!).Any(item => item.Scope.GenerationId == next.Capture.Scope.GenerationId));
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    public async Task CapturedCurrentDependencyIdentityCannotBeReplacedByEqualCompletedBytes()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture); var first = await CompleteReusePredecessorAsync(fixture, current).ConfigureAwait(false);
        var next = await CaptureDependencyBasisAsync(fixture, current).ConfigureAwait(false);
        var old = await ReuseOutboxAsync(fixture, first.Plan!.RootIndex.FileId).ConfigureAwait(false);
        var id = ReuseContractId(next.Capture.Scope, old); var target = "original-pending-reuse-target";
        await fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            var outbox = new WorkspacePublicationRepository(db, fixture.Budget);
            _ = outbox.Prepare(id, next.Capture.Scope, old.Role, old.MimeType, old.Payload.Span);
            return outbox.AssignFileId(id, old.Sha256, target);
        }, CancellationToken.None).ConfigureAwait(false);
        var before = (fixture.Allocations, fixture.Creates);
        var result = await new ReconciledCatalogGenerationSink(current, next.Capture.Scope)
            .StoreAsync(old.Role, old.MimeType, old.Payload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(target, result.FileId); Assert.AreNotEqual(old.FileId, result.FileId);
        Assert.AreEqual(before.Allocations, fixture.Allocations); Assert.AreEqual(before.Creates + 1, fixture.Creates);
        var retained = await ReuseOutboxAsync(fixture, target).ConfigureAwait(false);
        Assert.AreEqual(id, retained.Id); Assert.AreEqual(next.Capture.Scope, retained.Scope); Assert.IsNotNull(retained.VerifiedVersion);
    }

    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    public async Task EqualCompletedBytesInAnotherRoleCannotAliasTheNewDependencyRole()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture); var first = await CompleteReusePredecessorAsync(fixture, current).ConfigureAwait(false);
        var next = await CaptureDependencyBasisAsync(fixture, current).ConfigureAwait(false);
        var old = await ReuseOutboxAsync(fixture, first.Plan!.RootIndex.FileId).ConfigureAwait(false); var before = fixture.Creates;
        await fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            Assert.ThrowsExactly<InvalidDataException>(() => new CatalogGenerationCaptureRepository(db, fixture.Budget)
                .SealDependencies(next.Capture.Scope.GenerationId, new(next.Basis, first.Plan.RootIndex, first.Plan.SourceCoverage)));
            Assert.IsNull(new ProtocolSigningJournal(db, fixture.Budget).Read(next.Capture.CommitIntentId));
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        var result = await new ReconciledCatalogGenerationSink(current, next.Capture.Scope)
            .StoreAsync("coverage-root", old.MimeType, old.Payload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(old.FileId, result.FileId); Assert.AreEqual(before + 1, fixture.Creates);
        Assert.AreEqual("coverage-root", (await ReuseOutboxAsync(fixture, result.FileId).ConfigureAwait(false)).Role);
    }

    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    public async Task DependencyReuseExitJoinsActualHeldReadAndNeverCreatesReplacementWork()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture); var first = await CompleteReusePredecessorAsync(fixture, current).ConfigureAwait(false);
        var next = await CaptureDependencyBasisAsync(fixture, current).ConfigureAwait(false);
        var old = await ReuseOutboxAsync(fixture, first.Plan!.RootIndex.FileId).ConfigureAwait(false); var before = (fixture.Creates, fixture.Allocations);
        var read = fixture.Setup.Accounts.Readers.WorkspaceRead!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceRead = async (folder, file, bytes, hash, token) =>
        {
            var result = await read(folder, file, bytes, hash, token).ConfigureAwait(false);
            if (file == old.FileId) { entered.TrySetResult(); await release.Task.ConfigureAwait(false); }
            return result;
        };
        var work = new ReconciledCatalogGenerationSink(current, next.Capture.Scope).StoreAsync(old.Role, old.MimeType, old.Payload, CancellationToken.None); Task stopping;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = current.DisposeAsync().AsTask();
            Assert.IsFalse(work.IsCompleted); Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(current.ExitConfirmed);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => work).ConfigureAwait(false); await stopping.ConfigureAwait(false);
        Assert.IsTrue(current.ExitConfirmed); Assert.IsNull(current.Current); Assert.AreEqual(before, (fixture.Creates, fixture.Allocations));
    }

    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    public async Task FullNextGenerationCanPublishReferencesToCompletedImmutableDependencies()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current);
        var first = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var head = await completion.FinishAsync(first.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        var before = (fixture.Creates, fixture.Allocations); var next = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(first.Plan); Assert.IsNotNull(next.Plan); Assert.AreEqual(first.Plan.RootIndex, next.Plan.RootIndex);
        Assert.AreEqual(before, (fixture.Creates, fixture.Allocations)); Assert.AreNotEqual(first.Capture.Scope.GenerationId, next.Capture.Scope.GenerationId);
        var published = await completion.FinishAsync(next.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(head.Sequence + 1, published.Sequence); Assert.AreEqual(0L, published.Assets);
        Assert.AreEqual(before.Creates + 1, fixture.Creates); Assert.AreEqual(before.Allocations + 1, fixture.Allocations);
        await AssertRetiredCompletionAsync(fixture, published).ConfigureAwait(false);
        // A third generation proves that a reference to a generation which itself
        // reused earlier dependencies does not recurse or duplicate those objects.
        var third = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Plan.RootIndex, third.Plan!.RootIndex);
        var final = await completion.FinishAsync(third.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(published.Sequence + 1, final.Sequence);
        Assert.AreEqual(before.Creates + 2, fixture.Creates); Assert.AreEqual(before.Allocations + 2, fixture.Allocations);
        await AssertRetiredCompletionAsync(fixture, final).ConfigureAwait(false);
    }

    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    public async Task DependencyReuseFailedReadRetainsExactSelectionAndRecreatedOwnerResumesIt()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        CapturedCatalogPublication next; WorkspacePublicationRecord original; CatalogDependencyReference selected;
        var read = fixture.Setup.Accounts.Readers.WorkspaceRead!;
        await using (var current = CatalogReconciler(fixture))
        {
            var first = await CompleteReusePredecessorAsync(fixture, current).ConfigureAwait(false);
            next = await CaptureDependencyBasisAsync(fixture, current).ConfigureAwait(false);
            original = await ReuseOutboxAsync(fixture, first.Plan!.RootIndex.FileId).ConfigureAwait(false);
            fixture.Setup.Accounts.Readers.WorkspaceRead = (folder, file, bytes, hash, token) =>
                file == original.FileId ? throw new HttpRequestException("Synthetic selected dependency read failure.") : read(folder, file, bytes, hash, token);
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new ReconciledCatalogGenerationSink(current, next.Capture.Scope)
                .StoreAsync(original.Role, original.MimeType, original.Payload, CancellationToken.None)).ConfigureAwait(false);
            selected = await fixture.Setup.Accounts.State.ExecuteAsync(db =>
            {
                var saved = new CatalogDependencyReferenceRepository(db, fixture.Budget).Read(ReuseContractId(next.Capture.Scope, original))!;
                Assert.IsNotNull(saved); Assert.AreEqual(original.FileId, saved.FileId); Assert.AreEqual(original.Id, saved.OriginalPublicationId);
                Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => new WorkspacePublicationRepository(db, fixture.Budget)
                    .Prepare(saved.Id, saved.Scope, saved.Role, saved.MimeType, original.Payload.Span));
                return saved;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        var before = (fixture.Creates, fixture.Allocations);
        fixture.Setup.Accounts.Readers.WorkspaceRead = read;
        await using var resumed = CatalogReconciler(fixture);
        var actual = await new ReconciledCatalogGenerationSink(resumed, next.Capture.Scope)
            .StoreAsync(original.Role, original.MimeType, original.Payload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(selected.FileId, actual.FileId); Assert.AreEqual(before, (fixture.Creates, fixture.Allocations));
        Assert.AreEqual(selected, await fixture.Setup.Accounts.State.ExecuteAsync(db =>
            new CatalogDependencyReferenceRepository(db, fixture.Budget).Read(selected.Id), CancellationToken.None).ConfigureAwait(false));
    }

    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DependencyReuseFailedNativeCheckpointRollsBackWithoutAllocatingOrLosingOriginal(bool lowStorage)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture); var first = await CompleteReusePredecessorAsync(fixture, current).ConfigureAwait(false);
        var next = await CaptureDependencyBasisAsync(fixture, current).ConfigureAwait(false);
        var original = await ReuseOutboxAsync(fixture, first.Plan!.RootIndex.FileId).ConfigureAwait(false);
        var proof = await current.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        var before = (fixture.Creates, fixture.Allocations); var id = ReuseContractId(next.Capture.Scope, original);
        if (!lowStorage) await CompletionSqlAsync(fixture,
            "CREATE TRIGGER fail_dependency_reference BEFORE INSERT ON processor_meta WHEN NEW.key GLOB 'catalog_dependency_reference_v1:*' BEGIN SELECT RAISE(ABORT,'synthetic reference checkpoint failure'); END").ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            var budget = lowStorage ? new StorageBudgetManager(new RetirementNoSpace()) : fixture.Budget;
            var references = new CatalogDependencyReferenceRepository(db, budget);
            void Write() => references.Record(id, next.Capture.Scope, original.Id, proof.Observation,
                new WorkspaceObservationRepository(db, fixture.Budget).LastAttempt()!.Id);
            if (lowStorage) Assert.ThrowsExactly<InsufficientStorageBudgetException>(Write);
            else Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(Write);
            Assert.IsNull(references.Read(id)); Assert.AreEqual(0L, budget.Status().ReservedBytes);
            Assert.AreEqual("publication-planned", new CatalogGenerationCaptureRepository(db, fixture.Budget).Read(next.Capture.Scope.GenerationId)!.Phase);
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        if (!lowStorage) await CompletionSqlAsync(fixture, "DROP TRIGGER fail_dependency_reference").ConfigureAwait(false);
        var actual = await new ReconciledCatalogGenerationSink(current, next.Capture.Scope)
            .StoreAsync(original.Role, original.MimeType, original.Payload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(original.FileId, actual.FileId); Assert.AreEqual(before, (fixture.Creates, fixture.Allocations));
    }

    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    [DataRow("reference")]
    [DataRow("provenance")]
    [DataRow("payload")]
    public async Task DependencyReuseDamagedNativeEvidenceFailsClosedWithoutReplacement(string damage)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture); var first = await CompleteReusePredecessorAsync(fixture, current).ConfigureAwait(false);
        var next = await CaptureDependencyBasisAsync(fixture, current).ConfigureAwait(false);
        var original = await ReuseOutboxAsync(fixture, first.Plan!.RootIndex.FileId).ConfigureAwait(false);
        _ = await new ReconciledCatalogGenerationSink(current, next.Capture.Scope)
            .StoreAsync(original.Role, original.MimeType, original.Payload, CancellationToken.None).ConfigureAwait(false);
        var before = (fixture.Creates, fixture.Allocations);
        var sql = damage switch
        {
            "reference" => "UPDATE processor_meta SET value='{}' WHERE key GLOB 'catalog_dependency_reference_v1:*'",
            "provenance" => "UPDATE catalog_generation_verification SET document_hash='" + new string('0', 64) + "'",
            "payload" => "UPDATE workspace_publication SET payload=X'00' WHERE role='facts-index'",
            _ => throw new AssertFailedException("Unknown corruption fixture.")
        };
        await CompletionSqlAsync(fixture, sql).ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            var references = new CatalogDependencyReferenceRepository(db, fixture.Budget); var id = ReuseContractId(next.Capture.Scope, original);
            if (damage == "provenance") Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => references.Read(id));
            else Assert.ThrowsExactly<InvalidDataException>(() => references.Read(id));
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before, (fixture.Creates, fixture.Allocations));
    }

    [TestCategory("ImmutableDependencyReuse")]
    [TestMethod]
    public async Task DependencyReuseMissingOriginalCannotBeReplacedByIdenticalCloudCopy()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture); var first = await CompleteReusePredecessorAsync(fixture, current).ConfigureAwait(false);
        var next = await CaptureDependencyBasisAsync(fixture, current).ConfigureAwait(false);
        var original = await ReuseOutboxAsync(fixture, first.Plan!.RootIndex.FileId).ConfigureAwait(false);
        _ = await new ReconciledCatalogGenerationSink(current, next.Capture.Scope)
            .StoreAsync(original.Role, original.MimeType, original.Payload, CancellationToken.None).ConfigureAwait(false);
        var before = (fixture.Creates, fixture.Allocations); var saved = fixture.Cloud[original.FileId!];
        fixture.Cloud.Remove(original.FileId!); fixture.Cloud.Add("identical-replacement", saved with { FileId = "identical-replacement" });
        fixture.MetadataMimeTypes.Add("identical-replacement", original.MimeType);
        var read = fixture.Setup.Accounts.Readers.WorkspaceRead!; var missingReads = 0;
        fixture.Setup.Accounts.Readers.WorkspaceRead = (folder, file, bytes, hash, token) =>
        {
            if (file == original.FileId)
            {
                missingReads++;
                throw new HttpRequestException("Synthetic Drive metadata HTTP 404.", null, System.Net.HttpStatusCode.NotFound);
            }
            Assert.AreNotEqual("identical-replacement", file, "Equal bytes cannot retarget the selected immutable reference.");
            return read(folder, file, bytes, hash, token);
        };
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new ReconciledCatalogGenerationSink(current, next.Capture.Scope)
            .StoreAsync(original.Role, original.MimeType, original.Payload, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, error.StatusCode); Assert.AreEqual(1, missingReads);
        Assert.IsNull(current.Current); Assert.AreEqual(before, (fixture.Creates, fixture.Allocations));
        var selected = await fixture.Setup.Accounts.State.ExecuteAsync(db => new CatalogDependencyReferenceRepository(db, fixture.Budget)
            .Read(ReuseContractId(next.Capture.Scope, original)), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(original.FileId, selected!.FileId); Assert.AreEqual(original.Id, selected.OriginalPublicationId);
    }

    private static async Task<CapturedCatalogPublication> CompleteReusePredecessorAsync(InitialPublicationFixture fixture, LocalCatalogReconciliationService current)
    {
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current);
        var prepared = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await completion.FinishAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false); return prepared;
    }
    private static Task<WorkspacePublicationRecord> ReuseOutboxAsync(InitialPublicationFixture fixture, string file) => fixture.Setup.Accounts.State.ExecuteAsync(db =>
    {
        var outbox = new WorkspacePublicationRepository(db, fixture.Budget);
        return outbox.Identities(4096).Select(id => outbox.Read(id)!).Single(item => item.FileId == file);
    }, CancellationToken.None);
    private static Guid ReuseContractId(WorkspacePublicationScope scope, WorkspacePublicationRecord original)
    {
        var bytes = new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        { format = "mpa-catalog-dependency-outbox", major = 1, scope, role = original.Role, mimeType = original.MimeType, payloadSha256 = original.Sha256 }));
        var hash = SHA256.HashData(bytes); hash[6] = (byte)((hash[6] & 15) | 128); hash[8] = (byte)((hash[8] & 63) | 128);
        return new(hash.AsSpan(0, 16), bigEndian: true);
    }
}
