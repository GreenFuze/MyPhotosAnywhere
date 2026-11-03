using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void BinaryObservationsKeepTheirEncodingAndCannotRewriteJsonOrAdoptAnUnsupportedRole()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var candidate = new WorkspaceCandidateObservation("facts", 1, 262145, "application/gzip");
        var attempt = repository.RememberCandidates(repository.BeginScan(context), [candidate]);
        var document = new WorkspaceDocumentObservation(candidate.FileId, 1, candidate.Bytes, new string('a', 64), candidate.MimeType);
        repository.RememberRead(context, document); var completed = repository.CompleteScan(attempt, "actual", [document]); repository.AssertCompletedScan(completed);
        Assert.AreEqual("application/gzip", repository.Known(context).Single().MimeType);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.RememberRead(context, document with { MimeType = "application/octet-stream" }));
        var next = repository.BeginScan(context);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.RememberCandidates(next, [candidate with { MimeType = "application/octet-stream" }]));
        Assert.ThrowsExactly<ArgumentException>(() => repository.RememberRead(context, document with { MimeType = null })); // JSON retains 256 KiB.
        Assert.ThrowsExactly<ArgumentException>(() => repository.RememberCandidates(next, [candidate with { Bytes = 4194305 }]));
        Assert.ThrowsExactly<ArgumentException>(() => repository.RememberCandidates(next, [candidate with { MimeType = "image/jpeg" }]));
        Assert.ThrowsExactly<ArgumentException>(() => repository.RememberRead(context, document with { MimeType = "image/jpeg" }));
        Assert.AreEqual(completed, repository.LastCheckpoint());
    }
}
