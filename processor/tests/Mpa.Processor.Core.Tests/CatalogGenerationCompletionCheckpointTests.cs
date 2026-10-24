using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void FailedVerificationAndHeadWritesPreserveTheirOriginalPhaseReceiptAndExclusiveClaim()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true); var captured = LocalCompletionEvidence(fixture);
        var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget); var id = captured.Publication.Capture.Scope.GenerationId;
        fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => journal.ConfirmCurrentCommitVerified(id, captured.Checkpoint, captured.Commit));
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        Assert.IsNull(journal.Verification(id)); Assert.AreEqual("commit-sealed", journal.Read(id)!.Phase); Assert.IsNull(journal.PublishedHead());
        var receipt = journal.ConfirmCurrentCommitVerified(id, captured.Checkpoint, captured.Commit);
        fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => journal.CompleteCurrentGeneration(id, captured.Checkpoint, captured.Commit));
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        Assert.AreEqual(receipt, journal.Verification(id)); Assert.AreEqual("commit-verified", journal.Read(id)!.Phase); Assert.IsNull(journal.PublishedHead());
        fixture.Sql("CREATE TRIGGER fail_completed_head BEFORE INSERT ON processor_meta WHEN NEW.key='catalog_published_head_v1' BEGIN SELECT RAISE(ABORT,'synthetic failed head write'); END");
        Assert.ThrowsExactly<SqliteException>(() => journal.CompleteCurrentGeneration(id, captured.Checkpoint, captured.Commit));
        Assert.AreEqual(receipt, journal.Verification(id)); Assert.AreEqual("commit-verified", journal.Active().Single().Phase); Assert.IsNull(journal.PublishedHead());
        fixture.Sql("DROP TRIGGER fail_completed_head"); var head = journal.CompleteCurrentGeneration(id, captured.Checkpoint, captured.Commit);
        Assert.AreEqual(captured.Commit, head.Commit); Assert.AreEqual("complete", journal.Active().Single().Phase); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        Assert.ThrowsExactly<CatalogGenerationInProgressException>(() => journal.Begin(captured.Publication.Capture.Scope with { GenerationId = Guid.NewGuid() }, Guid.NewGuid(), Guid.NewGuid()));
    }

    [TestMethod]
    public void ANewIncompleteActualScanInvalidatesCompletionButCannotRewriteItsHistoricalVerification()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true); var captured = LocalCompletionEvidence(fixture);
        var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget); var id = captured.Publication.Capture.Scope.GenerationId;
        var receipt = journal.ConfirmCurrentCommitVerified(id, captured.Checkpoint, captured.Commit);
        var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); var attempt = observations.BeginScan(captured.Checkpoint.Context);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.CompleteCurrentGeneration(id, captured.Checkpoint, captured.Commit));
        Assert.AreEqual(receipt, journal.Verification(id)); Assert.AreEqual("commit-verified", journal.Read(id)!.Phase); Assert.IsNull(journal.PublishedHead());
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.BeginSnapshotRetirement(id));
        var checkpoint = observations.CompleteScan(attempt, "current-after-interruption", observations.Known(attempt.Context));
        var head = journal.CompleteCurrentGeneration(id, checkpoint, captured.Commit); Assert.AreEqual(captured.Commit, head.Commit);
        Assert.AreEqual(receipt, journal.Verification(id)); Assert.AreEqual(captured.Checkpoint, journal.Verification(id)!.Observation);
    }

    [TestMethod]
    public void CorruptVerificationCannotGrantCompletionOrPrivateSnapshotRetirement()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true); var captured = LocalCompletionEvidence(fixture);
        var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget); var id = captured.Publication.Capture.Scope.GenerationId;
        var receipt = journal.ConfirmCurrentCommitVerified(id, captured.Checkpoint, captured.Commit);
        var digest = (string)fixture.Scalar("SELECT document_hash FROM catalog_generation_verification");
        fixture.Sql("UPDATE catalog_generation_verification SET document_hash='" + new string('0', 64) + "'");
        Assert.ThrowsExactly<InvalidDataException>(() => journal.Read(id)); Assert.ThrowsExactly<InvalidDataException>(() => journal.Active());
        Assert.ThrowsExactly<InvalidDataException>(() => journal.CompleteCurrentGeneration(id, captured.Checkpoint, captured.Commit));
        Assert.ThrowsExactly<InvalidDataException>(() => journal.BeginSnapshotRetirement(id));
        fixture.Sql("UPDATE catalog_generation_verification SET document_hash='" + digest + "'");
        Assert.AreEqual(receipt, journal.Verification(id)); Assert.AreEqual("commit-verified", journal.Active().Single().Phase);
    }

    private static (CapturedCatalogPublication Publication, WorkspaceInventoryCheckpoint Checkpoint, CatalogObjectReference Commit) LocalCompletionEvidence(Fixture fixture)
    {
        // SQLite checkpoint fixture only: complete actual cloud/graph/current authority
        // is exercised by the concrete application-owner integration suite.
        var publication = SealCompletionFixture(fixture); var capture = publication.Capture;
        var intent = new ProtocolSigningJournal(fixture.Database, fixture.Budget).Read(capture.CommitIntentId)!;
        var outbox = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        var commit = outbox.Prepare(intent.Id, capture.Scope, "catalog-commit", "application/json", intent.Envelope!.Value.Span);
        outbox.AssignFileId(commit.Id, commit.Sha256, "completed-commit");
        commit = outbox.ConfirmVerified(commit.Id, capture.Scope.AccountId, capture.Scope.FolderId, "completed-commit", commit.Sha256, commit.Payload.Length, 1);
        var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var context = new WorkspaceObservationContext(capture.Scope.LibraryId, capture.Scope.AccountId, capture.Scope.FolderId, capture.Scope.AccountRevision, null);
        var attempt = observations.BeginScan(context); var actual = new List<WorkspaceDocumentObservation>();
        foreach (var id in outbox.Identities())
        {
            var item = outbox.Read(id)!; var observed = new WorkspaceDocumentObservation(item.FileId!, item.VerifiedVersion!.Value, item.Payload.Length, item.Sha256);
            observations.RememberRead(context, observed); actual.Add(observed);
        }
        var checkpoint = observations.CompleteScan(attempt, "actual-unit-checkpoint", actual);
        return (publication, checkpoint, new(capture.Scope.AccountId, commit.FileId!, capture.Scope.FolderId, commit.Payload.Length, commit.Sha256));
    }
}
