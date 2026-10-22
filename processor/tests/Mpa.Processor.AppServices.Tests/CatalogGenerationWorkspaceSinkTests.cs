using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task DependencyRetriesReuseCapturedOutboxIdentityButAlwaysReadCurrentRemoteBytes()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var seed = await PreparePublication(fixture, budget).ConfigureAwait(false);
        var authority = new PublicationAuthority(); var remote = new DependencyRemote(fixture, budget, seed.Scope);
        var first = await remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None).ConfigureAwait(false);
        var second = await remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first, second); Assert.AreEqual(1, remote.Allocations); Assert.AreEqual(1, remote.Creates); Assert.AreEqual(2, remote.Reads);
        Assert.AreEqual(1, fixture.Readers.Opened.Count);
    }

    [TestMethod]
    public async Task LostDependencyCompletionRetainsExactFileIdAndBytesAcrossNewSinkOwner()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var seed = await PreparePublication(fixture, budget).ConfigureAwait(false);
        var authority = new PublicationAuthority(); var remote = new DependencyRemote(fixture, budget, seed.Scope) { LoseFirstReply = true };
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None)).ConfigureAwait(false);
        var pending = await fixture.State.ExecuteAsync(db => new WorkspacePublicationRepository(db, budget).Pending()
            .Select(id => new WorkspacePublicationRepository(db, budget).Read(id)!).Single(row => row.FileId is not null), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(pending.VerifiedVersion); Assert.AreEqual("dependency-1", pending.FileId);
        var resumed = await remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pending.FileId, resumed.FileId); Assert.AreEqual(1, remote.Allocations); Assert.AreEqual(2, remote.Creates); Assert.AreEqual(1, remote.Reads);
    }

    [TestMethod]
    public async Task OldReceiptCannotHideRemoteDeletionOrReturnAReferenceWithoutCurrentReadback()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var seed = await PreparePublication(fixture, budget).ConfigureAwait(false);
        var remote = new DependencyRemote(fixture, budget, seed.Scope); var authority = new PublicationAuthority();
        var first = await remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None).ConfigureAwait(false);
        remote.Objects.Remove(first.FileId);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, remote.Allocations); Assert.AreEqual(1, remote.Creates); Assert.AreEqual(2, remote.Reads); Assert.AreEqual(0, remote.Objects.Count);
    }

    [TestMethod]
    public async Task DependencyAuthorityRevocationAndMutatedReadbackAreNeverAccepted()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var seed = await PreparePublication(fixture, budget).ConfigureAwait(false);
        var remote = new DependencyRemote(fixture, budget, seed.Scope); var authority = new PublicationAuthority { Allowed = false };
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, remote.Allocations); authority.Allowed = true;
        var first = await remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None).ConfigureAwait(false);
        remote.Objects[first.FileId] = "[]"u8.ToArray();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None)).ConfigureAwait(false);
        remote.Objects[first.FileId] = WorkspacePayload;
        fixture.Readers.WorkspaceRead = (folder, file, _, _, _) => { authority.Allowed = false; return Task.FromResult(new DriveWorkspaceObject(seed.Scope.AccountId, file, folder, 7, WorkspacePayload)); };
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, remote.Creates);
    }

    [TestMethod]
    public async Task DependencyScopeAndRoleNamespacesCannotOverwriteACommitPolicyOrAnotherGeneration()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var seed = await PreparePublication(fixture, budget).ConfigureAwait(false);
        var remote = new DependencyRemote(fixture, budget, seed.Scope); var authority = new PublicationAuthority();
        var first = await remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None).ConfigureAwait(false);
        var otherRole = await remote.Sink(authority).StoreAsync("coverage-root", "application/json", WorkspacePayload, CancellationToken.None).ConfigureAwait(false);
        var later = new CatalogGenerationWorkspaceSink(fixture.State, fixture.Service, budget, authority, seed.Scope with { GenerationId = Guid.NewGuid() });
        var next = await later.StoreAsync("facts-index", "application/json", WorkspacePayload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(first.FileId, otherRole.FileId); Assert.AreNotEqual(first.FileId, next.FileId);
        foreach (var role in new[] { "catalog-commit", "source-configuration", "original", "publisher-authorization" })
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => remote.Sink(authority).StoreAsync(role, "application/json", WorkspacePayload, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => remote.Sink(authority).StoreAsync("facts-chunk", "application/json", WorkspacePayload, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3, remote.Creates);
    }

    [TestMethod]
    public async Task DependencyOwnsCallerBytesBeforeAwaitAndCancellationJoinsActualReadback()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var seed = await PreparePublication(fixture, budget).ConfigureAwait(false);
        var remote = new DependencyRemote(fixture, budget, seed.Scope); var authority = new HeldDependencyAuthority(); var bytes = WorkspacePayload.ToArray();
        var storing = remote.Sink(authority).StoreAsync("facts-index", "application/json", bytes, CancellationToken.None);
        await authority.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try { bytes[0] = (byte)'['; }
        finally { authority.Release.TrySetResult(); }
        var reference = await storing.ConfigureAwait(false); CollectionAssert.AreEqual(WorkspacePayload, remote.Objects[reference.FileId]);
        using var cancel = new CancellationTokenSource();
        fixture.Readers.WorkspaceRead = async (folder, file, _, _, _) =>
        { fixture.Readers.Started.TrySetResult(); await fixture.Readers.ReleaseRead.Task.ConfigureAwait(false); return new(seed.Scope.AccountId, file, folder, 7, WorkspacePayload); };
        var reading = remote.Sink(authority).StoreAsync("facts-index", "application/json", WorkspacePayload, cancel.Token);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try { await cancel.CancelAsync().ConfigureAwait(false); Assert.IsFalse(reading.IsCompleted); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false); Assert.AreEqual(1, remote.Creates);
    }

    [TestMethod]
    public async Task DependencyWireBoundsCanonicalBytesAndCapturedScopeFailBeforeNetworkWrites()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var seed = await PreparePublication(fixture, budget).ConfigureAwait(false);
        var remote = new DependencyRemote(fixture, budget, seed.Scope); var authority = new PublicationAuthority(); var sink = remote.Sink(authority);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => sink.StoreAsync("facts-chunk", "application/gzip", ReadOnlyMemory<byte>.Empty, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => sink.StoreAsync("facts-chunk", "application/gzip", new byte[WorkspacePublicationRepository.MaximumPayloadBytes + 1], CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => sink.StoreAsync("facts-index", "application/json", new byte[Mpa.Processor.Protocol.ProtocolDocumentReader.MaximumDocumentBytes + 1], CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<Mpa.Processor.Protocol.ProtocolFormatException>(() => sink.StoreAsync("facts-index", "application/json", " {}"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => new CatalogGenerationWorkspaceSink(fixture.State, fixture.Service, budget, authority, seed.Scope with { GenerationId = Guid.Empty }));
        Assert.ThrowsExactly<ArgumentException>(() => new CatalogGenerationWorkspaceSink(fixture.State, fixture.Service, budget, authority, seed.Scope with { FolderId = "root" }));
        Assert.AreEqual(0, remote.Allocations); Assert.AreEqual(0, remote.Creates); Assert.AreEqual(0, remote.Reads);
    }

    private sealed class HeldDependencyAuthority : IWorkspacePublicationAuthority
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ValidateAsync(WorkspacePublicationScope scope, CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task.ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); }
    }
    private sealed class DependencyRemote
    {
        private readonly Fixture _fixture;
        private readonly StorageBudgetManager _budget;
        private readonly WorkspacePublicationScope _scope;
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public int Allocations { get; private set; }
        public int Creates { get; private set; }
        public int Reads { get; private set; }
        public bool LoseFirstReply { get; init; }
        public DependencyRemote(Fixture fixture, StorageBudgetManager budget, WorkspacePublicationScope scope)
        {
            _fixture = fixture; _budget = budget; _scope = scope;
            fixture.Readers.WorkspaceAllocate = _ => Task.FromResult("dependency-" + ++Allocations);
            fixture.Readers.WorkspaceCreate = async (folder, file, _, payload, hash, token) =>
            {
                var durable = await fixture.State.ExecuteAsync(db => new WorkspacePublicationRepository(db, budget).Pending()
                    .Select(id => new WorkspacePublicationRepository(db, budget).Read(id)!).Single(row => row.FileId == file), token).ConfigureAwait(false);
                Assert.AreEqual(folder, durable.Scope.FolderId); Assert.AreEqual(hash, durable.Sha256); CollectionAssert.AreEqual(durable.Payload.ToArray(), payload.ToArray());
                Creates++; if (Objects.TryGetValue(file, out var old)) CollectionAssert.AreEqual(old, payload.ToArray()); Objects[file] = payload.ToArray();
                if (LoseFirstReply && Creates == 1) throw new HttpRequestException("Synthetic lost dependency completion reply");
                return new(scope.AccountId, file, folder, 7, payload);
            };
            fixture.Readers.WorkspaceRead = (folder, file, _, _, _) =>
            {
                Reads++; if (!Objects.TryGetValue(file, out var bytes)) throw new HttpRequestException("Synthetic metadata not found", null, HttpStatusCode.NotFound);
                return Task.FromResult(new DriveWorkspaceObject(scope.AccountId, file, folder, 7, bytes));
            };
        }
        public CatalogGenerationWorkspaceSink Sink(IWorkspacePublicationAuthority authority) => new(_fixture.State, _fixture.Service, _budget, authority, _scope);
    }
}
