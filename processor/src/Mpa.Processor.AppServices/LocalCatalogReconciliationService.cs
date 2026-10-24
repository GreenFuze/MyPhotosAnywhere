using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>A completed read of this installation's current workspace. This is not a
/// transferable publication/activation capability. Pending identities must be recovered
/// under their original owners before admitting another generation.</summary>
public sealed record ReconciledLocalCatalog(LocalCatalogPublicationAuthority Authority, WorkspaceInventoryCheckpoint Observation,
    CatalogObjectReference? Parent, long Sequence, long Assets, ReadOnlyCollection<Guid> PendingSigning, ReadOnlyCollection<Guid> PendingPublications);

/// <summary>Joined ownership of actual Drive metadata reconciliation, owned signing
/// history, current controller policies and the complete signed/covered current head.
/// Starts closed on every attempt; no historical receipt, browser or caller record can
/// grant permission. ReconcileAsync is read-only. Its private one-object admissions
/// cover dependencies and exact captured commits; no activation or original action exists.
/// Retire this owner before its borrowed native keys, accounts, budget and state.</summary>
public sealed partial class LocalCatalogReconciliationService(ProcessorStateCoordinator state, ProcessorAccountService accounts,
    LibrarySigningIdentityOwner keys, StorageBudgetManager budget) : IAsyncDisposable
{
    private static readonly string[] Capabilities = [CatalogPublisherAuthorization.Capability, CatalogSourceConfiguration.Capability,
        CatalogPublicationReader.Capability, DriveCatalogFacts.Capability];
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly LibrarySigningIdentityOwner _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private bool _stopping;
    private bool _exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    private ReconciledLocalCatalog? _current;
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public Exception? Failure { get { lock (_gate) return _failure; } }
    public ReconciledLocalCatalog? Current { get { lock (_gate) return _current; } }

    public Task<ReconciledLocalCatalog> ReconcileAsync(CancellationToken cancellationToken) => ReconcileAsync(null, cancellationToken);

    private async Task<ReconciledLocalCatalog> ReconcileAsync(CapturedCatalogCommitPlan? prepared, CancellationToken cancellationToken,
        Guid? renewedGenerationId = null, IncrementalBaseline? baseline = null)
    {
        CancellationTokenSource linked;
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock (_gate) { Check(linked.Token); _current = null; _completedRead = null; }
                var captured = await _state.ExecuteAsync(database =>
                {
                    Check(linked.Token); var authority = new LibraryAuthorityRepository(database, _budget);
                    var pin = authority.Controller() ?? throw new CatalogPublicationNotAuthorizedException();
                    var local = authority.CaptureLocalPublicationAuthority(); _keys.AssertPublicationIdentity(pin, local);
                    var sharing = authority.BootstrapSources() ?? throw new CatalogPublicationNotAuthorizedException();
                    var signing = new ProtocolSigningJournal(database, _budget).Inventory(local.LibraryId);
                    return new Captured(pin, local, _keys.PublicIdentity, signing, sharing.AllowSharedMetadata,
                        renewedGenerationId is { } generationId ? CapturePendingRenewedSignature(database, generationId, local, signing) : null);
                }, linked.Token).ConfigureAwait(false);
                var access = await InspectAsync(captured, linked.Token).ConfigureAwait(false);
                var context = new WorkspaceObservationContext(captured.Authority.LibraryId, captured.Authority.AccountId,
                    captured.Authority.FolderId, captured.Authority.AccountRevision, access.SharedDriveId);
                var publications = await _state.ExecuteAsync(database => new WorkspacePublicationRepository(database, _budget).Inventory(context), linked.Token).ConfigureAwait(false);
                if (baseline is not null)
                {
                    if (prepared is not null || renewedGenerationId is not null) throw new InvalidOperationException("Prepared and renewed reads require complete inventory reconciliation.");
                    await AssertIncrementalBaselineAsync(baseline, captured, publications, access, linked.Token).ConfigureAwait(false);
                }
                var full = new CatalogWorkspaceDocumentSource(_state, _accounts, new(context.AccountId, context.FolderId), context.SharedDriveId, context.AccountRevision, allMetadata: true);
                var source = new CatalogReconciliationDocumentSource(full, baseline?.Read.Inventory, baseline?.Expected,
                    baseline is null ? null : (candidate, token) => ReadBaselineBytesAsync(baseline, candidate, token));
                var run = new Scan(this, captured, context, source, publications);
                await using var scanner = new WorkspaceObservationService(_state, _budget, context, source, baseline?.Read.Result.Observation, baseline?.Read.AttemptId);
                var checkpoint = await scanner.ScanObservedAsync(run.ObserveAsync, linked.Token, async token =>
                {
                    await run.VerifyHeadAsync(token).ConfigureAwait(false);
                    if (prepared is not null) await run.VerifyPreparedAsync(prepared, token).ConfigureAwait(false);
                    var after = await InspectAsync(captured, token).ConfigureAwait(false);
                    if (after != access) throw new WorkspaceInventoryIncompleteException();
                    await AssertCapturedAsync(captured, publications, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
                var attemptId = scanner.CompletedAttemptId ?? throw new InvalidOperationException("A completed scan lost its actual attempt identity.");
                Check(linked.Token);
                await AssertCapturedAsync(captured, publications, linked.Token).ConfigureAwait(false);
                await _state.ExecuteAsync(database =>
                {
                    Check(linked.Token); new WorkspaceObservationRepository(database, _budget).AssertCompletedScan(checkpoint, attemptId); return true;
                }, linked.Token).ConfigureAwait(false);
                var result = run.Result(checkpoint);
                var inventory = source.CompletedInventory ?? throw new InvalidOperationException("A completed scan lost its actual final inventory.");
                var known = await _state.ExecuteAsync(database => new WorkspaceObservationRepository(database, _budget).Known(context), linked.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    Check(linked.Token); _failure = null;
                    _completedRead = new(result, captured, publications, prepared, inventory, known, access, baseline is not null, attemptId); return _current = result;
                }
            }
            catch (Exception error) { lock (_gate) { _current = null; _completedRead = null; _failure = error; } throw; }
            finally { _serial.Release(); }
        }
    }

    private Task<bool> AssertCapturedAsync(Captured captured, WorkspacePublicationInventory publications, CancellationToken token) =>
        _state.ExecuteAsync(database =>
        {
            Check(token); var authority = new LibraryAuthorityRepository(database, _budget);
            if (authority.Controller() != captured.Pin || authority.CaptureLocalPublicationAuthority() != captured.Authority || _keys.PublicIdentity != captured.Native)
                throw new StaleLibraryConfigurationException();
            _keys.AssertPublicationIdentity(captured.Pin, captured.Authority);
            new ProtocolSigningJournal(database, _budget).AssertInventoryUnchanged(captured.Signing);
            new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(publications); return true;
        }, token);

    private Task<Access> InspectAsync(Captured captured, CancellationToken token) =>
        _accounts.ReadDriveAsync(captured.Authority.AccountId, async (reader, revision, ownedToken) =>
        {
            Check(token); if (revision != captured.Authority.AccountRevision) throw new StaleLibraryConfigurationException();
            if (reader is not IGoogleWorkspaceSetup setup) throw new InvalidOperationException("Current catalog reconciliation requires actual workspace inspection.");
            var actual = await setup.InspectWorkspaceAsync(captured.Authority.FolderId, ownedToken).ConfigureAwait(false); Check(ownedToken);
            var folder = await reader.ReadFolderAsync(captured.Authority.FolderId, ownedToken).ConfigureAwait(false); Check(ownedToken);
            if (actual.AccountId != captured.Authority.AccountId || actual.FolderId != captured.Authority.FolderId || actual.DriveVersion < 1 ||
                folder.Id != captured.Authority.FolderId || !folder.CanListChildren || folder.SharedDriveId is not null && !actual.Shared)
                throw new InvalidDataException("Current workspace inspection substituted its captured access context.");
            if (actual.Shared && !captured.AllowShared) throw new LibraryWorkspaceApprovalException("Review this workspace's sharing permission before publishing private metadata.");
            return new Access(actual.DriveVersion, actual.Shared, folder.SharedDriveId);
        }, token);

    private sealed record Captured(LibraryControllerPin Pin, LocalCatalogPublicationAuthority Authority,
        LibrarySigningPublicIdentity Native, ProtocolSigningInventory Signing, bool AllowShared, PendingCapturedSignature? Recovery);
    private sealed record Access(long Version, bool Shared, string? SharedDriveId);

    private sealed partial class Scan(LocalCatalogReconciliationService owner, Captured captured, WorkspaceObservationContext context,
        IWorkspaceDocumentSource source, WorkspacePublicationInventory publications) : ICatalogObjectSource
    {
        private readonly Dictionary<string, WorkspaceDocumentObservation> _observed = new(StringComparer.Ordinal);
        private readonly HashSet<Guid> _signed = [];
        private readonly Dictionary<long, CatalogObjectReference> _sourcePolicies = [];
        private readonly List<Head> _heads = [];
        private readonly List<Guid> _pendingSigning = [];
        private Head? _head;
        private long _assets;
        public async Task ObserveAsync(DriveWorkspaceObject document, WorkspaceDocumentObservation observation, CancellationToken token)
        {
            owner.Check(token); _observed.Add(document.FileId, observation);
            if (observation.MimeType is not (null or "application/json")) return;
            var hint = Route(document.Bytes); if (hint is null) return;
            // Actor/epoch are routing hints only; the retained local journal verifies
            // the actual signature, scope, sequence and exact previously sealed bytes.
            var (actor, epoch) = hint.Value;
            if (!captured.Signing.Actors.Any(item => item.ActorId == actor && item.Epoch == epoch)) throw new SigningHistoryRecoveryRequiredException();
            var intent = await owner._state.ExecuteAsync(database =>
            {
                owner.Check(token);
                if (owner._keys.PublicIdentity != captured.Native) throw new InvalidDataException("Ready native ownership changed during catalog reconciliation.");
                owner._keys.AssertPublicationIdentity(captured.Pin, captured.Authority);
                return new ProtocolSigningJournal(database, owner._budget).AssertRetainedCloudEnvelope(actor, epoch, document.Bytes.Span);
            }, token).ConfigureAwait(false);
            _signed.Add(intent.Id);
            if (intent.Kind is "library-publisher-authorization" or "library-source-configuration")
            {
                await new LocalControllerPolicyObserver(owner._state, owner._budget, owner._keys, context).ObserveAsync(document, token).ConfigureAwait(false);
                if (intent.Kind == "library-source-configuration")
                {
                    var configuration = DecodeSources(document.Bytes.Span);
                    _sourcePolicies.TryAdd(configuration.Revision, Reference(observation));
                }
            }
            else if (intent.Kind == "catalog-commit" && intent.ActorId == captured.Native.PublisherId && intent.Epoch == captured.Native.PublisherEpoch)
            {
                var verified = new SignedProtocolDocuments(Capabilities).VerifyAndRead(document.Bytes.Span, Publisher());
                var commit = new CatalogPublicationReader(new(context.AccountId, context.FolderId)).ReadCommit(verified);
                _heads.Add(new(Reference(observation), verified.Sequence, commit.GenerationId));
            }
            else throw new SigningHistoryRecoveryRequiredException();
        }

        public async Task VerifyHeadAsync(CancellationToken token)
        {
            foreach (var intent in captured.Signing.Intents)
            {
                if (_signed.Contains(intent.Id)) continue;
                if (intent.EnvelopeSha256 is null) { _pendingSigning.Add(intent.Id); continue; }
                if (captured.Recovery is { } recovery && recovery.IntentId == intent.Id && recovery.BodySha256 == intent.BodySha256 &&
                    recovery.EnvelopeSha256 == intent.EnvelopeSha256 && recovery.EnvelopeBytes == intent.EnvelopeBytes)
                { _pendingSigning.Add(intent.Id); continue; }
                var role = intent.Kind switch { "catalog-commit" => "catalog-commit", "library-publisher-authorization" => "publisher-authorization", "library-source-configuration" => "source-configuration", _ => throw new ProtocolSigningConflictException() };
                if (!publications.Objects.Any(item => item.Sha256 == intent.EnvelopeSha256 && item.Bytes == intent.EnvelopeBytes && item.Role == role &&
                    item.MimeType == "application/json" && item.VerifiedVersion is null)) throw new SigningHistoryRecoveryRequiredException();
            }
            _head = _heads.OrderByDescending(item => item.Sequence).ThenBy(item => item.Reference.FileId, StringComparer.Ordinal).FirstOrDefault();
            if (_head is null) return;
            var visitedSequences = new HashSet<long>(); VerifiedProtocolDocument? headDocument = null;
            await foreach (var entry in new CatalogCommitHistoryTraversal(new(context.AccountId, context.FolderId), Publisher(), this)
                .ReadAsync(_head.Reference, token).ConfigureAwait(false))
            {
                await owner._state.ExecuteAsync(database =>
                {
                    owner.Check(token); _ = new ProtocolSigningJournal(database, owner._budget).AssertRetainedCloudEnvelope(captured.Native.PublisherId,
                        captured.Native.PublisherEpoch, entry.Envelope.Span); return true;
                }, token).ConfigureAwait(false);
                visitedSequences.Add(entry.Verified.Sequence);
                if (entry.Verified.Sequence == _head.Sequence) headDocument = entry.Verified;
            }
            if (_heads.Any(item => !visitedSequences.Contains(item.Sequence))) throw new SigningHistoryRecoveryRequiredException();
            // Reject incomplete ancestry before decoding the potentially large facts
            // graph. Retain only this one bounded verified head payload while walking.
            if (headDocument is null) throw new InvalidDataException("Complete ancestry lost its verified head.");
            await foreach (var leaf in new CatalogFactsTraversal(context.LibraryId, new(context.AccountId, context.FolderId), this,
                WorkspaceObservationRepository.MaximumDocuments).ReadCoveredLeavesAsync(headDocument, ValidateCoverageAsync, token).ConfigureAwait(false))
                _assets = checked(_assets + leaf.Facts.Assets.Count);
        }

        private async Task ValidateCoverageAsync(CatalogSourceCoverageSet coverage, CancellationToken token)
        {
            if (!_sourcePolicies.TryGetValue(coverage.Root.ConfigurationRevision, out var reference)) throw new SigningHistoryRecoveryRequiredException();
            var bytes = await ReadAsync(reference, token).ConfigureAwait(false); DecodeSources(bytes.Span).ValidateCoverage(coverage);
        }
        private CatalogSourceConfiguration DecodeSources(ReadOnlySpan<byte> envelope)
        {
            var authority = new TrustedProtocolActor(context.LibraryId, captured.Pin.ActorId, captured.Pin.Epoch,
                Convert.FromBase64String(captured.Pin.PublicKeySpki), ["library-source-configuration"]);
            var verified = new SignedProtocolDocuments(Capabilities).VerifyAndRead(envelope, authority);
            return new CatalogPublicationReader(new(context.AccountId, context.FolderId)).ReadSourceConfiguration(verified, context.LibraryId);
        }
        private static (Guid Actor, long Epoch)? Route(ReadOnlyMemory<byte> bytes)
        {
            try
            {
                using var json = JsonDocument.Parse(bytes);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ProtocolFormatException("Metadata JSON must be an object.");
                if (!json.RootElement.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "mpa-signed-document") return null;
                var body = json.RootElement.GetProperty("document");
                if (!Guid.TryParseExact(body.GetProperty("actorId").GetString(), "D", out var actor) ||
                    !long.TryParse(body.GetProperty("epoch").GetString(), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var epoch)) throw new ProtocolFormatException("Signed metadata has an invalid actor hint.");
                return (actor, epoch);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new ProtocolFormatException("Untrusted metadata has a malformed envelope routing header.", error); }
        }
        private TrustedProtocolActor Publisher() => new(context.LibraryId, captured.Native.PublisherId, captured.Native.PublisherEpoch,
            Convert.FromBase64String(captured.Native.PublisherPublicKeySpki), ["catalog-commit"]);
        private CatalogObjectReference Reference(WorkspaceDocumentObservation observation) => new(context.AccountId, observation.FileId,
            context.FolderId, observation.Bytes, observation.Sha256);

        public async Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference, CancellationToken cancellationToken)
        {
            owner.Check(cancellationToken);
            if (reference.AccountId != context.AccountId || reference.WorkspaceId != context.FolderId ||
                !_observed.TryGetValue(reference.FileId, out var expected) || expected.Bytes != reference.Bytes || expected.Sha256 != reference.Sha256)
                throw new WorkspaceObservationConflictException();
            var document = await source.ReadAsync(new(expected.FileId, expected.Version, expected.Bytes, expected.MimeType ?? "application/json"), cancellationToken).ConfigureAwait(false);
            owner.Check(cancellationToken);
            var actual = new WorkspaceDocumentObservation(document.FileId, document.DriveVersion, document.Bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(document.Bytes.Span)), expected.MimeType);
            await owner._state.ExecuteAsync(database => new WorkspaceObservationRepository(database, owner._budget).RememberRead(context, actual), cancellationToken).ConfigureAwait(false);
            owner.Check(cancellationToken); if (actual != expected) throw new WorkspaceObservationConflictException(); return document.Bytes;
        }
        public ReconciledLocalCatalog Result(WorkspaceInventoryCheckpoint checkpoint) => new(captured.Authority, checkpoint, _head?.Reference,
            _head?.Sequence ?? 0, _assets, _pendingSigning.AsReadOnly(), publications.Objects.Where(item => item.VerifiedVersion is null).Select(item => item.Id).ToList().AsReadOnly());
        private sealed record Head(CatalogObjectReference Reference, long Sequence, Guid Generation);
    }

    private void Check(CancellationToken token)
    { lock (_gate) if (_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Catalog reconciliation retired before result admission.", token); }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true; _current = null; _completedRead = null;
            if (_disposal is { IsFaulted: true } && !_exitConfirmed) _disposal = null;
            return new(_disposal ??= StopAsync());
        }
    }
    private async Task StopAsync()
    {
        Exception? cancellationFailure = null;
        try { await _stop.CancelAsync().ConfigureAwait(false); } catch (Exception error) { cancellationFailure = error; }
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cancellationFailure is not null) throw new AggregateException("Catalog reconciliation cancellation needs attention; retain borrowed owners.", cancellationFailure);
            lock (_gate) { _localAccess?.Context.Dispose(); _localAccess = null; }
            _stop.Dispose(); lock (_gate) _exitConfirmed = true;
        }
        finally { _serial.Release(); }
    }
}
