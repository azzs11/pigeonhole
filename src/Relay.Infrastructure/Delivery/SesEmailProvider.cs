using System.Net.Http;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Relay.Application;

namespace Relay.Infrastructure.Delivery;

/// <summary>Amazon SES v2 with raw MIME, so the deterministic Message-ID is in the content we hand over.</summary>
internal sealed class SesEmailProvider(string name, IAmazonSimpleEmailServiceV2 ses, TimeProvider time) : IEmailProvider
{
    public string Name => name;
    public bool SupportsIdempotentRetry => false;

    public static IAmazonSimpleEmailServiceV2 CreateClient(SesSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var config = new AmazonSimpleEmailServiceV2Config { MaxErrorRetry = 0 };
        if (settings.ServiceUrl is not null)
        {
            config.ServiceURL = settings.ServiceUrl.ToString();
            config.AuthenticationRegion = settings.Region;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(settings.Region);
        }

        return settings.AccessKeyId is { Length: > 0 }
            ? new AmazonSimpleEmailServiceV2Client(new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey), config)
            : new AmazonSimpleEmailServiceV2Client(config);
    }

    public async Task<ProviderOutcome> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        using var mime = MimeBuilder.Build(email, time.GetUtcNow(), includeBcc: false);
        using var stream = new MemoryStream();
        await mime.WriteToAsync(stream, cancellationToken);
        stream.Position = 0;

        var request = new SendEmailRequest
        {
            Destination = new Destination { ToAddresses = [.. email.To], CcAddresses = [.. email.Cc], BccAddresses = [.. email.Bcc] },
            Content = new EmailContent { Raw = new RawMessage { Data = stream } },
        };

        try
        {
            var response = await ses.SendEmailAsync(request, cancellationToken);
            return ProviderOutcome.Sent(response.MessageId);
        }
        catch (Exception ex) when (ex is MessageRejectedException or MailFromDomainNotVerifiedException or BadRequestException or NotFoundException)
        {
            return ProviderOutcome.Rejected($"SES rejected the message ({ex.GetType().Name})");
        }
        catch (Exception ex) when (ex is TooManyRequestsException or LimitExceededException or SendingPausedException or AccountSuspendedException)
        {
            return ProviderOutcome.Transient($"SES refused to send now ({ex.GetType().Name})");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            return ProviderOutcome.Transient($"SES unreachable ({ex.HttpRequestError})");
        }
        catch (AmazonServiceException ex) when ((int)ex.StatusCode < 500)
        {
            return ProviderOutcome.Rejected($"SES {(int)ex.StatusCode} ({ex.ErrorCode})");
        }
        catch (AmazonServiceException ex)
        {
            return ProviderOutcome.Unknown($"SES {(int)ex.StatusCode} ({ex.ErrorCode})");
        }
        catch (AmazonClientException ex)
        {
            return ProviderOutcome.Unknown($"SES client error ({ex.GetType().Name})");
        }
    }
}
