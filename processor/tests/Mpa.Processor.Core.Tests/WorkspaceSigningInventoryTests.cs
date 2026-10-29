using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class ProtocolSigningJournalTests
{
    [TestMethod]
    public void SigningDuringAScanCannotCompleteItsCheckpointAndTheNextScanRetainsTheIntent()
    {
        using var fixture = new Fixture(); var pin = fixture.Bootstrap(); var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var context = new WorkspaceObservationContext(pin.LibraryId, pin.AccountId, pin.FolderId, 1, null);
        var attempt = observations.BeginScan(context); Assert.AreEqual(fixture.Journal.Inventory(pin.LibraryId).Sha256, attempt.SigningInventorySha256);
        var pending = fixture.Reserve(Guid.NewGuid());
        Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(() => observations.CompleteScan(attempt, "current", []));
        Assert.AreEqual(attempt, observations.LastAttempt()); Assert.IsNull(observations.LastCheckpoint()); Assert.IsNull(fixture.Journal.Read(pending.Id)!.Envelope);
        var completed = observations.CompleteScan(observations.BeginScan(context), "next", []); observations.AssertCompletedScan(completed);
        Assert.AreEqual(pending.Id, fixture.Journal.Inventory(pin.LibraryId).Intents.Single().Id);
    }

    [TestMethod]
    public void LaterSealingInvalidatesCurrentAdmissionButDoesNotDestroyHistoricalCheckpointEvidence()
    {
        using var fixture = new Fixture(); var pin = fixture.Bootstrap(); var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var context = new WorkspaceObservationContext(pin.LibraryId, pin.AccountId, pin.FolderId, 1, null); var pending = fixture.Reserve(Guid.NewGuid());
        var completed = observations.CompleteScan(observations.BeginScan(context), "before-seal", []);
        fixture.Journal.Seal(pending.Id, pending.BodySha256, new Mpa.Processor.Protocol.ProtocolDocumentSigner(fixture.Controller).Sign(pending.Body.Span));
        Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(() => observations.AssertCompletedScan(completed));
        Assert.AreEqual(completed, observations.LastCheckpoint()); Assert.AreEqual(completed, observations.LastAttempt()!.Completed);
        var next = observations.CompleteScan(observations.BeginScan(context), "after-seal", []); observations.AssertCompletedScan(next);
        Assert.IsNotNull(fixture.Journal.Read(pending.Id)!.Envelope);
    }

    [TestMethod]
    public void SigningChangedInsideCompletionRollsBackTheWholeCheckpointAndRetainsTheUnsealedRetry()
    {
        using var fixture = new Fixture(); var pin = fixture.Bootstrap(); var pending = fixture.Reserve(Guid.NewGuid());
        var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var context = new WorkspaceObservationContext(pin.LibraryId, pin.AccountId, pin.FolderId, 1, null); var attempt = observations.BeginScan(context);
        var envelope = Envelope(fixture, pending.Body.ToArray()); var hash = Convert.ToHexStringLower(SHA256.HashData(envelope));
        fixture.Sql("CREATE TRIGGER seal_during_completion AFTER UPDATE ON processor_meta WHEN NEW.key='workspace_observation_attempt_v1:sha256' BEGIN UPDATE signing_intent SET envelope=X'" +
            Convert.ToHexString(envelope) + "',envelope_hash='" + hash + "' WHERE id='" + pending.Id.ToString("D") + "'; END");
        Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(() => observations.CompleteScan(attempt, "atomic", []));
        Assert.IsNull(observations.LastCheckpoint()); Assert.AreEqual(attempt, observations.LastAttempt()); Assert.IsNull(fixture.Journal.Read(pending.Id)!.Envelope);
        fixture.Sql("DROP TRIGGER seal_during_completion"); var completed = observations.CompleteScan(attempt, "atomic", []); observations.AssertCompletedScan(completed);
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void LegacyAttemptBytesRemainReadableAndTheNextActualScanCapturesSigningEvidence()
    {
        using var fixture = new Fixture(); var pin = fixture.Bootstrap(); var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var context = new WorkspaceObservationContext(pin.LibraryId, pin.AccountId, pin.FolderId, 1, null); var current = observations.BeginScan(context);
        var legacy = current with { SigningInventorySha256 = null };
        var bytes = new Mpa.Processor.Protocol.CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(legacy));
        Assert.IsFalse(Encoding.UTF8.GetString(bytes).Contains("SigningInventorySha256", StringComparison.Ordinal));
        using (var connection = Open(fixture.Database.StateDirectory))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE processor_meta SET value=CASE key WHEN 'workspace_observation_attempt_v1' THEN $text ELSE $hash END WHERE key IN ('workspace_observation_attempt_v1','workspace_observation_attempt_v1:sha256')";
            command.Parameters.AddWithValue("$text", Encoding.UTF8.GetString(bytes)); command.Parameters.AddWithValue("$hash", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.AreEqual(2, command.ExecuteNonQuery());
        }
        Assert.AreEqual(legacy, observations.LastAttempt());
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => observations.CompleteScan(legacy, "legacy-cannot-grant", []));
        Assert.IsNull(observations.LastCheckpoint()); Assert.AreEqual(legacy, observations.LastAttempt());
        var next = observations.BeginScan(context);
        Assert.AreEqual(fixture.Journal.Inventory(pin.LibraryId).Sha256, next.SigningInventorySha256); Assert.AreEqual(current.Revision + 1, next.Revision);
    }
}
