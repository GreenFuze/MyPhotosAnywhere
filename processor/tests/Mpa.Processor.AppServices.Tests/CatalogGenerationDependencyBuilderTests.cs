using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class CatalogGenerationDependencyBuilderTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1001)]
    public async Task GeneratedDependenciesRoundTripThroughTheExistingCoveredTraversalWithoutPublishingAHead(int count)
    {
        using var fixture = new Fixture(18); var identity = new DriveAssetIdentity(fixture.Library);
        var files = Enumerable.Range(0, 600000).Select(index => "dense-" + index.ToString(CultureInfo.InvariantCulture))
            .Where(file => identity.Derive("account", file).ToString("N").StartsWith("ab", StringComparison.Ordinal)).Take(count).ToArray();
        Assert.AreEqual(count, files.Length); fixture.Add(files.Select(Fact).ToArray()); using var frozen = fixture.Freeze(); var sink = new Sink();
        var prepared = await new CatalogGenerationDependencyBuilder(Fixture.Workspace, sink).BuildAsync(frozen, fixture.Approved, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual((long)count, prepared.Assets); Assert.AreEqual(fixture.Approved.EnvelopeSha256, prepared.SourcePolicySha256);
        Assert.AreEqual(2, sink.Roles.Count(role => role == "coverage-page")); Assert.IsFalse(sink.Roles.Contains("catalog-commit"));
        var verified = fixture.Commit(prepared); var leaves = new List<CatalogFactsLeaf>();
        await foreach (var leaf in new CatalogFactsTraversal(fixture.Library, Fixture.Workspace, sink).ReadCoveredLeavesAsync(verified, (coverage, token) =>
            { token.ThrowIfCancellationRequested(); fixture.Approved.Configuration.ValidateCoverage(coverage); return Task.CompletedTask; }).ConfigureAwait(false)) leaves.Add(leaf);
        var ids = leaves.SelectMany(leaf => leaf.Facts.Assets).Select(asset => asset.Id).ToArray(); Assert.AreEqual(count, ids.Length); Assert.AreEqual(count, ids.Distinct().Count());
        CollectionAssert.AreEquivalent(files.Select(file => identity.Derive("account", file)).ToArray(), ids);
        if (count != 0) { Assert.IsTrue(leaves.All(leaf => leaf.Prefix.Length >= 4)); Assert.IsTrue(sink.Roles.Count(role => role == "facts-index") > 1); }
        else Assert.AreEqual(0, sink.Roles.Count(role => role == "facts-chunk"));
    }

    [TestMethod]
    [DataRow("scope")]
    [DataRow("bytes")]
    [DataRow("hash")]
    [DataRow("alias")]
    [DataRow("mutation")]
    public async Task UnverifiedChangedOrAliasedSinkResultsCannotProducePreparedDependencies(string fault)
    {
        using var fixture = new Fixture(); fixture.Add([Fact("retained")]); using var frozen = fixture.Freeze(); var sink = new Sink { Fault = fault };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new CatalogGenerationDependencyBuilder(Fixture.Workspace, sink)
            .BuildAsync(frozen, fixture.Approved, null, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsFalse(sink.Roles.Contains("catalog-commit")); Assert.AreEqual("retained", frozen.ReadPage("").Assets.Single().FileId);
    }

    [TestMethod]
    public async Task ChangedSourcePolicyIsRejectedBeforeAnyMetadataWrite()
    {
        using var fixture = new Fixture(); fixture.ChangeSource(); using var frozen = fixture.Freeze(); var sink = new Sink();
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => new CatalogGenerationDependencyBuilder(Fixture.Workspace, sink)
            .BuildAsync(frozen, fixture.Approved, null, CancellationToken.None)).ConfigureAwait(false); Assert.AreEqual(0, sink.Roles.Count);
    }

    [TestMethod]
    public async Task CanceledPreparationJoinsItsActualSinkReadAndCannotPublishAHead()
    {
        using var fixture = new Fixture(); fixture.Add([Fact("retained")]); using var frozen = fixture.Freeze(); using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new Sink { Hold = async () => { started.TrySetResult(); await release.Task.ConfigureAwait(false); } };
        var building = new CatalogGenerationDependencyBuilder(Fixture.Workspace, sink).BuildAsync(frozen, fixture.Approved, null, caller.Token);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); await caller.CancelAsync().ConfigureAwait(false); Assert.IsFalse(building.IsCompleted); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => building).ConfigureAwait(false); Assert.IsFalse(sink.Roles.Contains("catalog-commit"));
    }

    private static DiscoveredDriveAsset Fact(string file) => new(file, 1, "Photo.jpg", "image/jpeg", 40, 30, 100, null, null, 0);
    private sealed class Sink : ICatalogGenerationObjectSink, ICatalogObjectSource
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        public List<string> Roles { get; } = [];
        public string? Fault { get; init; }
        public Func<Task>? Hold { get; init; }
        public async Task<CatalogObjectReference> StoreAsync(string role, string mimeType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (Hold is not null) await Hold().ConfigureAwait(false); // Intentionally ignores cancellation to test actual joining.
            Assert.IsTrue(mimeType is "application/json" or "application/gzip"); Roles.Add(role);
            if (Fault == "mutation" && MemoryMarshal.TryGetArray(payload, out var array)) array.Array![array.Offset] ^= 1;
            var file = Fault == "alias" ? "repeated-file" : "object-" + Roles.Count.ToString(CultureInfo.InvariantCulture); var bytes = payload.ToArray(); _objects[file] = bytes;
            return new("account", file, Fault == "scope" ? "another-folder" : "metadata", bytes.Length + (Fault == "bytes" ? 1 : 0),
                Fault == "hash" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        public Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<ReadOnlyMemory<byte>>(_objects[reference.FileId]); }
    }
    private sealed class Space : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 20 * StorageBudgetManager.Gibibyte, 0); }
    private sealed class Fixture : IDisposable
    {
        public static readonly CatalogWorkspace Workspace = new("account", "metadata");
        private static readonly string[] CommitCapabilities = [DriveCatalogFacts.Capability, CatalogPublicationReader.Capability];
        private static readonly string[] SourceCapabilities = [CatalogSourceConfiguration.Capability];
        private static readonly string[] ApprovedAccounts = ["account"];
        private readonly string _path = Path.Combine(Path.GetTempPath(), "mpa-generation-builder-" + Guid.NewGuid().ToString("N"));
        private readonly ProcessorStateDatabase _database;
        private readonly CatalogPreparationDirectory _directory;
        private readonly ECDsa _controller = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly StorageBudgetManager _storage = new(new Space());
        private readonly Guid _source = Guid.NewGuid();
        private readonly Guid _actor = Guid.NewGuid();
        public Guid Library { get; }
        public AcceptedSourceConfiguration Approved { get; }
        public Fixture(int sourceCount = 1)
        {
            _database = new(_path); Library = new ProcessorLibraryRepository(_database).GetOrCreateIdentity();
            var accounts = new DriveAccountRepository(_database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account", "Synthetic", null);
            var sources = new LibrarySourceRepository(_database); sources.Add(_source, "account", 1, "photos", "Photos", true, 1);
            for (var index = 1; index < sourceCount; index++) sources.Add(Guid.NewGuid(), "account", 1, "photos-" + index.ToString(CultureInfo.InvariantCulture), "Photos", true, sources.Snapshot().Revision);
            var snapshot = sources.Snapshot(); var authority = new LibraryAuthorityRepository(_database, _storage);
            authority.Bootstrap(_actor, 1, Workspace, 1, _controller.ExportSubjectPublicKeyInfo(), sourceApproval: new(snapshot.Revision, false));
            var body = Document("library-source-configuration", SourceCapabilities, new { configurationRevision = snapshot.Revision.ToString(CultureInfo.InvariantCulture),
                workspace = new { accountId = "account", folderId = "metadata" }, accounts = ApprovedAccounts, sources = snapshot.Sources.Select(source => new {
                    id = source.Id.ToString("D"), accountId = source.AccountId, folderId = source.FolderId, revision = source.Revision.ToString(CultureInfo.InvariantCulture),
                    includeDescendants = source.IncludeDescendants, enabled = source.Enabled }) });
            Approved = authority.SelectSourceConfiguration(new SignedProtocolDocuments(SourceCapabilities).Encode(body, new ProtocolDocumentSigner(_controller).Sign(body)));
            _directory = new(_path, _database.InstallationId);
        }
        public void Add(DiscoveredDriveAsset[] facts)
        { foreach (var batch in facts.Chunk(250)) new DiscoveredCatalogRepository(_database).ApplyBatch(_source, 1, 1, batch); }
        public void ChangeSource() { var sources = new LibrarySourceRepository(_database); sources.Update(_source, false, true, 1, sources.Snapshot().Revision); }
        public FrozenCatalogSnapshot Freeze() => FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(_database), _directory, _storage);
        public VerifiedProtocolDocument Commit(PreparedCatalogDependencies prepared)
        {
            var body = Document("catalog-commit", CommitCapabilities, new { generationId = Guid.NewGuid().ToString("D"), parent = (object?)null,
                rootIndex = Wire(prepared.RootIndex), sourceCoverage = Wire(prepared.SourceCoverage), settingsRevision = "0", minimumHistoryGeneration = "1" });
            return new ProtocolDocumentReader(CommitCapabilities).VerifyAndRead(body, new ProtocolDocumentSigner(_controller).Sign(body),
                new(Library, _actor, 1, _controller.ExportSubjectPublicKeyInfo(), ["catalog-commit"])); // Isolated traversal fixture, not publication authorization.
        }
        private byte[] Document(string kind, string[] capabilities, object payload) => new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new {
            format = "mpa", major = 1, minor = 0, libraryId = Library.ToString("D"), actorId = _actor.ToString("D"), epoch = "1", sequence = "1", kind, requiredCapabilities = capabilities, payload }));
        private static object Wire(CatalogObjectReference value) => new { accountId = value.AccountId, fileId = value.FileId, workspaceId = value.WorkspaceId,
            bytes = value.Bytes.ToString(CultureInfo.InvariantCulture), sha256 = value.Sha256 };
        public void Dispose() { _directory.Dispose(); _database.Dispose(); _controller.Dispose(); Directory.Delete(_path, recursive: true); }
    }
}
