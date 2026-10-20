using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Mpa.Processor.Web;

public sealed class BrowserInvitation(Guid id, string secret, Uri endpoint)
{
    public Guid Id { get; } = id;
    [JsonIgnore] public string Secret { get; } = secret;
    [JsonIgnore] public Uri Endpoint { get; } = endpoint;
    [JsonIgnore] public Uri OpenLink => new(Endpoint, "#connect=" + Secret);
    public override string ToString() => "[single-use browser invitation]";
}

public sealed class BrowserSessionGrant(Guid id, string cookie, string csrf)
{
    public Guid Id { get; } = id;
    [JsonIgnore] public string Cookie { get; } = cookie;
    [JsonIgnore] public string Csrf { get; } = csrf;
    public override string ToString() => "[authenticated browser grant]";
}

public sealed record BrowserSession(Guid Id, bool HostOwner, [property: JsonIgnore] string Csrf)
{
    public override string ToString() => "[authenticated browser session]";
}
public sealed class BrowserAuthenticationException() : InvalidOperationException("Browser access expired or was not approved. Open MPA from the host computer to connect again.");

/// <summary>One engine-lifetime owner, independent of browser tabs. Only token digests are
/// retained. Invites are single-use, endpoint-bound and monotonic-time limited. No Google data.</summary>
public sealed class BrowserSessionAuthority(TimeProvider? time = null) : IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Invitation> _invitations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;
    private long? _attemptWindow;
    private int _attempts;

    public BrowserInvitation Invite(Uri endpoint, bool hostOwner)
    {
        ValidateEndpoint(endpoint);
        if (hostOwner && !endpoint.IsLoopback) throw new ArgumentException("A host-owner invitation is restricted to loopback.", nameof(hostOwner));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Prune();
            if (_invitations.Count >= 4) throw new InvalidOperationException("Finish or cancel an existing browser invitation first.");
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); var id = Guid.NewGuid();
            _invitations.Add(Digest(secret), new(id, endpoint.GetLeftPart(UriPartial.Authority), hostOwner, _time.GetTimestamp()));
            return new(id, secret, endpoint);
        }
    }

    public BrowserSessionGrant Exchange(string secret, Uri endpoint)
    {
        ValidateEndpoint(endpoint);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Prune();
            var now = _time.GetTimestamp();
            if (_attemptWindow is null || _time.GetElapsedTime(_attemptWindow.Value, now) >= TimeSpan.FromSeconds(10))
            { _attemptWindow = now; _attempts = 0; }
            if (++_attempts > 16 || !Token(secret)) throw new BrowserAuthenticationException();
            var digest = Digest(secret);
            if (!_invitations.TryGetValue(digest, out var invitation) || invitation.Endpoint != endpoint.GetLeftPart(UriPartial.Authority))
                throw new BrowserAuthenticationException();
            if (_sessions.Count >= 32) throw new InvalidOperationException("Maximum connected browser count reached. Disconnect an existing session first.");
            var cookie = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var csrf = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); var id = Guid.NewGuid();
            _sessions.Add(Digest(cookie), new(id, invitation.Endpoint, invitation.HostOwner, csrf, now, now));
            _invitations.Remove(digest);
            return new(id, cookie, csrf);
        }
    }

    public BrowserSession Authenticate(string? cookie, Uri endpoint, string? csrf, bool mutation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Prune();
            if (!Token(cookie) || !_sessions.TryGetValue(Digest(cookie!), out var session) ||
                session.Endpoint != endpoint.GetLeftPart(UriPartial.Authority) || mutation && !EqualToken(csrf, session.Csrf))
                throw new BrowserAuthenticationException();
            _sessions[Digest(cookie!)] = session with { Touched = _time.GetTimestamp() };
            return new(session.Id, session.HostOwner, session.Csrf);
        }
    }

    public void Revoke(Guid id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var entry in _sessions.Where(entry => entry.Value.Id == id).ToArray()) _sessions.Remove(entry.Key);
            foreach (var entry in _invitations.Where(entry => entry.Value.Id == id).ToArray()) _invitations.Remove(entry.Key);
        }
    }
    private void Prune()
    {
        var now = _time.GetTimestamp();
        foreach (var entry in _invitations.Where(entry => _time.GetElapsedTime(entry.Value.Created, now) >= TimeSpan.FromMinutes(2)).ToArray()) _invitations.Remove(entry.Key);
        foreach (var entry in _sessions.Where(entry => _time.GetElapsedTime(entry.Value.Created, now) >= TimeSpan.FromHours(8) ||
            _time.GetElapsedTime(entry.Value.Touched, now) >= TimeSpan.FromMinutes(30)).ToArray()) _sessions.Remove(entry.Key);
    }
    private static bool Token(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool EqualToken(string? first, string second) => Token(first) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(first!), Encoding.ASCII.GetBytes(second));
    private static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    internal static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            throw new ArgumentException("Browser access requires HTTPS, or the local loopback endpoint.", nameof(endpoint));
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _invitations.Clear(); _sessions.Clear(); }
    }
    private sealed record Invitation(Guid Id, string Endpoint, bool HostOwner, long Created);
    private sealed record Session(Guid Id, string Endpoint, bool HostOwner, string Csrf, long Created, long Touched);
}
