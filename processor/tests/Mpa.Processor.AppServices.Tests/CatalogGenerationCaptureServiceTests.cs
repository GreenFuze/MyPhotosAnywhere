using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class CatalogGenerationCaptureServiceTests
{
    [TestMethod]
    public async Task RecreatedOwnerResumesExactFrozenFactsAndIdentitiesAfterLiveLibraryChanges()
    {
        await using var fixture = new Fixture(); CatalogGenerationCapture? first = null;
        await using (var owner = fixture.Owner())
        {
            Assert.AreEqual(1L, await fixture.Run(owner, (claim, snapshot, _) => { first = claim; return Task.FromResult(snapshot.Count); }).ConfigureAwait(false));
        }
        Assert.IsNotNull(first); fixture.Add("later");
        await using var resumed = fixture.Owner();
        Assert.AreEqual("original", await fixture.Run(resumed, (claim, snapshot, token) =>
        {
            Assert.AreEqual(first, claim); Assert.AreEqual(1L, snapshot.Count); return Task.FromResult(snapshot.ReadPage("", cancellationToken: token).Assets.Single().FileId);
        }).ConfigureAwait(false));
        await resumed.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(resumed.ExitConfirmed);
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes); Assert.IsTrue(File.Exists(fixture.SnapshotPath));
    }

    [TestMethod]
    public async Task CompleteCopyWithLostSealCheckpointIsInspectedInsteadOfRecaptured()
    {
        await using var fixture = new Fixture(); var claim = fixture.Begin(); CatalogSnapshotDescriptor proof;
        using (var preparation = new CatalogPreparationDirectory(fixture.Path, fixture.Database.InstallationId))
        using (var frozen = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), preparation, fixture.Budget))
        using (var store = new CatalogGenerationSnapshotStore(fixture.Path, fixture.Database, fixture.Budget)) proof = store.Save(frozen, claim, CancellationToken.None);
        fixture.Add("later"); await using var owner = fixture.Owner();
        var actual = await fixture.Run(owner, (sealedClaim, snapshot, _) =>
        { Assert.AreEqual(1L, snapshot.Count); return Task.FromResult(sealedClaim.Snapshot); }).ConfigureAwait(false);
        Assert.AreEqual(proof, actual);
    }

    [TestMethod]
    public async Task PartialClaimIsPreservedAndNeverOverwrittenByCurrentLibrary()
    {
        await using var fixture = new Fixture(); fixture.Begin();
        using (var store = new CatalogGenerationSnapshotStore(fixture.Path, fixture.Database, fixture.Budget)) { }
        var partial = new byte[] { 1, 2, 3, 4 }; await File.WriteAllBytesAsync(fixture.SnapshotPath, partial).ConfigureAwait(false);
        await using var owner = fixture.Owner();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Run<bool>(owner, (_, _, _) => throw new AssertFailedException("A partial capture must never reach its consumer."))).ConfigureAwait(false);
        CollectionAssert.AreEqual(partial, await File.ReadAllBytesAsync(fixture.SnapshotPath).ConfigureAwait(false));
        Assert.AreEqual("capturing", fixture.Read().Phase); Assert.IsNull(owner.Failure);
    }

    [TestMethod]
    public async Task ExitCancelsAndJoinsConsumerBeforeRetainedReaderAndStateRelease()
    {
        await using var fixture = new Fixture(); await using var owner = fixture.Owner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Task? stopping = null;
        var running = fixture.Run(owner, async (_, snapshot, token) =>
        { entered.TrySetResult(); await release.Task.ConfigureAwait(false); Assert.AreEqual(1L, snapshot.Count); token.ThrowIfCancellationRequested(); return true; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = owner.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(owner.ExitConfirmed); Assert.IsTrue(File.Exists(fixture.SnapshotPath));
            Assert.AreEqual(1, await fixture.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ReadPage().Assets.Count, CancellationToken.None).ConfigureAwait(false));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => running).ConfigureAwait(false); Assert.IsNotNull(stopping); await stopping.ConfigureAwait(false);
        Assert.IsTrue(owner.ExitConfirmed); Assert.IsTrue(File.Exists(fixture.SnapshotPath)); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => fixture.Run(owner, (_, _, _) => Task.FromResult(true))).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RevocationAfterSealingBlocksConsumerAndLeavesFrozenWorkRecoverable()
    {
        await using var fixture = new Fixture(); fixture.Authority.OnValidate = calls =>
        { if (calls == 3) throw new UnauthorizedAccessException("Synthetic reconciled revocation"); };
        await using var owner = fixture.Owner();
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => fixture.Run<bool>(owner, (_, _, _) => throw new AssertFailedException("Revoked work cannot reach a consumer."))).ConfigureAwait(false);
        Assert.AreEqual("frozen", fixture.Read().Phase); Assert.IsTrue(File.Exists(fixture.SnapshotPath));
        fixture.Authority.OnValidate = null;
        Assert.AreEqual(1L, await fixture.Run(owner, (_, snapshot, _) => Task.FromResult(snapshot.Count)).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalSourcePolicyChangeRejectsOldContextEvenIfExternalAuthorityReturnsSuccessfully()
    {
        await using var fixture = new Fixture(); await using var owner = fixture.Owner();
        await fixture.Run(owner, (_, snapshot, _) => Task.FromResult(snapshot.Count)).ConfigureAwait(false);
        var before = fixture.Read(); var bytes = await File.ReadAllBytesAsync(fixture.SnapshotPath).ConfigureAwait(false);
        new LibrarySourceRepository(fixture.Database).Update(fixture.Source, false, true, 1, 2);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => fixture.Run(owner, (_, _, _) => Task.FromResult(true))).ConfigureAwait(false);
        Assert.AreEqual(before, fixture.Read()); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.SnapshotPath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task FailedAdmissionAndEarlyCancellationCreateNeitherCaptureNorFiles()
    {
        await using var fixture = new Fixture(); await using var owner = fixture.Owner(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => owner.RunAsync(fixture.Scope, fixture.SnapshotId, fixture.IntentId, (_, _, _) => Task.FromResult(true), canceled.Token)).ConfigureAwait(false);
        fixture.Authority.OnValidate = _ => throw new UnauthorizedAccessException("Synthetic unreconciled account");
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => fixture.Run(owner, (_, _, _) => Task.FromResult(true))).ConfigureAwait(false);
        Assert.IsNull(new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget).Read(fixture.Scope.GenerationId));
        Assert.IsFalse(Directory.Exists(System.IO.Path.GetDirectoryName(fixture.SnapshotPath)));
    }

    [TestMethod]
    public async Task SameGenerationConsumersSerializeAndFailureDoesNotReplaceItsSnapshot()
    {
        await using var fixture = new Fixture(); await using var owner = fixture.Owner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Run(owner, async (_, _, _) => { entered.TrySetResult(); await release.Task.ConfigureAwait(false); return await Task.FromException<bool>(new InvalidOperationException("Synthetic consumer failure")).ConfigureAwait(false); });
        Task<long>? second = null; var secondEntered = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); fixture.Add("later");
            second = fixture.Run(owner, (_, snapshot, _) => { secondEntered = true; return Task.FromResult(snapshot.Count); }); Assert.IsFalse(secondEntered);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first).ConfigureAwait(false);
        Assert.IsNotNull(second); Assert.AreEqual(1L, await second.ConfigureAwait(false)); Assert.IsNull(owner.Failure);
    }

    private sealed class Authority : IWorkspacePublicationAuthority
    {
        private int _calls;
        public Action<int>? OnValidate { get; set; }
        public Task ValidateAsync(WorkspacePublicationScope scope, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Assert.AreEqual("metadata-workspace", scope.FolderId); OnValidate?.Invoke(++_calls); return Task.CompletedTask; }
    }
    private sealed class Space : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 20 * StorageBudgetManager.Gibibyte, 0); }
    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly string[] Accounts = ["account-A"];
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mpa-generation-owner-" + Guid.NewGuid().ToString("N"));
        public Guid SnapshotId { get; } = Guid.NewGuid();
        public Guid IntentId { get; } = Guid.NewGuid();
        public Guid Source { get; } = Guid.NewGuid();
        public ProcessorStateDatabase Database { get; }
        public ProcessorStateCoordinator State { get; }
        public StorageBudgetManager Budget { get; } = new(new Space());
        public Authority Authority { get; } = new();
        public WorkspacePublicationScope Scope { get; }
        public string SnapshotPath => System.IO.Path.Combine(Path, "catalog-generation-snapshots", "snapshot-" + SnapshotId.ToString("N") + ".sqlite");
        public Fixture()
        {
            Database = new(Path); State = new(Database); var accounts = new DriveAccountRepository(Database); var pending = accounts.Begin(Guid.NewGuid());
            accounts.Promote(pending.Id, "account-A", "Synthetic", null); var library = new ProcessorLibraryRepository(Database).GetOrCreateIdentity();
            new LibrarySourceRepository(Database).Add(Source, "account-A", 1, "photo-folder", "Photos", true, 1); Add("original");
            var actor = Guid.NewGuid(); var publisher = Guid.NewGuid(); using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var publisherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var workspace = new CatalogWorkspace("account-A", "metadata-workspace"); var authority = new LibraryAuthorityRepository(Database, Budget);
            authority.Bootstrap(actor, 1, workspace, 1, controllerKey.ExportSubjectPublicKeyInfo(), sourceApproval: new(2, false));
            var key = publisherKey.ExportSubjectPublicKeyInfo();
            byte[] Signed(string kind, string capability, long sequence, object payload)
            {
                var body = new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa", major = 1, minor = 0, libraryId = library.ToString("D"),
                    actorId = actor.ToString("D"), epoch = "1", sequence = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), kind, requiredCapabilities = new[] { capability }, payload }));
                return new SignedProtocolDocuments([capability]).Encode(body, new ProtocolDocumentSigner(controllerKey).Sign(body));
            }
            authority.SelectPublisherPolicy(Signed("library-publisher-authorization", CatalogPublisherAuthorization.Capability, 1,
                new { workspace = new { accountId = workspace.AccountId, folderId = workspace.FolderId }, publisher = new { actorId = publisher.ToString("D"), epoch = "1", publicKeySpki = Convert.ToBase64String(key), keyId = Convert.ToHexStringLower(SHA256.HashData(key)) } }));
            authority.SelectSourceConfiguration(Signed("library-source-configuration", CatalogSourceConfiguration.Capability, 2,
                new { configurationRevision = "2", workspace = new { accountId = workspace.AccountId, folderId = workspace.FolderId }, accounts = Accounts,
                    sources = new[] { new { id = Source.ToString("D"), accountId = "account-A", folderId = "photo-folder", revision = "1", includeDescendants = true, enabled = true } } }));
            var current = authority.CaptureLocalPublicationAuthority(); Scope = new(current.LibraryId, current.ActorId, current.ActorEpoch, Guid.NewGuid(), current.AccountId, current.AccountRevision, current.FolderId, current.ConfigurationRevision, current.AuthoritySha256);
        }
        public void Add(string file) => new DiscoveredCatalogRepository(Database).ApplyBatch(Source, 1, 1, [new(file, 1, "Photo.jpg", "image/jpeg", 40, 30, 100, null, null, 0)]);
        public CatalogGenerationCaptureService Owner() => new(State, Path, Database.InstallationId, Budget, Authority);
        public CatalogGenerationCapture Begin() => new CatalogGenerationCaptureRepository(Database, Budget).Begin(Scope, SnapshotId, IntentId);
        public CatalogGenerationCapture Read() => new CatalogGenerationCaptureRepository(Database, Budget).Read(Scope.GenerationId)!;
        public Task<T> Run<T>(CatalogGenerationCaptureService owner, Func<CatalogGenerationCapture, FrozenCatalogSnapshot, CancellationToken, Task<T>> consumer) => owner.RunAsync(Scope, SnapshotId, IntentId, consumer, CancellationToken.None);
        public async ValueTask DisposeAsync()
        { await State.DisposeAsync().ConfigureAwait(false); Database.Dispose(); Directory.Delete(Path, recursive: true); }
    }
}
