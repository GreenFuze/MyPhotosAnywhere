using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class ProtocolSigningJournal
{
    internal ReadOnlyMemory<byte> RetainedControllerReference(LibraryConnectionDescriptor pin,LibraryPolicyReference policy,string kind,SqliteTransaction transaction)
    {
        using var command=Command(transaction,"SELECT id FROM signing_intent WHERE actor_id=$actor AND epoch=$epoch AND sequence=$sequence",
            ("$actor",pin.ControllerId.ToString("D")),("$epoch",pin.ControllerEpoch),("$sequence",policy.Sequence));
        var raw=command.ExecuteScalar() as string;
        if(raw is null || !Guid.TryParseExact(raw,"D",out var id)) throw new SigningHistoryRecoveryRequiredException();
        var intent=Read(id,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
        if(intent.Kind!=kind || intent.Envelope is null || intent.Envelope.Value.Length!=policy.Reference.Bytes || intent.EnvelopeSha256!=policy.Reference.Sha256)
            throw new SigningHistoryRecoveryRequiredException();
        return intent.Envelope.Value;
    }
}
