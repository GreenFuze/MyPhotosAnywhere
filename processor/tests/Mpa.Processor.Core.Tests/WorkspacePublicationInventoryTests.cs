using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void PublicationInventoryRetainsVerifiedAllocatedAndUnallocatedWorkWithoutTruncation()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); var repository = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        var pending = repository.Prepare(Guid.NewGuid(), work.Plan.Scope, "facts-index", "application/json", "{}"u8);
        var allocated = repository.Prepare(Guid.NewGuid(), work.Plan.Scope, "facts-index", "application/json", "{}"u8); repository.AssignFileId(allocated.Id, allocated.Sha256, "uncertain-file");
        var snapshot = repository.Inventory(work.Checkpoint.Context, 4); Assert.AreEqual(4, snapshot.Objects.Count);
        Assert.AreEqual(2, snapshot.Objects.Count(item => item.VerifiedVersion == 7));
        Assert.IsNull(snapshot.Objects.Single(item => item.Id == pending.Id).FileId);
        var uncertain = snapshot.Objects.Single(item => item.Id == allocated.Id); Assert.AreEqual("uncertain-file", uncertain.FileId); Assert.IsNull(uncertain.VerifiedVersion);
        Assert.IsTrue(snapshot.Objects.All(item => item.Scope == work.Plan.Scope)); repository.AssertInventoryUnchanged(snapshot);
        Assert.ThrowsExactly<InvalidDataException>(() => repository.Inventory(work.Checkpoint.Context, 3)); Assert.AreEqual(4, repository.Identities(4).Count);
    }

    [TestMethod]
    public void AllocationVerificationAndNewWorkInvalidateTheExactPriorInventory()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); var repository = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        var first = repository.Inventory(work.Checkpoint.Context);
        var pending = repository.Prepare(Guid.NewGuid(), work.Plan.Scope, "facts-index", "application/json", "{}"u8);
        Assert.ThrowsExactly<WorkspacePublicationInventoryChangedException>(() => repository.AssertInventoryUnchanged(first)); var unallocated = repository.Inventory(work.Checkpoint.Context);
        repository.AssignFileId(pending.Id, pending.Sha256, "fixed-file");
        Assert.ThrowsExactly<WorkspacePublicationInventoryChangedException>(() => repository.AssertInventoryUnchanged(unallocated)); var allocated = repository.Inventory(work.Checkpoint.Context);
        repository.ConfirmVerified(pending.Id, work.Plan.Scope.AccountId, work.Plan.Scope.FolderId, "fixed-file", pending.Sha256, 2, 9);
        Assert.ThrowsExactly<WorkspacePublicationInventoryChangedException>(() => repository.AssertInventoryUnchanged(allocated));
        repository.AssertInventoryUnchanged(repository.Inventory(work.Checkpoint.Context)); Assert.AreEqual("fixed-file", repository.Read(pending.Id)!.FileId);
    }

    [TestMethod]
    public void NewAccessCanInspectOriginalScopesButCannotRetargetThemOrAdmitTheOldScan()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); var repository = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        var original = repository.Inventory(work.Checkpoint.Context); var accounts = new DriveAccountRepository(fixture.Database); accounts.Disconnect("account-A", 1);
        var pending = accounts.Begin(Guid.NewGuid(), "account-A"); var connected = accounts.Promote(pending.Id, "account-A", "Again", null);
        var current = repository.Inventory(work.Checkpoint.Context with { AccountRevision = connected.Revision });
        Assert.AreNotEqual(original.Sha256, current.Sha256); Assert.IsTrue(current.Objects.All(item => item.Scope.AccountRevision == 1));
        Assert.ThrowsExactly<StaleDriveConnectionException>(() => repository.AssertInventoryUnchanged(original));
        Assert.AreEqual(work.Plan.Scope, repository.Read(work.Plan.PublisherObjectId)!.Scope); // Inspection grants no renewed upload authority.
    }

    [TestMethod]
    public void ForeignWorkAndAlteredDescriptionsCannotBeSilentlyDroppedFromPublicationEvidence()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); var repository = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        var snapshot = repository.Inventory(work.Checkpoint.Context);
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => repository.AssertInventoryUnchanged(snapshot with { Objects = new(snapshot.Objects.Skip(1).ToList()) }));
        repository.Prepare(Guid.NewGuid(), work.Plan.Scope with { FolderId = "another-workspace" }, "facts-index", "application/json", "{}"u8);
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => repository.Inventory(work.Checkpoint.Context)); Assert.AreEqual(3, repository.Identities(3).Count);
    }

    [TestMethod]
    public void ScanCompletionAndItsOutboxFenceRollbackTogetherIfATriggerChangesAReceipt()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); var outbox = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); var inventory = outbox.Inventory(work.Checkpoint.Context);
        var attempt = observations.BeginScan(work.Checkpoint.Context);
        fixture.Sql("CREATE TRIGGER change_receipt AFTER UPDATE ON processor_meta WHEN NEW.key='workspace_observation_attempt_v1:sha256' BEGIN UPDATE workspace_publication SET verified_version=8 WHERE id='" + work.Plan.PublisherObjectId.ToString("D") + "'; END");
        Assert.ThrowsExactly<WorkspacePublicationInventoryChangedException>(() => observations.CompleteScan(attempt, "new-cursor", observations.Known(attempt.Context), inventory));
        Assert.AreEqual(7L, outbox.Read(work.Plan.PublisherObjectId)!.VerifiedVersion); Assert.AreEqual(work.Checkpoint, observations.LastCheckpoint()); Assert.AreEqual(attempt, observations.LastAttempt());
        fixture.Sql("DROP TRIGGER change_receipt"); var completed = observations.CompleteScan(attempt, "new-cursor", observations.Known(attempt.Context), inventory);
        observations.AssertCompletedScan(completed); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void InventoryCannotMakeAnOversizedJsonUploadIntoSupportedMetadata()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence(); var repository = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        var pending = repository.Prepare(Guid.NewGuid(), work.Plan.Scope, "facts-index", "application/json", new byte[262145]);
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => repository.Inventory(work.Checkpoint.Context));
        Assert.AreEqual(262145, repository.Read(pending.Id)!.Payload.Length); Assert.IsNull(repository.Read(pending.Id)!.FileId);
    }
}
