using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCatalogReconciliationService
{
    // Called only under this owner's serial operation and actual private current proof.
    // A prior receipt/hash alone cannot select or return a reference. This path is
    // read-only: no old scope is reopened for upload and no pending ID is retargeted.
    private async Task<CatalogObjectReference?> ReadReusableDependencyAsync(CompletedRead proof, CapturedCatalogPublication generation,
        WorkspacePublicationExpectation expected, CancellationToken token)
    {
        var candidate = await _state.ExecuteAsync(database =>
        {
            var actual = AssertGeneration(database, proof, expected.Scope, token);
            if (actual != generation) throw new CatalogGenerationCaptureConflictException();
            new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
            // Even an unallocated original row is immutable pending work. Resume it
            // rather than substituting another generation's equal bytes/file.
            if (proof.Publications.Objects.Any(item => item.Id == expected.Id)) return null;
            var references = new CatalogDependencyReferenceRepository(database, _budget);
            var retained = references.Read(expected.Id);
            if (retained is not null)
            {
                if (retained.Scope != expected.Scope || retained.Role != expected.Role || retained.MimeType != expected.MimeType ||
                    retained.Bytes != expected.Bytes || retained.Sha256 != expected.Sha256) throw new WorkspacePublicationConflictException();
                // Missing originals fail closed. Never select an equal replacement.
                return proof.Publications.Objects.Single(item => item.Id == retained.OriginalPublicationId);
            }
            foreach (var item in proof.Publications.Objects.Where(item => item.Scope.GenerationId != expected.Scope.GenerationId &&
                item.Scope.LibraryId == expected.Scope.LibraryId && item.Scope.ActorId == expected.Scope.ActorId && item.Scope.ActorEpoch == expected.Scope.ActorEpoch &&
                item.Scope.AccountId == expected.Scope.AccountId && item.Scope.FolderId == expected.Scope.FolderId && item.Role == expected.Role &&
                item.MimeType == expected.MimeType && item.Bytes == expected.Bytes && item.Sha256 == expected.Sha256 &&
                item.FileId is not null && item.VerifiedVersion is not null).OrderBy(item => item.FileId, StringComparer.Ordinal))
            {
                var prior = new CatalogGenerationCaptureRepository(database, _budget).Publication(item.Scope.GenerationId);
                if (prior?.Capture.Scope != item.Scope || prior.Capture.Phase is not ("complete" or "snapshot-retiring" or "retired")) continue;
                var observation = proof.Known.SingleOrDefault(known => known.FileId == item.FileId);
                if (observation is not null && observation.Version >= item.VerifiedVersion && observation.Bytes == item.Bytes &&
                    observation.Sha256 == item.Sha256 && (observation.MimeType ?? "application/json") == item.MimeType)
                {
                    _ = references.Record(expected.Id, expected.Scope, item.Id, proof.Result.Observation, proof.AttemptId, LocalAccessForScope(expected.Scope));
                    return item;
                }
            }
            return null;
        }, token).ConfigureAwait(false);
        if (candidate is null) return null;
        var reference = new CatalogObjectReference(candidate.Scope.AccountId, candidate.FileId!, candidate.Scope.FolderId, candidate.Bytes, candidate.Sha256);
        // Actual joined read checks present membership/type/size/version and exact
        // bytes under the CURRENT credential revision, not the old receipt's revision.
        var bytes = await new CatalogWorkspaceObjectSource(_state, _accounts, new(reference.AccountId, reference.WorkspaceId),
            proof.Result.Authority.AccountRevision).ReadAsync(reference, token).ConfigureAwait(false);
        if (bytes.Length != expected.Bytes || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.Span), Convert.FromHexString(expected.Sha256)))
            throw new WorkspaceObservationConflictException();
        await _state.ExecuteAsync(database =>
        {
            if (AssertGeneration(database, proof, expected.Scope, token) != generation) throw new CatalogGenerationCaptureConflictException();
            new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
            var selected = new CatalogDependencyReferenceRepository(database, _budget).Read(expected.Id);
            if (selected?.OriginalPublicationId != candidate.Id || selected.FileId != reference.FileId) throw new WorkspacePublicationConflictException();
            return true;
        }, token).ConfigureAwait(false);
        Check(token); return reference;
    }
}
