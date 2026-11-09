using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class JournaledProtocolSignerTests
{
    [TestMethod]
    public async Task CapturedSigningUsesDurablePlanAndReplaysItsExactNativeSignature()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var prepared = await PrepareCapturedSigningAsync(fixture).ConfigureAwait(false);
        var signed = await fixture.Signer.SignCapturedCatalogAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(prepared.Capture.CommitIntentId, signed.Id); Assert.AreEqual(1L, signed.Sequence);
        CollectionAssert.AreEqual(prepared.Plan!.EncodeBody(prepared.Capture.Scope, signed.Sequence), signed.Body.ToArray());
        var retained = await fixture.State.ExecuteAsync(db => new CatalogGenerationCaptureRepository(db, fixture.Budget).Publication(prepared.Capture.Scope.GenerationId), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("commit-sealed", retained!.Capture.Phase); Assert.AreEqual(prepared.Plan, retained.Plan);
        var replay = await new JournaledProtocolSigner(fixture.State, fixture.Keys, fixture.Budget)
            .SignCapturedCatalogAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(signed.Envelope!.Value.ToArray(), replay.Envelope!.Value.ToArray());
        var identity = fixture.Keys.PublicIdentity;
        var document = new SignedProtocolDocuments(WrittenCommitCapabilities).VerifyAndRead(replay.Envelope.Value.Span,
            new(identity.LibraryId, identity.PublisherId, identity.PublisherEpoch, Convert.FromBase64String(identity.PublisherPublicKeySpki), ["catalog-commit"]));
        var commit = new CatalogPublicationReader(new("account", "metadata")).ReadCommit(document);
        Assert.AreEqual(prepared.Plan.RootIndex, commit.RootIndex); Assert.AreEqual(prepared.Plan.SourceCoverage, commit.SourceCoverage);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(() => fixture.State.ExecuteAsync(db => new CatalogGenerationCaptureRepository(db, fixture.Budget)
            .SealDependencies(prepared.Capture.Scope.GenerationId, prepared.Plan with { Basis = prepared.Basis with { SettingsRevision = 99 } }), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(signed.BodySha256, (await fixture.ReadAsync(signed.Id).ConfigureAwait(false))!.BodySha256);
    }

    [TestMethod]
    public async Task CapturedSigningRequiresCurrentPermissionWithoutRewritingOriginalScopeOrBurningCounter()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var prepared = await PrepareCapturedSigningAsync(fixture).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(db => { new DriveAccountRepository(db).Disconnect("account", prepared.Capture.Scope.AccountRevision); return true; }, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => fixture.Signer.SignCapturedCatalogAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.ReadAsync(prepared.Capture.CommitIntentId).ConfigureAwait(false));
        var captured = await fixture.State.ExecuteAsync(db => new CatalogGenerationCaptureRepository(db, fixture.Budget).Publication(prepared.Capture.Scope.GenerationId), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(prepared, captured);
    }

    [TestMethod]
    public async Task CapturedParentSequenceConflictFailsBeforeReservingOrSigningAnotherBody()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var prepared = await PrepareCapturedSigningAsync(fixture, parentSequence: 1).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(() => fixture.Signer.SignCapturedCatalogAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.ReadAsync(prepared.Capture.CommitIntentId).ConfigureAwait(false));
        var inventory = await fixture.State.ExecuteAsync(db => new ProtocolSigningJournal(db, fixture.Budget).Inventory(fixture.Library), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, inventory.Actors.Single(actor => actor.ActorId == fixture.Keys.PublicIdentity.PublisherId).Sequence);
    }

    private static async Task<CapturedCatalogPublication> PrepareCapturedSigningAsync(Fixture fixture, long parentSequence = 0)
    {
        await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var initial = await fixture.Signer.PrepareInitialPolicyPlanAsync(CancellationToken.None).ConfigureAwait(false);
        var publisher = await fixture.Signer.SignInitialPublisherAuthorizationAsync(initial.PublisherIntentId, CancellationToken.None).ConfigureAwait(false);
        var sources = await fixture.Signer.SignInitialSourceConfigurationAsync(initial.SourceIntentId, CancellationToken.None).ConfigureAwait(false);
        return await fixture.State.ExecuteAsync(db =>
        {
            var authority = new LibraryAuthorityRepository(db, fixture.Budget); authority.SelectPublisherPolicy(publisher.Envelope!.Value.Span);
            var approved = authority.SelectSourceConfiguration(sources.Envelope!.Value.Span); var current = authority.CaptureLocalPublicationAuthority();
            var scope = new WorkspacePublicationScope(current.LibraryId, current.ActorId, current.ActorEpoch, Guid.NewGuid(), current.AccountId,
                current.AccountRevision, current.FolderId, current.ConfigurationRevision, current.AuthoritySha256);
            var journal = new CatalogGenerationCaptureRepository(db, fixture.Budget); var capture = journal.Begin(scope, Guid.NewGuid(), Guid.NewGuid());
            // Local journal/signing integration fixture. It does not claim a real remote
            // graph/current-history proof, private upload admission or catalog activation.
            capture = journal.SealSnapshot(scope.GenerationId, new(capture.SnapshotId, scope.LibraryId, scope.ConfigurationRevision, 1, 0, 8192, new string('a', 64)));
            var basis = new CapturedCatalogBasis(capture.Snapshot!, parentSequence == 0 ? null : new("account", "parent", "metadata", 32, new string('b', 64)),
                parentSequence, approved.EnvelopeSha256, 0, 1);
            journal.CapturePublicationBasis(scope.GenerationId, basis);
            var outbox = new WorkspacePublicationRepository(db, fixture.Budget);
            CatalogObjectReference Dependency(string role, string file)
            {
                var work = outbox.Prepare(Guid.NewGuid(), scope, role, "application/json", "{}"u8); outbox.AssignFileId(work.Id, work.Sha256, file);
                outbox.ConfirmVerified(work.Id, scope.AccountId, scope.FolderId, file, work.Sha256, work.Payload.Length, 1);
                return new(scope.AccountId, file, scope.FolderId, work.Payload.Length, work.Sha256);
            }
            return journal.SealDependencies(scope.GenerationId, new(basis, Dependency("facts-index", "index"), Dependency("coverage-root", "coverage")));
        }, CancellationToken.None).ConfigureAwait(false);
    }
}
