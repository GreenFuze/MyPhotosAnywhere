using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task EngineOwnedSetupContinuesIntoActualCatalogReconciliationWithoutABrowserOwner()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); ConfigureCatalogMetadata(fixture);
        var catalog = CatalogReconciler(fixture);
        await using var runtime = new InitialLibraryPublicationRuntime(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service,
            fixture.Setup.Keys, fixture.Budget, catalog);
        runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, runtime.Activity.Stage);
        Assert.IsNotNull(runtime.CurrentCatalog); Assert.AreEqual(2, runtime.CurrentCatalog.Observation.Documents);
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations); Assert.IsNull(runtime.Failure);
        await runtime.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(catalog.ExitConfirmed); Assert.IsNull(runtime.CurrentCatalog);
    }

    [TestMethod]
    public async Task EngineCatalogHistoryAutomaticallyContinuesTheNextDurableBoundedBatch()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureCatalogMetadata(fixture);
        var freshStarts = 0; var firstReplay = 0;
        fixture.Setup.Accounts.Readers.DocumentStart = (drive, _) =>
        { freshStarts++; return Task.FromResult(new DriveChangeCursor("permission-A", drive, "fresh")); };
        fixture.Setup.Accounts.Readers.DocumentChanges = (cursor, _) =>
        {
            if (cursor.Token == "current") firstReplay++;
            var number = cursor.Token == "current" ? 0 : cursor.Token.StartsWith("history-", StringComparison.Ordinal) &&
                int.TryParse(cursor.Token.AsSpan(8), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
            var next = number is >= 0 and < 128 ? cursor with { Token = "history-" + (number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) } : null;
            return Task.FromResult(new DriveChangePage([], next, next is null ? cursor with { Token = cursor.Token == "fresh" ? "fresh" : "history-end" } : null));
        };
        var catalog = CatalogReconciler(fixture);
        await using var runtime = new InitialLibraryPublicationRuntime(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service,
            fixture.Setup.Keys, fixture.Budget, catalog);
        runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, runtime.Activity.Stage); Assert.IsNotNull(runtime.CurrentCatalog);
        Assert.AreEqual(2, freshStarts); Assert.AreEqual(1, firstReplay); Assert.IsNull(runtime.LastAttemptFailure);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var attempt = new WorkspaceObservationRepository(database, fixture.Budget).LastAttempt()!;
            Assert.IsNotNull(attempt.Completed); Assert.AreEqual(129L, attempt.HistoryReplay!.Pages);
            Assert.AreEqual("current", attempt.FirstUnresolvedCursor); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
    }

    [TestMethod]
    public async Task EngineCatalogRetryNeverUsesHistoricalSetupCompletionAsACurrentHeadGrant()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        var completion = await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var head = await AddCatalogHeadAsync(fixture, "retry-head").ConfigureAwait(false); ConfigureCatalogMetadata(fixture);
        var initialScans = fixture.InitialInventories;
        fixture.Setup.Accounts.Readers.MetadataRead = (_, _, _) => throw new HttpRequestException("Synthetic metadata outage");
        await using var runtime = new InitialLibraryPublicationRuntime(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service,
            fixture.Setup.Keys, fixture.Budget, CatalogReconciler(fixture));
        runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention, runtime.Activity.Stage); Assert.IsNull(runtime.CurrentCatalog);
        Assert.IsNull(runtime.Failure); Assert.IsInstanceOfType<HttpRequestException>(runtime.LastAttemptFailure);
        ConfigureCatalogMetadata(fixture); runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, runtime.Activity.Stage); Assert.AreEqual(head.Reference, runtime.CurrentCatalog!.Parent);
        Assert.AreEqual(initialScans, fixture.InitialInventories); Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
        Assert.AreEqual(completion.Completion, await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).InitialPublicationCompletion(), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentCatalogReconcilesInitialPoliciesWithoutWritesAndStartsClosedAfterRestart()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        var initial = await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureCatalogMetadata(fixture);
        await using (var owner = CatalogReconciler(fixture))
        {
            Assert.IsNull(owner.Current); var read = await owner.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, read.Observation.Documents); Assert.IsNull(read.Parent); Assert.AreEqual(0L, read.Sequence);
            Assert.HasCount(0, read.PendingSigning); Assert.HasCount(0, read.PendingPublications); Assert.AreSame(read, owner.Current);
            Assert.AreEqual(initial.Completion, await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).InitialPublicationCompletion(), CancellationToken.None).ConfigureAwait(false));
            await owner.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(owner.ExitConfirmed); Assert.IsNull(owner.Current);
        }
        await using var restarted = CatalogReconciler(fixture); Assert.IsNull(restarted.Current);
        var current = await restarted.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(current.Observation); Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task ActualCurrentHeadRequiresCompleteSignedAncestryAndCoveredDependenciesRegardlessOfListingOrder()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var first = await AddCatalogHeadAsync(fixture, "head-first").ConfigureAwait(false);
        var second = await AddCatalogHeadAsync(fixture, "head-second", first.Reference).ConfigureAwait(false);
        var reads = ConfigureCatalogMetadata(fixture); await using var owner = CatalogReconciler(fixture);
        var result = await owner.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(second.Reference, result.Parent); Assert.AreEqual(2L, result.Sequence); Assert.AreEqual(0L, result.Assets);
        Assert.HasCount(0, result.PendingSigning); Assert.HasCount(0, result.PendingPublications);
        Assert.AreEqual(2, reads.Count(id => id == second.Reference.FileId)); Assert.AreEqual(2, reads.Count(id => id == first.Reference.FileId));
        Assert.AreEqual(2, reads.Count(id => id == second.Plan.Dependencies.RootIndex.FileId));
        Assert.AreEqual(2, reads.Count(id => id == second.Plan.Dependencies.SourceCoverage.FileId));
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
    }

    [TestMethod]
    public async Task MissingSignedHistoryIsNotInferredAwayByASuccessfulEmptyCatalogListing()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var lost = await AddCatalogHeadAsync(fixture, "lost-head").ConfigureAwait(false); fixture.Cloud.Remove(lost.Reference.FileId);
        ConfigureCatalogMetadata(fixture); await using var owner = CatalogReconciler(fixture);
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(() => owner.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(owner.Current);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var retained = new ProtocolSigningJournal(database, fixture.Budget).Read(lost.Intent.Id)!;
            Assert.AreEqual(lost.Intent.EnvelopeSha256, retained.EnvelopeSha256); Assert.IsNull(new WorkspaceObservationRepository(database, fixture.Budget).LastAttempt()!.Completed); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, fixture.Allocations); Assert.AreEqual(2, fixture.Creates);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingSigningAndSealedOutboxWorkRemainExplicitWithTheirOriginalIdentities(bool sealedOutbox)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var pending = await AddCatalogHeadAsync(fixture, "pending-head", unsealed: !sealedOutbox).ConfigureAwait(false); fixture.Cloud.Remove(pending.Reference.FileId);
        var publicationId = Guid.NewGuid();
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            if (sealedOutbox) new WorkspacePublicationRepository(database, fixture.Budget).Prepare(publicationId, pending.Plan.Scope,
                "catalog-commit", "application/json", pending.Intent.Envelope!.Value.Span);
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        ConfigureCatalogMetadata(fixture); await using var owner = CatalogReconciler(fixture);
        var result = await owner.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(result.Parent); Assert.AreEqual(0L, result.Sequence);
        if (sealedOutbox) { Assert.AreEqual(publicationId, result.PendingPublications.Single()); Assert.HasCount(0, result.PendingSigning); }
        else { Assert.AreEqual(pending.Intent.Id, result.PendingSigning.Single()); Assert.HasCount(0, result.PendingPublications); }
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new ProtocolSigningJournal(database, fixture.Budget); Assert.AreEqual(pending.Intent.BodySha256, journal.Read(pending.Intent.Id)!.BodySha256);
            if (sealedOutbox) { var row = new WorkspacePublicationRepository(database, fixture.Budget).Read(publicationId)!; Assert.AreEqual(pending.Plan.Scope, row.Scope); Assert.IsNull(row.FileId); }
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, fixture.Allocations); Assert.AreEqual(2, fixture.Creates);
    }

    [TestMethod]
    public async Task ChangesArrivingDuringCoveredGraphReadsLeaveTheEntireCurrentAttemptClosed()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        await AddCatalogHeadAsync(fixture, "stable-head").ConfigureAwait(false); ConfigureCatalogMetadata(fixture); var checks = 0;
        fixture.Setup.Accounts.Readers.DocumentChanges = (cursor, _) => Task.FromResult(new DriveChangePage(
            new(++checks == 3 ? [new("file", "late-policy", false, new("application/json", new(["metadata"]), false, 1, 2, true))] : []),
            null, cursor with { Token = "end" }));
        await using var owner = CatalogReconciler(fixture);
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => owner.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(owner.Current);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var repository = new WorkspaceObservationRepository(database, fixture.Budget); var attempt = repository.LastAttempt()!;
            Assert.IsNull(attempt.Completed); Assert.Contains("late-policy", repository.KnownIdentities(attempt.Context)); return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReconciliationExitJoinsAnActualUncooperativeCoveredReadBeforeRetiringBorrowedOwners()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var head = await AddCatalogHeadAsync(fixture, "held-head").ConfigureAwait(false); ConfigureCatalogMetadata(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = fixture.Setup.Accounts.Readers.MetadataRead!; var heads = 0;
        fixture.Setup.Accounts.Readers.MetadataRead = async (folder, candidate, token) =>
        {
            if (candidate.FileId == head.Reference.FileId && ++heads == 2) { entered.TrySetResult(); await release.Task.ConfigureAwait(false); }
            return await read(folder, candidate, CancellationToken.None).ConfigureAwait(false);
        };
        await using var owner = CatalogReconciler(fixture); var reading = owner.ReconcileAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); var stopping = owner.DisposeAsync().AsTask();
        try { Assert.IsFalse(stopping.IsCompleted); Assert.IsNotNull(fixture.Setup.Keys.PublicIdentity); Assert.IsNull(owner.Current); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false); await stopping.ConfigureAwait(false);
        Assert.IsTrue(owner.ExitConfirmed); Assert.IsNull(owner.Current);
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
    }

    private static LocalCatalogReconciliationService CatalogReconciler(InitialPublicationFixture fixture) =>
        new(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service, fixture.Setup.Keys, fixture.Budget);

    [TestMethod]
    public async Task InvalidActualAncestryFailsBeforeDecodingOrRereadingTheCoveredFactsGraph()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var first = await AddCatalogHeadAsync(fixture, "parent-head").ConfigureAwait(false);
        var head = await AddCatalogHeadAsync(fixture, "child-head", first.Reference with { Sha256 = new string('a', 64) }).ConfigureAwait(false);
        var reads = ConfigureCatalogMetadata(fixture); await using var owner = CatalogReconciler(fixture);
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => owner.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(owner.Current);
        Assert.AreEqual(1, reads.Count(file => file == head.Plan.Dependencies.RootIndex.FileId));
        Assert.AreEqual(1, reads.Count(file => file == head.Plan.Dependencies.SourceCoverage.FileId));
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
    }

    [TestMethod]
    public async Task ActualCoveredGzipFactsUseTheirObservedEncodingWithoutReadingOrBackingUpOriginals()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var head = await AddCatalogHeadAsync(fixture, "photo-head", includeAsset: true).ConfigureAwait(false);
        var reads = ConfigureCatalogMetadata(fixture); await using var owner = CatalogReconciler(fixture);
        var result = await owner.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, result.Assets); Assert.AreEqual(head.Reference, result.Parent);
        Assert.AreEqual(2, reads.Count(file => file == "photo-head-facts")); Assert.DoesNotContain("read-only-original", reads);
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var repository = new WorkspaceObservationRepository(database, fixture.Budget);
            Assert.AreEqual("application/gzip", repository.Known(result.Observation.Context).Single(item => item.FileId == "photo-head-facts").MimeType); return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ActualCloudAheadSignatureIsRetainedBeforeRecoveryAndNeverAdvancesTheLocalJournal()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var prior = await AddCatalogHeadAsync(fixture, "retained-head").ConfigureAwait(false);
        var identity = fixture.Setup.Keys.PublicIdentity;
        var ahead = prior.Plan with { Scope = prior.Plan.Scope with { GenerationId = Guid.NewGuid() }, Parent = prior.Reference };
        var body = new CatalogCommitDocumentWriter(ahead).Encode(identity, 2);
        var envelope = new SignedProtocolDocuments([DriveCatalogFacts.Capability, CatalogPublicationReader.Capability])
            .Encode(body, fixture.Setup.Keys.SignCatalogDocument(body));
        fixture.Cloud.Add("ahead-head", new("permission-A", "ahead-head", "metadata", 7, envelope));
        ConfigureCatalogMetadata(fixture); await using var owner = CatalogReconciler(fixture);
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(() => owner.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(owner.Current);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new ProtocolSigningJournal(database, fixture.Budget);
            Assert.AreEqual(1L, journal.Inventory(identity.LibraryId).Actors.Single(actor => actor.ActorId == identity.PublisherId).Sequence);
            var observations = new WorkspaceObservationRepository(database, fixture.Budget); var attempt = observations.LastAttempt()!;
            Assert.IsNull(attempt.Completed); Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(envelope)), observations.Known(attempt.Context).Single(item => item.FileId == "ahead-head").Sha256); return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ActualRevocationRemainsSelectedWhenItClosesCatalogReconciliation()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var revoked = await new JournaledProtocolSigner(fixture.Setup.Accounts.State, fixture.Setup.Keys, fixture.Budget).SignControllerAsync(Guid.NewGuid(),
            "library-publisher-authorization", (identity, sequence) => new CanonicalJson().Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "mpa", major = 1, minor = 0, libraryId = identity.LibraryId.ToString("D"), actorId = identity.ControllerId.ToString("D"),
                epoch = identity.ControllerEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), sequence = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                kind = "library-publisher-authorization", requiredCapabilities = new[] { CatalogPublisherAuthorization.Capability },
                payload = new { workspace = new { accountId = "permission-A", folderId = "metadata" }, publisher = (object?)null }
            })), CancellationToken.None).ConfigureAwait(false);
        fixture.Cloud.Add("z-revocation", new("permission-A", "z-revocation", "metadata", 7, revoked.Envelope!.Value));
        var reads = ConfigureCatalogMetadata(fixture); await using var owner = CatalogReconciler(fixture);
        await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(() => owner.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(owner.Current); var completedReads = reads.Count;
        await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(() => owner.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(completedReads, reads.Count);
        var selected = await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).CurrentPublisherPolicy(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(selected!.Publisher); Assert.AreEqual(revoked.EnvelopeSha256, selected.EnvelopeSha256);
    }

    [TestMethod]
    public async Task ChangedActualPredecessorReadCannotReuseThePreviousCompletedCatalog()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var head = await AddCatalogHeadAsync(fixture, "changed-head").ConfigureAwait(false);
        ConfigureCatalogMetadata(fixture); await using var owner = CatalogReconciler(fixture);
        Assert.IsNotNull(await owner.ReconcileAsync(CancellationToken.None).ConfigureAwait(false));
        ConfigureCatalogMetadata(fixture); var read = fixture.Setup.Accounts.Readers.MetadataRead!; var headReads = 0;
        fixture.Setup.Accounts.Readers.MetadataRead = async (folder, candidate, token) =>
        {
            var result = await read(folder, candidate, token).ConfigureAwait(false);
            if (candidate.FileId != head.Reference.FileId || ++headReads != 2) return result;
            var changed = result.Bytes.ToArray(); changed[^2] ^= 1; return result with { Bytes = changed };
        };
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => owner.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(owner.Current);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var repository = new WorkspaceObservationRepository(database, fixture.Budget); var attempt = repository.LastAttempt()!;
            Assert.IsNull(attempt.Completed); Assert.AreEqual(head.Reference.Sha256, repository.Known(attempt.Context).Single(item => item.FileId == head.Reference.FileId).Sha256); return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static List<string> ConfigureCatalogMetadata(InitialPublicationFixture fixture)
    {
        var reads = new List<string>();
        fixture.Setup.Accounts.Readers.MetadataInventory = (_, _, _, token) =>
        {
            token.ThrowIfCancellationRequested(); return Task.FromResult(new DriveWorkspaceMetadataPage(new(fixture.Cloud.Values
                .OrderByDescending(item => item.FileId, StringComparer.Ordinal).Select(item => new DriveWorkspaceMetadataCandidate(item.FileId,
                    item.DriveVersion, item.Bytes.Length, fixture.MetadataMimeTypes.GetValueOrDefault(item.FileId, "application/json"))).ToList()), null, false));
        };
        fixture.Setup.Accounts.Readers.MetadataRead = (_, candidate, token) =>
        { token.ThrowIfCancellationRequested(); reads.Add(candidate.FileId); return Task.FromResult(fixture.Cloud[candidate.FileId]); };
        return reads;
    }
    private static async Task<(CatalogObjectReference Reference, CatalogCommitPlan Plan, ProtocolSigningIntent Intent)> AddCatalogHeadAsync(
        InitialPublicationFixture fixture, string file, CatalogObjectReference? parent = null, bool unsealed = false, bool includeAsset = false)
    {
        var captured = await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var authority = new LibraryAuthorityRepository(database, fixture.Budget);
            return (Authority: authority.CaptureLocalPublicationAuthority(), Sources: authority.CurrentSourceConfiguration()!);
        }, CancellationToken.None).ConfigureAwait(false);
        var authority = captured.Authority; var generation = Guid.NewGuid(); var workspace = new CatalogWorkspace(authority.AccountId, authority.FolderId);
        CatalogObjectReference Store(string name, byte[] bytes, string mimeType = "application/json")
        {
            fixture.Cloud.Add(name, new(authority.AccountId, name, authority.FolderId, 7, bytes));
            fixture.MetadataMimeTypes.Add(name, mimeType);
            return new(authority.AccountId, name, authority.FolderId, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        var children = new List<CatalogIndexChild>();
        if (includeAsset)
        {
            var source = captured.Sources.Configuration.Sources.Single(); var assetId = new DriveAssetIdentity(authority.LibraryId).Derive(source.AccountId, "read-only-original");
            var prefix = assetId.ToString("N")[..2];
            var asset = new DriveCatalogAsset(assetId, source.AccountId, "read-only-original", 1, "Original.jpg", "image/jpeg", 12, 8, null, null, null,
                new([new(source.Id, source.Revision, 0)]));
            var bytes = new DriveCatalogFacts(authority.LibraryId).Encode(prefix, [asset]);
            using var compressed = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
            var encoded = Store(file + "-facts", compressed.ToArray(), "application/gzip");
            children.Add(new(prefix, encoded, new(encoded, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))));
        }
        var index = Store(file + "-index", new CatalogIndexDocumentWriter(workspace).Encode("", children));
        var sources = captured.Sources.Configuration.Sources.Select(source => new CatalogSourceCoverage(source.Id, source.AccountId, source.FolderId,
            source.Revision, 1, source.IncludeDescendants, source.Enabled, true, "pending", null)).ToArray();
        var coveragePages = new List<CatalogCoveragePageReference>();
        if (sources.Length > 0)
        {
            var bytes = new CatalogCoverageDocumentWriter(workspace).EncodePage(authority.LibraryId, authority.ConfigurationRevision, 1, sources);
            coveragePages.Add(new(sources[0].Id, sources[^1].Id, sources.Length, Store(file + "-coverage-page", bytes)));
        }
        var coverage = Store(file + "-coverage", new CatalogCoverageDocumentWriter(workspace).EncodeRoot(authority.LibraryId,
            authority.ConfigurationRevision, 1, sources.Length, coveragePages));
        var scope = new WorkspacePublicationScope(authority.LibraryId, authority.ActorId, authority.ActorEpoch, generation, authority.AccountId,
            authority.AccountRevision, authority.FolderId, authority.ConfigurationRevision, authority.AuthoritySha256);
        var dependencies = new PreparedCatalogDependencies(authority.LibraryId, authority.ConfigurationRevision, 1, includeAsset ? 1 : 0, captured.Sources.EnvelopeSha256, index, coverage);
        var plan = new CatalogCommitPlan(scope, dependencies, captured.Sources, parent, 0, 1);
        var identity = fixture.Setup.Keys.PublicIdentity; var writer = new CatalogCommitDocumentWriter(plan); var id = Guid.NewGuid();
        var intent = unsealed ? await fixture.Setup.Accounts.State.ExecuteAsync(database => new ProtocolSigningJournal(database, fixture.Budget)
            .Reserve(id, identity.PublisherId, identity.PublisherEpoch, "catalog-commit", sequence => writer.Encode(identity, sequence)), CancellationToken.None).ConfigureAwait(false) :
            await new JournaledProtocolSigner(fixture.Setup.Accounts.State, fixture.Setup.Keys, fixture.Budget)
                .SignCatalogAsync(id, writer.Encode, CancellationToken.None).ConfigureAwait(false);
        var reference = intent.Envelope is { } envelope ? Store(file, envelope.ToArray()) :
            new CatalogObjectReference(authority.AccountId, file, authority.FolderId, intent.Body.Length, intent.BodySha256);
        return (reference, plan, intent);
    }
}
