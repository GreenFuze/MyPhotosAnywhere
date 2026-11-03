using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CatalogCommitHistoryTraversalTests
{
    private static readonly string[] Capabilities = [DriveCatalogFacts.Capability, CatalogPublicationReader.Capability];
    private static readonly string[] Kinds = ["catalog-commit"];

    [TestMethod]
    public async Task CompleteHistoryVerifiesEachActualSignatureAndDoesNotPrefetchPastConsumerPersistence()
    {
        using var fixture = new Fixture(); var first = fixture.Add(1); var second = fixture.Add(2, first); var head = fixture.Add(3, second);
        var expected = 3L;
        await foreach (var entry in fixture.Reader().ReadAsync(head).ConfigureAwait(false))
        {
            Assert.AreEqual(expected, entry.Verified.Sequence); Assert.AreEqual(4 - expected, fixture.Source.Reads);
            CollectionAssert.AreEqual(fixture.Source.Objects[entry.Reference.FileId], entry.Envelope.ToArray());
            Assert.AreEqual(expected == 1, entry.Commit.Parent is null); expected--;
        }
        Assert.AreEqual(0L, expected); Assert.AreEqual(3, fixture.Source.Reads);
        fixture.Source.Reads = 0;
        await using var partial = fixture.Reader().ReadAsync(head).GetAsyncEnumerator();
        Assert.IsTrue(await partial.MoveNextAsync().ConfigureAwait(false)); Assert.AreEqual(1, fixture.Source.Reads);
    }

    [TestMethod]
    public async Task MissingOrChangedPredecessorFailsAfterTheHeadWithoutClaimingCompleteHistory()
    {
        using var fixture = new Fixture(); var first = fixture.Add(1); var head = fixture.Add(2, first);
        var original = fixture.Source.Objects[first.FileId]; fixture.Source.Objects.Remove(first.FileId); var emitted = 0;
        async Task Read() { await foreach (var _ in fixture.Reader().ReadAsync(head).ConfigureAwait(false)) emitted++; }
        await Assert.ThrowsExactlyAsync<IOException>(Read).ConfigureAwait(false); Assert.AreEqual(1, emitted);
        fixture.Source.Objects[first.FileId] = original.ToArray(); fixture.Source.Objects[first.FileId][^2] ^= 1; emitted = 0;
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(Read).ConfigureAwait(false); Assert.AreEqual(1, emitted);
        CollectionAssert.AreNotEqual(original, fixture.Source.Objects[first.FileId]);
    }

    [TestMethod]
    public async Task ValidSignaturesCannotHideASequenceGapRepeatedGenerationOrCommitDependencyAlias()
    {
        using var fixture = new Fixture(); var generation = Guid.NewGuid(); var first = fixture.Add(1, generation: generation);
        await Reject(fixture.Add(3, first)).ConfigureAwait(false);
        await Reject(fixture.Add(2, first, generation)).ConfigureAwait(false);
        var aliasedFirst = fixture.Add(1, rootFile: "commit-2-aliased", name: "aliased-first");
        await Reject(fixture.Add(2, aliasedFirst, name: "commit-2-aliased")).ConfigureAwait(false);
        async Task Reject(CatalogObjectReference head)
        {
            var emitted = 0;
            async Task Read() { await foreach (var _ in fixture.Reader().ReadAsync(head).ConfigureAwait(false)) emitted++; }
            await Assert.ThrowsExactlyAsync<ProtocolFormatException>(Read).ConfigureAwait(false); Assert.AreEqual(1, emitted);
        }
    }

    [TestMethod]
    public async Task ReceivedReferencesAndSignaturesCannotChooseWorkspaceKeyEpochOrAnUnboundedHistory()
    {
        using var fixture = new Fixture(); var first = fixture.Add(1);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => Drain(fixture.Reader(), first with { AccountId = "foreign" })).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Source.Reads);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => Drain(fixture.Reader(), first with { Bytes = 262145 })).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Source.Reads);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => Drain(fixture.Reader(), fixture.Add(1, epoch: 2))).ConfigureAwait(false);
        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => Drain(fixture.Reader(), fixture.Add(1, key: foreignKey))).ConfigureAwait(false);
        var second = fixture.Add(2, first);
        await Assert.ThrowsExactlyAsync<CatalogTraversalLimitException>(() => Drain(fixture.Reader(1), second)).ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Reader(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Reader(4097));
    }

    [TestMethod]
    public async Task CancellationJoinsTheActualReadAndCannotEmitItsLateResult()
    {
        using var fixture = new Fixture(); var head = fixture.Add(1); using var cancellation = new CancellationTokenSource();
        fixture.Source.Hold = true;
        var reading = Drain(fixture.Reader(), head, cancellation.Token);
        await fixture.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try { await cancellation.CancelAsync().ConfigureAwait(false); Assert.IsFalse(reading.IsCompleted); }
        finally { fixture.Source.Release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false); Assert.AreEqual(1, fixture.Source.Reads);
    }

    private static async Task Drain(CatalogCommitHistoryTraversal reader, CatalogObjectReference head, CancellationToken token = default)
    { await foreach (var _ in reader.ReadAsync(head, token).ConfigureAwait(false)) { } }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly Guid _library = Guid.NewGuid();
        private readonly Guid _actor = Guid.NewGuid();
        private readonly CatalogWorkspace _workspace = new("account", "metadata");
        public Source Source { get; } = new();
        public CatalogCommitHistoryTraversal Reader(int maximum = CatalogCommitHistoryTraversal.MaximumCommits) =>
            new(_workspace, new(_library, _actor, 1, _key.ExportSubjectPublicKeyInfo(), Kinds), Source, maximum);
        public CatalogObjectReference Add(long sequence, CatalogObjectReference? parent = null, Guid? generation = null,
            string? rootFile = null, string? name = null, long epoch = 1, ECDsa? key = null)
        {
            name ??= "commit-" + Guid.NewGuid().ToString("N");
            CatalogObjectReference Metadata(string file) => new("account", file, "metadata", 2, Convert.ToHexStringLower(SHA256.HashData("{}"u8)));
            var payload = new CatalogPublicationWriter(_workspace).EncodeCommit(new(generation ?? Guid.NewGuid(), parent,
                Metadata(rootFile ?? "index-" + name), Metadata("coverage-" + name), 1, 1), sequence);
            using var document = JsonDocument.Parse(payload);
            var body = new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "mpa", major = 1, minor = 0, libraryId = _library.ToString("D"), actorId = _actor.ToString("D"),
                epoch = epoch.ToString(CultureInfo.InvariantCulture), sequence = sequence.ToString(CultureInfo.InvariantCulture),
                kind = "catalog-commit", requiredCapabilities = Capabilities, payload = document.RootElement
            }));
            var signature = new ProtocolDocumentSigner(key ?? _key).Sign(body);
            var bytes = new SignedProtocolDocuments(Capabilities).Encode(body, signature); Source.Objects.Add(name, bytes);
            return new("account", name, "metadata", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        public void Dispose() => _key.Dispose();
    }
    private sealed class Source : ICatalogObjectSource
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public int Reads { get; set; }
        public bool Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++; Entered.TrySetResult();
            if (Hold) await Release.Task.ConfigureAwait(false);
            return Objects.TryGetValue(reference.FileId, out var bytes) ? bytes : throw new IOException("Synthetic unavailable predecessor.");
        }
    }
}
