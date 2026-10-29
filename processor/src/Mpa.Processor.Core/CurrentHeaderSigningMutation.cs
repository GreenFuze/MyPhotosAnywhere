using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CurrentControlTransitionJournal
{
    // Called only inside the SAME transaction that checked before against actual
    // native state, then reserved the exact original capture's plan-derived body.
    // Returned actual state is a local postcondition, never a refreshed scan grant.
    internal CurrentControlSigningInventory AssertOnlyCurrentHeaderReservationChangedIn(
        CurrentControlSigningInventory before,LocalCurrentControlAttempt fenced,
        ProtocolSigningIntent reserved,SqliteTransaction transaction,CancellationToken token)
    {
        if(reserved.Kind!="catalog-commit" || reserved.Envelope is not null ||
            reserved.EnvelopeSha256 is not null || reserved.Sequence<1 ||
            before.Signing.Intents.Any(value=>value.Id==reserved.Id))
            throw new ProtocolSigningConflictException();
        var actor=before.Signing.Actors.SingleOrDefault(value=>value.ActorId==reserved.ActorId && value.Epoch==reserved.Epoch)
            ??throw new ProtocolSigningConflictException();
        if(actor.Role!="publisher" || actor.Sequence==long.MaxValue || reserved.Sequence!=actor.Sequence+1)
            throw new ProtocolSigningConflictException();
        var expectedActors=before.Signing.Actors.Select(value=>value==actor?value with{Sequence=reserved.Sequence}:value).ToArray();
        var actual=HeaderSigningMutationIn(before,fenced,transaction,token);
        var expected=new ProtocolSigningIntentExpectation(reserved.Id,reserved.ActorId,reserved.Epoch,
            reserved.Sequence,reserved.Kind,reserved.Body.Length,reserved.BodySha256,null,null);
        if(!actual.Signing.Actors.SequenceEqual(expectedActors) || actual.Signing.Intents.Count!=before.Signing.Intents.Count+1 ||
            actual.Signing.Intents.SingleOrDefault(value=>value.Id==reserved.Id)!=expected ||
            !actual.Signing.Intents.Where(value=>value.Id!=reserved.Id).SequenceEqual(before.Signing.Intents))
            throw new ProtocolSigningInventoryChangedException();
        token.ThrowIfCancellationRequested();return actual;
    }

    // The exact native signature/envelope delta must not consume another counter,
    // change another intent, replace old signed bytes or mutate any Root terminal.
    internal CurrentControlSigningInventory AssertOnlyCurrentHeaderEnvelopeChangedIn(
        CurrentControlSigningInventory before,LocalCurrentControlAttempt fenced,
        ProtocolSigningIntent sealedIntent,SqliteTransaction transaction,CancellationToken token)
    {
        var original=before.Signing.Intents.SingleOrDefault(value=>value.Id==sealedIntent.Id)
            ??throw new ProtocolSigningConflictException();
        if(original.Kind!="catalog-commit" || original.EnvelopeBytes is not null || original.EnvelopeSha256 is not null ||
            sealedIntent.Envelope is not {} envelope || sealedIntent.EnvelopeSha256 is null ||
            original.ActorId!=sealedIntent.ActorId || original.Epoch!=sealedIntent.Epoch ||
            original.Sequence!=sealedIntent.Sequence || original.Kind!=sealedIntent.Kind ||
            original.BodyBytes!=sealedIntent.Body.Length || original.BodySha256!=sealedIntent.BodySha256)
            throw new ProtocolSigningConflictException();
        var expected=before.Signing.Intents.Select(value=>value.Id==original.Id?
            value with{EnvelopeBytes=envelope.Length,EnvelopeSha256=sealedIntent.EnvelopeSha256}:value).ToArray();
        var actual=HeaderSigningMutationIn(before,fenced,transaction,token);
        if(!actual.Signing.Actors.SequenceEqual(before.Signing.Actors) || !actual.Signing.Intents.SequenceEqual(expected))
            throw new ProtocolSigningInventoryChangedException();
        token.ThrowIfCancellationRequested();return actual;
    }

    private CurrentControlSigningInventory HeaderSigningMutationIn(CurrentControlSigningInventory before,
        LocalCurrentControlAttempt fenced,SqliteTransaction transaction,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(before);ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(transaction);
        if(!ReferenceEquals(transaction.Connection,_database.Connection))
            throw new InvalidOperationException("Native header postconditions require their owning transaction.");
        var actual=CaptureSigningInventoryIn(fenced,transaction,token);
        if(actual.ConnectionCode!=before.ConnectionCode || actual.AttemptId!=before.AttemptId ||
            actual.AccountRevision!=before.AccountRevision || actual.RootSha256!=before.RootSha256 ||
            actual.Signing.LibraryId!=before.Signing.LibraryId || actual.Roots.Count!=before.Roots.Count)
            throw new CurrentControlSigningInventoryChangedException();
        var codec=new CanonicalJson(32768);
        for(var index=0;index<actual.Roots.Count;index++)
        {
            token.ThrowIfCancellationRequested();
            if(!codec.Encode(JsonSerializer.SerializeToUtf8Bytes(actual.Roots[index],Json)).AsSpan()
                .SequenceEqual(codec.Encode(JsonSerializer.SerializeToUtf8Bytes(before.Roots[index],Json))))
                throw new CurrentControlSigningInventoryChangedException();
        }
        token.ThrowIfCancellationRequested();return actual;
    }
}
