using System.Net;
using System.Text.Json;
using Pixelz.Domain.Common;

namespace Pixelz.API.Middleware;

/// <summary>
/// Catches all unhandled exceptions and returns a consistent JSON error response.
/// The RequestId (from Serilog log context) is included so support can correlate
/// the error response with the log file entry.
/// </summary>
public class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var requestId = context.TraceIdentifier;

        var (statusCode, errorCode, message) = exception switch
        {
            NotFoundException nfe => (HttpStatusCode.NotFound, "NOT_FOUND", nfe.Message),
            BusinessRuleException bre => (HttpStatusCode.BadRequest, "BUSINESS_ERROR", bre.Message),
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "UNAUTHORIZED", "Authentication required"),
            ExternalServiceException ese => (HttpStatusCode.BadGateway, "EXTERNAL_ERROR", ese.Message),
            _ => (HttpStatusCode.InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred. Please try again later.")
        };

        if (statusCode == HttpStatusCode.InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception. RequestId={RequestId} Path={Path}", requestId, context.Request.Path);
        }
        else
        {
            logger.LogWarning(exception, "Handled exception {ExType}. RequestId={RequestId} Path={Path}", exception.GetType().Name, requestId, context.Request.Path);
        }

        context.Response.StatusCode  = (int)statusCode;
        context.Response.ContentType = "application/json";

        var body = JsonSerializer.Serialize(
            new { error = errorCode, message, requestId, timestamp = DateTime.UtcNow },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        await context.Response.WriteAsync(body);
    }
}