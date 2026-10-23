using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void InitialCompletionSurvivesRestartAndRetainsItsOriginalExactObservation()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); LibraryInitialPublicationCompletion saved;
        using (var fixture = new Fixture(root.Path))
        {
            var work = fixture.PrepareCompletionEvidence(); saved = fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint);
            Assert.AreEqual(saved, fixture.Store.InitialPublicationCompletion()); Assert.AreEqual(work.Plan, saved.Plan); Assert.AreEqual(2, saved.Observation.Documents);
            var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
            var newer = observations.Known(work.Checkpoint.Context).Select(document => document with { Version = document.Version + 1 }).ToArray();
            foreach (var document in newer) observations.RememberRead(work.Checkpoint.Context, document);
            var next = observations.Complete(work.Checkpoint.Context, "later-cursor", newer);
            Assert.AreEqual(saved, fixture.Store.CompleteInitialPublication(work.Plan, next)); // No replacement of historical evidence.
            Assert.AreEqual(work.Checkpoint, saved.Observation); Assert.AreNotEqual(next, saved.Observation);
        }
        using var database = new ProcessorStateDatabase(root.Path); var repository = new LibraryAuthorityRepository(database, new(new Space()));
        Assert.AreEqual(saved, repository.InitialPublicationCompletion());
    }

    [TestMethod]
    public void InitialCompletionRequiresBothVerifiedObjectsAndAnActualCompleteRetainedScan()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(verifySources: false);
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint)); Assert.IsNull(fixture.Store.InitialPublicationCompletion());
        var outbox = new WorkspacePublicationRepository(fixture.Database, fixture.Budget); var source = outbox.Read(work.Plan.SourceObjectId)!;
        outbox.ConfirmVerified(source.Id, source.Scope.AccountId, source.Scope.FolderId, source.FileId!, source.Sha256, source.Payload.Length, 7);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint with { Cursor = "unobserved" }));
        Assert.ThrowsExactly<LibraryInitialPublicationConflictException>(() => fixture.Store.CompleteInitialPublication(work.Plan with { SourceObjectId = Guid.NewGuid() }, work.Checkpoint));
        Assert.IsNull(fixture.Store.InitialPublicationCompletion()); Assert.IsNotNull(fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint));
    }

    [TestMethod]
    public void CompletionDoesNotInventMissingObservationsFromUploadReceipts()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence();
        fixture.Sql("DELETE FROM workspace_document_observation WHERE id='source-file'");
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint));
        Assert.IsNull(fixture.Store.InitialPublicationCompletion());
        Assert.AreEqual(2L, fixture.Scalar("SELECT count(*) FROM workspace_publication WHERE verified_version IS NOT NULL"));
    }

    [TestMethod]
    public void InitialCompletionIsReadableAfterRevocationDisconnectAndLaterCatalogWorkButGrantsNoPermission()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); var saved = fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint);
        new WorkspacePublicationRepository(fixture.Database, fixture.Budget).Prepare(Guid.NewGuid(), work.Plan.Scope, "facts-index", "application/json", "{}"u8);
        var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var later = new WorkspaceDocumentObservation("later-object", 1, 2, new string('a', 64)); observations.RememberRead(work.Checkpoint.Context, later);
        observations.Complete(work.Checkpoint.Context, "later-generation", observations.Known(work.Checkpoint.Context));
        fixture.Store.SelectPublisherPolicy(fixture.Policy(3, revoked: true)); new DriveAccountRepository(fixture.Database).Disconnect("account-A", 1);
        Assert.AreEqual(saved, fixture.Store.InitialPublicationCompletion());
        Assert.ThrowsExactly<CatalogPublicationNotAuthorizedException>(() => fixture.Store.AssertLocalPublicationScope(work.Plan.Scope));
        Assert.ThrowsExactly<LibraryInitialPolicyConflictException>(() => fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint));
        Assert.AreEqual(saved, fixture.Store.InitialPublicationCompletion());
    }

    [TestMethod]
    public void MissingCompletionOverLaterWorkCannotRestartBootstrapOrReconstructAStageReceipt()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint);
        new WorkspacePublicationRepository(fixture.Database, fixture.Budget).Prepare(Guid.NewGuid(), work.Plan.Scope, "facts-index", "application/json", "{}"u8);
        fixture.Sql("DELETE FROM processor_meta WHERE key IN('initial_library_publication_complete_v1','initial_library_publication_complete_v1:sha256')");
        Assert.ThrowsExactly<LibraryInitialPublicationCompletionMissingException>(() => fixture.Store.InitialPublicationCompletion());
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint));
        Assert.AreEqual(3L, fixture.Scalar("SELECT count(*) FROM workspace_publication"));
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key='initial_library_publication_complete_v1'"));
    }

    [TestMethod]
    public void MissingCompletionAlsoRetainsLaterUnsealedSigningWorkRatherThanTreatingItAsAnUnusedPlan()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence();
        var journal = new ProtocolSigningJournal(fixture.Database, fixture.Budget);
        var pending = journal.Reserve(Guid.NewGuid(), fixture.ActorId, 1, "library-publisher-authorization", _ => InitialPolicyBody(fixture.Policy(3)));
        Assert.IsNull(pending.Envelope);
        Assert.ThrowsExactly<LibraryInitialPublicationCompletionMissingException>(() => fixture.Store.InitialPublicationCompletion());
        Assert.ThrowsExactly<LibraryInitialPublicationCompletionMissingException>(() => fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint));
        Assert.AreEqual(pending.BodySha256, journal.Read(pending.Id)!.BodySha256);
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key='initial_library_publication_complete_v1'"));
    }

    [TestMethod]
    public void HistoricalCompletionRejectsCheckpointRevisionReuseEvenWhenTheNewRowHashIsValid()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint);
        var altered = new Mpa.Processor.Protocol.CanonicalJson(32768).Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(work.Checkpoint with { Cursor = "different-same-revision" }));
        using var connection = Open(fixture.Database.StateDirectory); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE workspace_inventory_checkpoint SET document=$document,row_hash=$hash WHERE id=1";
        command.Parameters.AddWithValue("$document", System.Text.Encoding.UTF8.GetString(altered));
        command.Parameters.AddWithValue("$hash", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(altered)));
        Assert.AreEqual(1, command.ExecuteNonQuery());
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.InitialPublicationCompletion());
    }

    [TestMethod]
    public void CompletionWriteAndHashAreAtomicUnderLowStorageAndInterruptedWrites()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint)); Assert.IsNull(fixture.Store.InitialPublicationCompletion());
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_completion BEFORE INSERT ON processor_meta WHEN NEW.key='initial_library_publication_complete_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint)); Assert.IsNull(fixture.Store.InitialPublicationCompletion());
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key='initial_library_publication_complete_v1'"));
        fixture.Sql("DROP TRIGGER fail_completion"); Assert.IsNotNull(fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint));
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void CorruptOrIncompleteCompletionAndMissingRetainedEvidenceNeverBecomeANewSetup()
    {
        foreach (var sql in new[] {
            "DELETE FROM processor_meta WHERE key='initial_library_publication_complete_v1:sha256'",
            "UPDATE processor_meta SET value='corrupt' WHERE key='initial_library_publication_complete_v1'",
            "DELETE FROM workspace_document_observation WHERE id='publisher-file'",
            "DELETE FROM workspace_inventory_checkpoint",
            "DELETE FROM processor_meta WHERE key IN('library_bootstrap_sources_v1','library_bootstrap_sources_v1:sha256')",
            "DELETE FROM workspace_publication WHERE id=(SELECT id FROM workspace_publication ORDER BY id LIMIT 1)",
            "DELETE FROM signing_intent WHERE sequence=1" })
        {
            using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint); fixture.Sql(sql);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.InitialPublicationCompletion());
            Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key='initial_library_publication_complete_v1'"));
        }
    }

    private sealed partial class Fixture
    {
        public (LibraryInitialPublicationPlan Plan, WorkspaceInventoryCheckpoint Checkpoint) PrepareCompletionEvidence(bool verifySources = true)
        {
            var sealedPolicies = SealInitialPolicies(); var plan = Store.PrepareInitialPublication(); var outbox = new WorkspacePublicationRepository(Database, Budget);
            var publisher = outbox.Prepare(plan.PublisherObjectId, plan.Scope, "publisher-authorization", "application/json", sealedPolicies.Publisher.Envelope!.Value.Span);
            var sources = outbox.Prepare(plan.SourceObjectId, plan.Scope, "source-configuration", "application/json", sealedPolicies.Sources.Envelope!.Value.Span);
            publisher = outbox.AssignFileId(publisher.Id, publisher.Sha256, "publisher-file"); sources = outbox.AssignFileId(sources.Id, sources.Sha256, "source-file");
            outbox.ConfirmVerified(publisher.Id, plan.Scope.AccountId, plan.Scope.FolderId, publisher.FileId!, publisher.Sha256, publisher.Payload.Length, 7);
            if (verifySources) outbox.ConfirmVerified(sources.Id, plan.Scope.AccountId, plan.Scope.FolderId, sources.FileId!, sources.Sha256, sources.Payload.Length, 7);
            var context = new WorkspaceObservationContext(plan.Scope.LibraryId, plan.Scope.AccountId, plan.Scope.FolderId, plan.Scope.AccountRevision, null);
            var observations = new WorkspaceObservationRepository(Database, Budget);
            observations.RememberRead(context, new(publisher.FileId!, 7, publisher.Payload.Length, publisher.Sha256)); observations.RememberRead(context, new(sources.FileId!, 7, sources.Payload.Length, sources.Sha256));
            return (plan, observations.Complete(context, "captured-cursor", observations.Known(context)));
        }
    }
}
