using BuildingDraft.Domain;
using BuildingDraft.Domain.Geometry.Pipeline;
using Microsoft.AspNetCore.Diagnostics;

namespace BuildingDraft.Api.Infrastructure;

// Turns every DomainException into the API error contract: { success, errorCode, message, traceId, ... }.
public sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    static readonly Dictionary<string, int> Statuses = new()
    {
        ["NOT_FOUND"] = StatusCodes.Status404NotFound,
        ["VERSION_CONFLICT"] = StatusCodes.Status409Conflict,
        ["ORIGINAL_LAYOUT_PROTECTED"] = StatusCodes.Status409Conflict,
        ["ORIGINAL_NOT_APPROVED"] = StatusCodes.Status409Conflict,
        ["OPERATION_ID_REUSED"] = StatusCodes.Status409Conflict,
        ["FLOOR_NUMBER_EXISTS"] = StatusCodes.Status409Conflict,
        ["PROJECT_ARCHIVED"] = StatusCodes.Status409Conflict,
        ["UNKNOWN_COMMAND"] = StatusCodes.Status400BadRequest,
        ["INVALID_COMMAND_PAYLOAD"] = StatusCodes.Status400BadRequest,
        ["INVALID_NAME"] = StatusCodes.Status400BadRequest
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not DomainException domain)
            return false;

        var status = Statuses.GetValueOrDefault(domain.Code, StatusCodes.Status422UnprocessableEntity);
        logger.LogInformation("Request rejected with {Code}: {Message}", domain.Code, domain.Message);

        var failure = (domain as GeometryRejectedException)?.Failure;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new
        {
            success = false,
            errorCode = domain.Code,
            message = domain.Message,
            traceId = context.TraceIdentifier,
            errors = failure?.Errors,
            conflictingConstraintIds = failure?.ConflictingConstraintIds
        }, ct);
        return true;
    }
}
