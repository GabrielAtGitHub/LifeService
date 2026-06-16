using System.Net;
using LifeService.Api.Contracts;
using LifeService.Domain.Errors;

namespace LifeService.Api.Middleware;

/// <summary>
/// Global exception middleware. Translates <see cref="LifeException"/> into deterministic error
/// responses and never leaks stack traces (SYSTEM_SPECIFICATION.md §10).
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (LifeException ex)
        {
            await WriteErrorAsync(context, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Path}", context.Request.Path);
            await WriteErrorAsync(context, LifeErrorCode.InternalError,
                "An unexpected error occurred.").ConfigureAwait(false);
        }
    }

    private static Task WriteErrorAsync(HttpContext context, LifeErrorCode code, string message)
    {
        var status = code switch
        {
            LifeErrorCode.BoardNotFound => HttpStatusCode.NotFound,
            LifeErrorCode.BoardQuarantined => HttpStatusCode.Conflict,
            LifeErrorCode.ActiveCellLimitExceeded => HttpStatusCode.UnprocessableEntity,
            LifeErrorCode.StatesLimitExceeded => HttpStatusCode.UnprocessableEntity,
            LifeErrorCode.InvalidRange => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.InternalServerError,
        };

        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new ErrorResponse(code.ToString(), message));
    }
}
