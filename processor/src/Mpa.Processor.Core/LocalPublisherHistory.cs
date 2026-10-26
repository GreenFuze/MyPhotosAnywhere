using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>One bounded observed history walk. Retired identities and key/epoch
/// rewinds cannot be revived by another newly signed controller policy.</summary>
internal sealed class LocalPublisherHistory
{
    private readonly HashSet<(Guid,long)> _retired=[];
    private readonly Dictionary<Guid,long> _epochs=[];
    private readonly Dictionary<(Guid,long),string> _keys=[];
    private (Guid Actor,long Epoch,string Key)? _previous;
    internal void Observe(CatalogPublisherAuthorization policy)
    {
        (Guid Actor,long Epoch,string Key)? next=policy.Publisher is { } actor ? (actor.ActorId,actor.Epoch,policy.PublisherKeyId!) : null;
        if(_previous is { } old && _previous!=next) _retired.Add((old.Actor,old.Epoch));
        if(next is { } current)
        {
            if(_retired.Contains((current.Actor,current.Epoch)) || _epochs.TryGetValue(current.Actor,out var high) && current.Epoch<high ||
                _keys.TryGetValue((current.Actor,current.Epoch),out var key) && key!=current.Key) throw new ProtocolSigningConflictException();
            _epochs[current.Actor]=current.Epoch;_keys[(current.Actor,current.Epoch)]=current.Key;
        }
        _previous=next;
    }
}
