namespace Relay.Infrastructure.Delivery;

public enum ProviderType
{
    Smtp,
    Resend,
    Ses,
}

public enum SmtpSecurity
{
    None,
    StartTls,
    SslOnConnect,
    Auto,
}

/// <summary>One entry of <c>Relay:Providers</c>. Order in configuration is failover order.</summary>
public sealed record ProviderSettings
{
    public string Name { get; init; } = string.Empty;
    public ProviderType Type { get; init; }
    public SmtpSettings? Smtp { get; init; }
    public ResendSettings? Resend { get; init; }
    public SesSettings? Ses { get; init; }
}

public sealed record SmtpSettings
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 1025;
    public SmtpSecurity Security { get; init; } = SmtpSecurity.Auto;
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public sealed record ResendSettings
{
    public string ApiKey { get; init; } = string.Empty;
    public Uri BaseUrl { get; init; } = new("https://api.resend.com/");
}

public sealed record SesSettings
{
    public string Region { get; init; } = "us-east-1";

    /// <summary>Override endpoint (tests, LocalStack). Leave empty for AWS.</summary>
    public Uri? ServiceUrl { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretAccessKey { get; init; }
}
