using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mpa.Processor.Protocol;

public sealed record ProtocolSignatureVerificationStatistics(int Entries,int ActiveScopes,long NativeAttempts,long Hits);

/// <summary>Bounded owned mathematical evidence for exact successful ES256
/// verification. Retains only framed SHA-256 digests; no documents, signatures,
/// native keys, actor enrollment, counters, permissions or freshness. Callers must
/// independently validate every actual domain/history/current-state input.
/// Scope entry is explicit and disposal requires every scope to have joined.</summary>
public sealed class ProtocolSignatureVerificationOwner : IDisposable
{
    private const int MaximumCapacity=1024;
    private static readonly AsyncLocal<Scope?> Current=new();
    private readonly object _gate=new();
    private readonly int _capacity;
    private readonly HashSet<string> _positive=new(StringComparer.Ordinal);
    private readonly Queue<string> _order=new();
    private int _activeScopes;
    private long _nativeAttempts,_hits;
    private bool _disposed;
    public ProtocolSignatureVerificationOwner(int capacity=MaximumCapacity)
    {if(capacity is <1 or >MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));_capacity=capacity;}
    public ProtocolSignatureVerificationStatistics Statistics
    {get {lock(_gate) {AssertBound();return new(_positive.Count,_activeScopes,_nativeAttempts,_hits);}}}
    public IDisposable EnterScope()
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);AssertBound();
            var scope=new Scope(this,Current.Value);_activeScopes=checked(_activeScopes+1);Current.Value=scope;return scope;
        }
    }
    internal static Scope? Capture()=>Current.Value is {IsActive:true} scope?scope:null;
    public void Dispose()
    {
        lock(_gate)
        {
            if(_disposed) return;
            if(_activeScopes!=0) throw new InvalidOperationException("Join every actual mathematical-verification scope before retiring its owner.");
            AssertBound();_positive.Clear();_order.Clear();_disposed=true;
        }
    }
    private void AssertBound()
    {if(_positive.Count!=_order.Count || _positive.Count>_capacity || _activeScopes<0) throw new InvalidOperationException("Mathematical verification evidence exceeded its exact owned bound.");}
    internal sealed class Scope(ProtocolSignatureVerificationOwner owner,Scope? previous) : IDisposable
    {
        private int _active=1;
        internal bool IsActive=>Volatile.Read(ref _active)==1;
        internal bool Contains(string key)
        {
            lock(owner._gate)
            {
                owner.AssertBound();if(!IsActive || owner._disposed || !owner._positive.Contains(key)) return false;
                owner._hits=checked(owner._hits+1);return true;
            }
        }
        internal void NativeAttempt()
        {lock(owner._gate) {if(IsActive && !owner._disposed) owner._nativeAttempts=checked(owner._nativeAttempts+1);}}
        internal void Remember(string key)
        {
            lock(owner._gate)
            {
                owner.AssertBound();if(!IsActive || owner._disposed || owner._positive.Contains(key)) return;
                if(owner._positive.Count==owner._capacity)
                    if(!owner._positive.Remove(owner._order.Dequeue())) throw new InvalidOperationException("Mathematical evidence lost its bounded identity.");
                if(!owner._positive.Add(key)) throw new InvalidOperationException("Mathematical evidence changed during its owned insertion.");
                owner._order.Enqueue(key);owner.AssertBound();
            }
        }
        public void Dispose()
        {
            lock(owner._gate)
            {
                if(!IsActive) return;
                if(!ReferenceEquals(Current.Value,this)) throw new InvalidOperationException("Verification scopes must retire in their actual nested execution context.");
                Current.Value=previous;Volatile.Write(ref _active,0);owner._activeScopes--;owner.AssertBound();
            }
        }
    }
    internal static string Fingerprint(string actualKeyId,ReadOnlySpan<byte> canonical,ReadOnlySpan<byte> signature)
    {
        // Exact actual canonical key identity, canonical bytes and decoded P1363
        // signature. Explicit lengths prevent ambiguous concatenations. Algorithm
        // and canonical input/encoding limits are checked before this call.
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);Span<byte> length=stackalloc byte[4];
        var key=Encoding.ASCII.GetBytes(actualKeyId);BinaryPrimitives.WriteInt32BigEndian(length,key.Length);digest.AppendData(length);digest.AppendData(key);
        BinaryPrimitives.WriteInt32BigEndian(length,canonical.Length);digest.AppendData(length);digest.AppendData(canonical);
        BinaryPrimitives.WriteInt32BigEndian(length,signature.Length);digest.AppendData(length);digest.AppendData(signature);
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
}
