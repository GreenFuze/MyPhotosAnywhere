using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void PublicAttemptDoesNotOpenStrictFencesButAnExplicitOwnedLocalContextPreservesOriginalScopeAndAllocation()
    {
        using var fixture=new Fixture(); fixture.PrepareLocalPublication(registerSigning:true);
        var journal=new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget);
        var capture=journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()),Guid.NewGuid(),Guid.NewGuid());
        var evidence=RenewedAccessEvidence(fixture);
        _=journal.RecordRenewedAccess(capture.Scope.GenerationId,evidence.Current,evidence.Checkpoint,evidence.Signing,evidence.Publications);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>fixture.Store.AssertLocalPublicationScope(capture.Scope));
        var outbox=new WorkspacePublicationRepository(fixture.Database,fixture.Budget); var id=Guid.NewGuid();
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>outbox.Prepare(id,capture.Scope,"facts-index","application/json","{}"u8));
        using var local=journal.OpenLocalAccess(capture.Scope.GenerationId,evidence.Current.AccountRevision,evidence.Checkpoint,evidence.Signing,evidence.Publications,CancellationToken.None);
        fixture.Store.AssertLocalPublicationScope(capture.Scope,local);
        var work=outbox.Prepare(id,capture.Scope,"facts-index","application/json","{}"u8,local); outbox.AssignFileId(id,work.Sha256,"original-renewed-id");
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>outbox.ConfirmVerified(id,capture.Scope.AccountId,capture.Scope.FolderId,"original-renewed-id",work.Sha256,2,1));
        var verified=outbox.ConfirmVerified(id,capture.Scope.AccountId,capture.Scope.FolderId,"original-renewed-id",work.Sha256,2,1,local);
        Assert.AreEqual(capture.Scope,verified.Scope); Assert.AreEqual("original-renewed-id",verified.FileId); Assert.AreEqual(1L,verified.VerifiedVersion);
        Assert.AreEqual(capture,journal.Active().Single()); Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void RetiredCanceledAndLaterRevisionLocalContextsCannotAdmitAnotherWriteOrGeneration()
    {
        using var fixture=new Fixture(); fixture.PrepareLocalPublication(registerSigning:true);
        var journal=new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget);
        var capture=journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()),Guid.NewGuid(),Guid.NewGuid());
        var first=RenewedAccessEvidence(fixture);
        _=journal.RecordRenewedAccess(capture.Scope.GenerationId,first.Current,first.Checkpoint,first.Signing,first.Publications);
        using var local=journal.OpenLocalAccess(capture.Scope.GenerationId,first.Current.AccountRevision,first.Checkpoint,first.Signing,first.Publications,CancellationToken.None);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>fixture.Store.AssertLocalPublicationScope(capture.Scope with { GenerationId=Guid.NewGuid() },local));
        var next=RenewedAccessEvidence(fixture);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>fixture.Store.AssertLocalPublicationScope(capture.Scope,local));
        _=journal.RecordRenewedAccess(capture.Scope.GenerationId,next.Current,next.Checkpoint,next.Signing,next.Publications);
        using var ownerStop=new CancellationTokenSource();
        using var current=journal.OpenLocalAccess(capture.Scope.GenerationId,next.Current.AccountRevision,next.Checkpoint,next.Signing,next.Publications,ownerStop.Token);
        fixture.Store.AssertLocalPublicationScope(capture.Scope,current); ownerStop.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>fixture.Store.AssertLocalPublicationScope(capture.Scope,current));
        local.Dispose(); Assert.ThrowsExactly<ObjectDisposedException>(()=>fixture.Store.AssertLocalPublicationScope(capture.Scope,local));
        Assert.AreEqual(capture,journal.Active().Single()); Assert.AreEqual(0L,fixture.Scalar("SELECT count(*) FROM workspace_publication"));
    }

    [TestMethod]
    public void SemanticSourceChangesRevokeLocalUseWithoutRewritingTheOriginalCaptureOrAttempt()
    {
        using var fixture=new Fixture(); fixture.PrepareLocalPublication(registerSigning:true);
        var journal=new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget);
        var capture=journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()),Guid.NewGuid(),Guid.NewGuid());
        var evidence=RenewedAccessEvidence(fixture);
        var attempt=journal.RecordRenewedAccess(capture.Scope.GenerationId,evidence.Current,evidence.Checkpoint,evidence.Signing,evidence.Publications);
        using var local=journal.OpenLocalAccess(capture.Scope.GenerationId,evidence.Current.AccountRevision,evidence.Checkpoint,evidence.Signing,evidence.Publications,CancellationToken.None);
        fixture.DisablePublicationSource(); fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(4,3,enabled:false,sourceRevision:2));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>fixture.Store.AssertLocalPublicationScope(capture.Scope,local));
        Assert.AreEqual(attempt,journal.AccessAttempt(capture.Scope.GenerationId,evidence.Current.AccountRevision)); Assert.AreEqual(capture,journal.Active().Single());
    }

    [TestMethod]
    public void ALocalContextCannotCrossActualDatabaseOwnerRecreationEvenWhenInstallationAndHistorySurvive()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory(); CatalogGenerationLocalAccess old; CatalogGenerationCapture capture; Guid id;
        using(var fixture=new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(registerSigning:true); var journal=new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget);
            capture=journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()),Guid.NewGuid(),Guid.NewGuid()); var evidence=RenewedAccessEvidence(fixture);
            _=journal.RecordRenewedAccess(capture.Scope.GenerationId,evidence.Current,evidence.Checkpoint,evidence.Signing,evidence.Publications);
            old=journal.OpenLocalAccess(capture.Scope.GenerationId,evidence.Current.AccountRevision,evidence.Checkpoint,evidence.Signing,evidence.Publications,CancellationToken.None);
            id=Guid.NewGuid(); new WorkspacePublicationRepository(fixture.Database,fixture.Budget).Prepare(id,capture.Scope,"facts-index","application/json","{}"u8,old);
        }
        using(old)
        using(var restarted=new ProcessorStateDatabase(root.Path))
        {
            var budget=new StorageBudgetManager(new Space());
            Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>new WorkspacePublicationRepository(restarted,budget).AssertCurrent(id,old));
            Assert.AreEqual(capture,new CatalogGenerationCaptureRepository(restarted,budget).Active().Single());
        }
    }

    [TestMethod]
    public void ActualRenewedObservationCanCompleteOriginalSignedWorkWithoutRewritingItsScopeAndRemainsHistoricalAfterDisconnect()
    {
        using var fixture=new Fixture(); fixture.PrepareLocalPublication(registerSigning:true); var completed=LocalCompletionEvidence(fixture);
        var journal=new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget); var capture=completed.Publication.Capture;
        var evidence=RenewedAccessEvidence(fixture);
        _=journal.RecordRenewedAccess(capture.Scope.GenerationId,evidence.Current,evidence.Checkpoint,evidence.Signing,evidence.Publications);
        using var local=journal.OpenLocalAccess(capture.Scope.GenerationId,evidence.Current.AccountRevision,evidence.Checkpoint,evidence.Signing,evidence.Publications,CancellationToken.None);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>journal.ConfirmCurrentCommitVerified(capture.Scope.GenerationId,evidence.Checkpoint,completed.Commit));
        var receipt=journal.ConfirmCurrentCommitVerified(capture.Scope.GenerationId,evidence.Checkpoint,completed.Commit,local);
        Assert.AreEqual(capture.Scope,receipt.Scope); Assert.AreEqual(evidence.Current.AccountRevision,receipt.Observation.Context.AccountRevision);
        var head=journal.CompleteCurrentGeneration(capture.Scope.GenerationId,evidence.Checkpoint,completed.Commit,local);
        new DriveAccountRepository(fixture.Database).Disconnect(capture.Scope.AccountId,evidence.Current.AccountRevision);
        Assert.AreEqual(receipt,journal.Verification(capture.Scope.GenerationId)); Assert.AreEqual(head,journal.PublishedHead());
        var digest=(string)fixture.Scalar("SELECT document_hash FROM catalog_generation_access_attempt");
        fixture.Sql("UPDATE catalog_generation_access_attempt SET document_hash='"+new string('0',64)+"'");
        Assert.ThrowsExactly<InvalidDataException>(()=>journal.PublishedHead());
        fixture.Sql("UPDATE catalog_generation_access_attempt SET document_hash='"+digest+"'");
        Assert.AreEqual(head,journal.PublishedHead()); Assert.AreEqual("snapshot-retiring",journal.BeginSnapshotRetirement(capture.Scope.GenerationId).Phase);
    }

    [TestMethod]
    public void AHistoricalAttemptCannotOpenLocalUseAcrossANewerIncompleteScanOrChangedInventory()
    {
        using var fixture=new Fixture(); fixture.PrepareLocalPublication(registerSigning:true);
        var journal=new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget);
        var capture=journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()),Guid.NewGuid(),Guid.NewGuid());
        var evidence=RenewedAccessEvidence(fixture);
        var retained=journal.RecordRenewedAccess(capture.Scope.GenerationId,evidence.Current,evidence.Checkpoint,evidence.Signing,evidence.Publications);
        var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget); var scan=observations.BeginScan(evidence.Checkpoint.Context);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>journal.OpenLocalAccess(capture.Scope.GenerationId,evidence.Current.AccountRevision,
            evidence.Checkpoint,evidence.Signing,evidence.Publications,CancellationToken.None));
        var complete=observations.CompleteScan(scan,"renewed-later-complete",observations.Known(evidence.Checkpoint.Context));
        Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(()=>journal.OpenLocalAccess(capture.Scope.GenerationId,evidence.Current.AccountRevision,
            complete,evidence.Signing with { Sha256=new string('0',64) },evidence.Publications,CancellationToken.None));
        Assert.AreEqual(retained,journal.AccessAttempt(capture.Scope.GenerationId,evidence.Current.AccountRevision));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>fixture.Store.AssertLocalPublicationScope(capture.Scope));
    }

    [TestMethod]
    public void ExistingVerificationBytesStayFixedWhenOnlyTheLaterCompletionUsesRenewedAccess()
    {
        using var fixture=new Fixture(); fixture.PrepareLocalPublication(registerSigning:true); var completed=LocalCompletionEvidence(fixture);
        var journal=new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget); var capture=completed.Publication.Capture;
        var original=journal.ConfirmCurrentCommitVerified(capture.Scope.GenerationId,completed.Checkpoint,completed.Commit);
        var document=(string)fixture.Scalar("SELECT document FROM catalog_generation_verification"); var evidence=RenewedAccessEvidence(fixture);
        _=journal.RecordRenewedAccess(capture.Scope.GenerationId,evidence.Current,evidence.Checkpoint,evidence.Signing,evidence.Publications);
        using var local=journal.OpenLocalAccess(capture.Scope.GenerationId,evidence.Current.AccountRevision,evidence.Checkpoint,evidence.Signing,evidence.Publications,CancellationToken.None);
        _=journal.CompleteCurrentGeneration(capture.Scope.GenerationId,evidence.Checkpoint,completed.Commit,local);
        Assert.AreEqual(original,journal.Verification(capture.Scope.GenerationId)); Assert.AreEqual(document,fixture.Scalar("SELECT document FROM catalog_generation_verification"));
    }
}
