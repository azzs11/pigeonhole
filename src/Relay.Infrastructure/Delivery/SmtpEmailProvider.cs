using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Relay.Application;

namespace Relay.Infrastructure.Delivery;

/// <summary>
/// SMTP via MailKit. Anything that fails before DATA is handed over is definitely not sent;
/// a broken connection or timeout after that is an unknown outcome.
/// </summary>
internal sealed class SmtpEmailProvider(string name, SmtpSettings settings, TimeProvider time) : IEmailProvider
{
    public string Name => name;
    public bool SupportsIdempotentRetry => false;

    public async Task<ProviderOutcome> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        using var message = MimeBuilder.Build(email, time.GetUtcNow(), includeBcc: true);
        using var client = new SmtpClient();
        var handedOver = false;
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, Map(settings.Security), cancellationToken);
            if (!string.IsNullOrEmpty(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, cancellationToken);
            }

            handedOver = true;
            var response = await client.SendAsync(message, cancellationToken);
            await DisconnectQuietlyAsync(client);
            return ProviderOutcome.Sent(Truncate(response.ResponseText ?? string.Empty));
        }
        catch (SmtpCommandException ex)
        {
            var stage = ex.ErrorCode switch
            {
                SmtpErrorCode.RecipientNotAccepted => "recipient",
                SmtpErrorCode.SenderNotAccepted => "sender",
                _ => "message",
            };
            var detail = $"SMTP {(int)ex.StatusCode}: {stage} not accepted";
            return (int)ex.StatusCode >= 500 ? ProviderOutcome.Rejected(detail) : ProviderOutcome.Transient(detail);
        }
        catch (AuthenticationException)
        {
            return ProviderOutcome.Transient("SMTP authentication failed");
        }
        catch (OperationCanceledException) when (!handedOver)
        {
            return ProviderOutcome.Transient("SMTP connect timed out or was cancelled");
        }
        catch (Exception ex) when (ex is SocketException or IOException or SmtpProtocolException or SslHandshakeException)
        {
            return handedOver
                ? ProviderOutcome.Unknown($"SMTP connection lost during send ({ex.GetType().Name})")
                : ProviderOutcome.Transient($"SMTP connection failed ({ex.GetType().Name})");
        }
    }

    private static SecureSocketOptions Map(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };

    private static async Task DisconnectQuietlyAsync(SmtpClient client)
    {
        // The message is already accepted; a failed QUIT must not change the outcome.
        try
        {
            await client.DisconnectAsync(quit: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ServiceNotConnectedException or SmtpProtocolException)
        {
        }
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];
}
