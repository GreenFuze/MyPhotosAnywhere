using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Web.Tests;

[TestClass]
public sealed class BrowserSessionTests
{
    private static readonly Uri Local = new("http://127.0.0.1:12345/");
    private static readonly Uri Remote = new("https://mpa-synthetic.local:12346/");

    [TestMethod]
    public void ReopeningTheSameBrowserPreservesItsCsrfAndViewerOwnerButDoesNotReuseDifferentAccess()
    {
        using var access = new BrowserSessionAuthority(); var first = access.Exchange(access.Invite(Local, true).Secret, Local);
        var invitation = access.Invite(Local, true); var reopened = access.Exchange(invitation.Secret, Local, first.Cookie);
        Assert.AreEqual(first.Id, reopened.Id); Assert.AreEqual(first.Cookie, reopened.Cookie); Assert.AreEqual(first.Csrf, reopened.Csrf);
        Assert.AreEqual(first.Id, access.Authenticate(first.Cookie, Local, first.Csrf, true).Id);
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Exchange(invitation.Secret, Local, first.Cookie));
        var differentAccess = access.Exchange(access.Invite(Local, false).Secret, Local, first.Cookie);
        Assert.AreNotEqual(first.Id, differentAccess.Id); Assert.IsFalse(access.Authenticate(differentAccess.Cookie, Local, differentAccess.Csrf, true).HostOwner);
        var remote = access.Exchange(access.Invite(Remote, false).Secret, Remote, first.Cookie); Assert.AreNotEqual(first.Id, remote.Id);
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Authenticate(first.Cookie, Remote, first.Csrf, false));
    }

    [TestMethod]
    public void BrowserReopenCannotExtendItsOriginalAbsoluteExpiryOrReviveAnExpiredCookie()
    {
        var clock = new Clock(); using var access = new BrowserSessionAuthority(clock); var first = access.Exchange(access.Invite(Local, true).Secret, Local);
        for (var step = 0; step < 47; step++)
        {
            clock.Advance(TimeSpan.FromMinutes(10)); var reopened = access.Exchange(access.Invite(Local, true).Secret, Local, first.Cookie); Assert.AreEqual(first.Id, reopened.Id);
        }
        clock.Advance(TimeSpan.FromMinutes(10)); var fresh = access.Exchange(access.Invite(Local, true).Secret, Local, first.Cookie);
        Assert.AreNotEqual(first.Id, fresh.Id); Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Authenticate(first.Cookie, Local, null, false));
    }

    [TestMethod]
    public void SingleUseInviteBindsTheEndpointAndNeverSerializesItsCapability()
    {
        using var access = new BrowserSessionAuthority();
        var invitation = access.Invite(Local, true);
        Assert.IsFalse(JsonSerializer.Serialize(invitation).Contains(invitation.Secret, StringComparison.Ordinal));
        Assert.IsFalse(invitation.ToString().Contains(invitation.Secret, StringComparison.Ordinal));
        Assert.AreEqual("", invitation.OpenLink.Query); Assert.IsTrue(invitation.OpenLink.Fragment.Contains(invitation.Secret, StringComparison.Ordinal));
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Exchange(invitation.Secret, Remote));
        var grant = access.Exchange(invitation.Secret, Local);
        Assert.IsFalse(JsonSerializer.Serialize(grant).Contains(grant.Cookie, StringComparison.Ordinal));
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Exchange(invitation.Secret, Local));
        var session = access.Authenticate(grant.Cookie, Local, grant.Csrf, true);
        Assert.IsTrue(session.HostOwner); Assert.AreEqual(grant.Id, session.Id);
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Authenticate(grant.Cookie, Remote, grant.Csrf, false));
    }

    [TestMethod]
    public void MutationRequiresCsrfAndExplicitRevocationPreservesOtherBrowsers()
    {
        using var access = new BrowserSessionAuthority();
        var first = access.Exchange(access.Invite(Local, true).Secret, Local);
        var other = access.Exchange(access.Invite(Remote, false).Secret, Remote);
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Authenticate(first.Cookie, Local, null, true));
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Authenticate(first.Cookie, Local, other.Csrf, true));
        Assert.IsFalse(access.Authenticate(other.Cookie, Remote, other.Csrf, true).HostOwner);
        access.Revoke(first.Id);
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Authenticate(first.Cookie, Local, first.Csrf, false));
        Assert.AreEqual(other.Id, access.Authenticate(other.Cookie, Remote, other.Csrf, false).Id);
    }

    [TestMethod]
    public void MonotonicExpirySurvivesWallClockChangesAndIdleSessionCannotBeRevived()
    {
        var clock = new Clock(); using var access = new BrowserSessionAuthority(clock);
        var expired = access.Invite(Local, true); clock.Advance(TimeSpan.FromMinutes(2));
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Exchange(expired.Secret, Local));
        var grant = access.Exchange(access.Invite(Local, true).Secret, Local);
        clock.Advance(TimeSpan.FromMinutes(29)); access.Authenticate(grant.Cookie, Local, null, false);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Authenticate(grant.Cookie, Local, null, false));
    }

    [TestMethod]
    public void AbsoluteExpiryStillAppliesToAnActiveBrowserAndRestartInvalidatesSessions()
    {
        var clock = new Clock(); using var access = new BrowserSessionAuthority(clock);
        var grant = access.Exchange(access.Invite(Local, true).Secret, Local);
        for (var step = 0; step < 47; step++) { clock.Advance(TimeSpan.FromMinutes(10)); access.Authenticate(grant.Cookie, Local, null, false); }
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Authenticate(grant.Cookie, Local, null, false));
        using var reopened = new BrowserSessionAuthority(clock);
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => reopened.Authenticate(grant.Cookie, Local, null, false));
    }

    [TestMethod]
    public void UnauthenticatedGuessingAndInvitationCountsAreBoundedWithoutDeletingValidInvites()
    {
        var clock = new Clock(); using var access = new BrowserSessionAuthority(clock);
        var invitation = access.Invite(Local, true);
        for (var attempt = 0; attempt < 16; attempt++) Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Exchange(new string('0', 64), Local));
        Assert.ThrowsExactly<BrowserAuthenticationException>(() => access.Exchange(invitation.Secret, Local));
        clock.Advance(TimeSpan.FromSeconds(10)); access.Exchange(invitation.Secret, Local);
        for (var count = 0; count < 4; count++) access.Invite(Local, true);
        Assert.ThrowsExactly<InvalidOperationException>(() => access.Invite(Local, true));
        Assert.ThrowsExactly<ArgumentException>(() => access.Invite(new("http://192.168.1.50/"), false));
        Assert.ThrowsExactly<ArgumentException>(() => access.Invite(Remote, true));
        Assert.ThrowsExactly<ArgumentException>(() => access.Invite(new("https://name/path"), false));
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
