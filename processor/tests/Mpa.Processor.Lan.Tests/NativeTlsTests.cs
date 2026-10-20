using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mpa.Processor.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Lan.Tests;

[TestClass]
public sealed class NativeTlsTests
{
    [TestMethod]
    public async Task ActualMutualTlsUsesBothPinnedP256PrivateKeysAndRejectsAnUnrelatedPin()
    {
        await using var serverVault = new LanIdentityTests.Vault();
        await using var server = new LanIdentityOwner(serverVault, Guid.NewGuid(), Guid.NewGuid());
        await server.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=MPA synthetic phone", clientKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var clientCertificate = ProcessorLanIdentity.ForTransport(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2)));
        var clientPin = Convert.ToHexStringLower(SHA256.HashData(clientKey.ExportSubjectPublicKeyInfo()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
        using var outgoing = new TcpClient();
        var connect = outgoing.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using var accepted = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
        await connect.ConfigureAwait(false);
        using var serverTls = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false,
            (_, cert, _, errors) => TlsPeerPin.Matches(cert, clientPin, errors, DateTimeOffset.UtcNow));
        using var clientTls = new SslStream(outgoing.GetStream(), leaveInnerStreamOpen: false,
            (_, cert, _, errors) => TlsPeerPin.Matches(cert, server.Identity.TlsKeyId, errors, DateTimeOffset.UtcNow));
        var serverAuthentication = serverTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = server.Identity.ServerCertificate, ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, deadline.Token);
        var clientAuthentication = clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = server.Identity.DnsName,
            ClientCertificates = [clientCertificate],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, deadline.Token);
        await Task.WhenAll(serverAuthentication, clientAuthentication).ConfigureAwait(false);
        Assert.IsTrue(serverTls.IsMutuallyAuthenticated); Assert.IsTrue(clientTls.IsMutuallyAuthenticated);
        await clientTls.WriteAsync(new byte[] { 42 }, deadline.Token).ConfigureAwait(false);
        var actual = new byte[1]; await serverTls.ReadExactlyAsync(actual, deadline.Token).ConfigureAwait(false);
        Assert.AreEqual((byte)42, actual[0]);
        Assert.IsFalse(TlsPeerPin.Matches(server.Identity.ServerCertificate, clientPin, SslPolicyErrors.RemoteCertificateChainErrors, DateTimeOffset.UtcNow));
        Assert.IsFalse(TlsPeerPin.Matches(server.Identity.ServerCertificate, server.Identity.TlsKeyId, SslPolicyErrors.RemoteCertificateNameMismatch, DateTimeOffset.UtcNow));
        Assert.IsFalse(TlsPeerPin.Matches(server.Identity.ServerCertificate, server.Identity.TlsKeyId, SslPolicyErrors.None, DateTimeOffset.UtcNow.AddYears(3)));
        using var proof = new ProtocolDocumentVerifier(server.Identity.ExportSigningPublicKey());
        Assert.IsTrue(proof.Verify("{\"transport\":\"separate\"}"u8, server.Identity.SignDocument("{\"transport\":\"separate\"}"u8)));
    }

    [TestMethod]
    public async Task ActualTlsRejectsWrongServerPinOrNameBeforeSendingApplicationData()
    {
        await using var vault = new LanIdentityTests.Vault();
        await using var owner = new LanIdentityOwner(vault, Guid.NewGuid(), Guid.NewGuid());
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var wrongName in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            using var outgoing = new TcpClient();
            var connect = outgoing.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            await connect.ConfigureAwait(false);
            using var server = new SslStream(accepted.GetStream());
            using var client = new SslStream(outgoing.GetStream(), false, (_, certificate, _, errors) =>
                TlsPeerPin.Matches(certificate, wrongName ? owner.Identity.TlsKeyId : new string('0', 64), errors, DateTimeOffset.UtcNow));
            var serverAuthentication = server.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = owner.Identity.ServerCertificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
            var rejected = false;
            try
            {
                await client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    { TargetHost = wrongName ? "wrong-processor.local" : owner.Identity.DnsName, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException) { rejected = true; }
            finally
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                outgoing.Dispose(); accepted.Dispose();
                try { await serverAuthentication.ConfigureAwait(false); }
                catch (AuthenticationException) { /* Expected peer rejection. */ }
                catch (IOException) { /* Expected peer closure after rejection. */ }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { /* Joined handshake owner. */ }
            }
            Assert.IsTrue(rejected);
            Assert.IsFalse(client.IsAuthenticated);
        }
    }

    [TestMethod]
    public async Task WindowsTransportKeyContainerIsRemovedWhenItsCertificateOwnerDisposes()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var vault = new LanIdentityTests.Vault();
        var owner = new LanIdentityOwner(vault, Guid.NewGuid(), Guid.NewGuid());
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        string name;
        using (var key = owner.Identity.ServerCertificate.GetECDsaPrivateKey())
        {
            Assert.IsInstanceOfType<ECDsaCng>(key);
            name = ((ECDsaCng)key).Key.KeyName ?? throw new InvalidOperationException("Schannel test requires a named temporary container.");
            using var present = CngKey.Open(name);
            Assert.IsFalse(present.IsEphemeral);
        }
        await owner.DisposeAsync().ConfigureAwait(false);
        var removed = false;
        try { using var missing = CngKey.Open(name); }
        catch (CryptographicException) { removed = true; }
        Assert.IsTrue(removed, "The owned temporary Windows key container must be removed after disposal.");
    }
}
