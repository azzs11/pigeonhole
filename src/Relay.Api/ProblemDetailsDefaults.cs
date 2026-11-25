using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Relay.Api;

internal static class ProblemDetailsDefaults
{
    public static void AddTraceId(ProblemDetailsContext context) =>
        context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
}

/// <summary>
/// The single place unexpected exceptions become responses: RFC 9457 ProblemDetails
/// with a trace id and never the exception text.
/// </summary>
internal sealed partial class UnhandledExceptionHandler(
    IProblemDetailsService problemDetails, ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception is BadHttpRequestException bad
            ? (bad.StatusCode, "The request could not be read.")
            : (StatusCodes.Status500InternalServerError, "An unexpected error occurred.");

        if (status >= 500)
        {
            LogUnhandled(logger, exception);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
