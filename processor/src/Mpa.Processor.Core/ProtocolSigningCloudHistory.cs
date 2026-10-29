using System.Globalization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed class SigningHistoryRecoveryRequiredException() : InvalidOperationException("Drive contains signed work missing from this installation's complete signing history. Pause publication and recover the retained history or explicitly enroll a new authorized epoch; never reset or advance a counter from Drive alone.");

public sealed partial class ProtocolSigningJournal
{
    /// <summary>Read-only restore fence for one cloud envelope, verified against the
    /// already registered local actor. The caller must supply the complete reconciled
    /// cloud history and independently prove current native-key/policy ownership. A
    /// subset or empty inventory is never a complete publication admission proof.</summary>
    public ProtocolSigningIntent AssertRetainedCloudEnvelope(Guid actorId, long epoch, ReadOnlySpan<byte> signedEnvelope)
    {
        CheckId(actorId);ArgumentOutOfRangeException.ThrowIfLessThan(epoch,1L);
        if (signedEnvelope.Length is <1 or >ProtocolDocumentReader.MaximumDocumentBytes) throw new ArgumentException("A bounded signed cloud envelope is required.",nameof(signedEnvelope));
        using var transaction = _database.Connection.BeginTransaction();
        var retained=AssertRetainedCloudEnvelope(actorId,epoch,signedEnvelope,transaction);transaction.Commit();return retained;
    }

    internal ProtocolSigningIntent AssertRetainedCloudEnvelope(Guid actorId,long epoch,ReadOnlySpan<byte> signedEnvelope,SqliteTransaction transaction)
    {
        CheckId(actorId); ArgumentOutOfRangeException.ThrowIfLessThan(epoch, 1L);
        if (signedEnvelope.Length is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes) throw new ArgumentException("A bounded signed cloud envelope is required.", nameof(signedEnvelope));
        var actor = Actor(actorId, epoch, transaction);
        var verified = new SignedProtocolDocuments(Capabilities).VerifyAndRead(signedEnvelope, Authority(actor));
        // A restored old database may have a perfectly consistent *shorter* journal.
        // Only a verified cloud signature can establish this recovery requirement;
        // neither untrusted sequence text nor a remote name/properties can do so.
        if (verified.Sequence > actor.Sequence) throw new SigningHistoryRecoveryRequiredException();
        Guid id;
        using (var command = Command(transaction, "SELECT id FROM signing_intent WHERE actor_id=$actor AND epoch=$epoch AND sequence=$sequence",
            ("$actor", actorId.ToString("D")), ("$epoch", epoch), ("$sequence", verified.Sequence)))
        {
            var value = command.ExecuteScalar() as string;
            if (value is null) throw new InvalidDataException("The complete signing journal lost an observed sequence " + verified.Sequence.ToString(CultureInfo.InvariantCulture) + ".");
            id = ParseId(value);
        }
        var retained = Read(id, transaction) ?? throw new InvalidDataException("The observed signing intent disappeared.");
        if (retained.Envelope is null) throw new SigningHistoryRecoveryRequiredException();
        if (retained.Kind != verified.Kind || !retained.Envelope.Value.Span.SequenceEqual(signedEnvelope)) throw new ProtocolSigningConflictException();
        return retained;
    }
}
