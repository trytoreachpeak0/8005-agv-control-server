namespace ControlServer.Host.Transport;

public sealed class OnboardTransportOptions
{
    public const string SectionName = "OnboardTransport";

    public bool Enabled { get; set; } = true;
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 58005;
    public int MaxLineBytes { get; set; } = 1_048_576;
    public string? ServerCertificatePath { get; set; }
    public string? ServerCertificatePasswordEnvironmentVariable { get; set; }
    public string CredentialEnvironmentVariable { get; set; } = "CONTROL_SERVER_ONBOARD_CREDENTIAL";
    public bool AllowInsecureLoopback { get; set; } = true;
}
