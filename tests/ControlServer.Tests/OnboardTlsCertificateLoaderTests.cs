using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ControlServer.Host.Transport;

namespace ControlServer.Tests;

public sealed class OnboardTlsCertificateLoaderTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task MachineKeyCertificateCompletesARealSchannelHandshake()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"controlserver-tls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string pfxPath = Path.Combine(directory, "server.pfx");
        const string password = "temporary-test-password";

        try
        {
            using RSA key = RSA.Create(2048);
            CertificateRequest request = new(
                "CN=localhost",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
            OidCollection usages = new();
            usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
            using X509Certificate2 source = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(10));
            await File.WriteAllBytesAsync(
                pfxPath,
                source.Export(X509ContentType.Pkcs12, password),
                TestContext.Current.CancellationToken);

            using X509Certificate2 serverCertificate = OnboardTlsCertificateLoader.Load(pfxPath, password);
            Assert.True(serverCertificate.HasPrivateKey);
            string expectedCertificateSha256 = Convert.ToHexString(
                SHA256.HashData(serverCertificate.RawData));

            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task server = AuthenticateServerAsync(
                    listener,
                    serverCertificate,
                    TestContext.Current.CancellationToken);

                using TcpClient client = new();
                await client.ConnectAsync(
                    IPAddress.Loopback,
                    port,
                    TestContext.Current.CancellationToken);
                await using SslStream clientTls = new(
                    client.GetStream(),
                    false,
                    (_, certificate, _, _) => certificate is not null &&
                        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))
                            .Equals(expectedCertificateSha256, StringComparison.Ordinal));
                await clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, TestContext.Current.CancellationToken);
                await clientTls.WriteAsync(
                    new byte[] { 0x2a },
                    TestContext.Current.CancellationToken);

                await server;
            }
            finally
            {
                listener.Stop();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static async Task AuthenticateServerAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        using TcpClient accepted = await listener.AcceptTcpClientAsync(cancellationToken);
        await using SslStream serverTls = new(accepted.GetStream(), false);
        await serverTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, cancellationToken);
        byte[] value = new byte[1];
        int read = await serverTls.ReadAsync(value, cancellationToken);
        Assert.Equal(1, read);
        Assert.Equal(0x2a, value[0]);
    }
}
