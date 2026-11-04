using MeshCore.Client;
using MeshCore.Host.Auth;
using MeshCore.Protocol;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace MeshCore.Host.Api;

/// <summary>Turns radio and account errors into RFC 9457 problem responses with the right status code.</summary>
internal sealed partial class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            AuthException { Error: AuthError.Validation } => (StatusCodes.Status400BadRequest, "Invalid request"),
            AuthException { Error: AuthError.NotFound } => (StatusCodes.Status404NotFound, "Not found"),
            AuthException { Error: AuthError.Conflict } => (StatusCodes.Status409Conflict, "Conflict"),
            AuthException { Error: AuthError.Forbidden } => (StatusCodes.Status403Forbidden, "Not allowed"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
            FormatException => (StatusCodes.Status400BadRequest, "Invalid request"),
            MeshCoreDisconnectedException => (StatusCodes.Status503ServiceUnavailable, "Radio not connected"),
            MeshCoreTimeoutException => (StatusCodes.Status504GatewayTimeout, "Radio did not answer"),
            MeshCoreCommandException { IsDisabled: true } => (StatusCodes.Status501NotImplemented, "Disabled on this radio"),
            MeshCoreCommandException c => c.Error switch
            {
                ErrorCode.NotFound => (StatusCodes.Status404NotFound, "Not found on the radio"),
                ErrorCode.TableFull => (StatusCodes.Status409Conflict, "Radio storage is full"),
                ErrorCode.BadState => (StatusCodes.Status409Conflict, "Radio is busy"),
                ErrorCode.IllegalArgument => (StatusCodes.Status409Conflict, "Radio refused the value"),
                ErrorCode.UnsupportedCommand => (StatusCodes.Status501NotImplemented, "Not supported by this firmware"),
                _ => (StatusCodes.Status502BadGateway, "Radio error"),
            },
            _ => (0, ""),
        };

        if (status == 0)
        {
            return false; // Unexpected: let the default handler log it and return 500.
        }

        var exceptionName = exception.GetType().Name;
        LogHandled(exceptionName, status, exception.Message);
        httpContext.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = exception is ArgumentException a && a.ParamName is { } p
                    ? a.Message.Replace($" (Parameter '{p}')", "", StringComparison.Ordinal)
                    : exception.Message,
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Exception} → {Status}: {Message}")]
    private partial void LogHandled(string exception, int status, string message);
}
