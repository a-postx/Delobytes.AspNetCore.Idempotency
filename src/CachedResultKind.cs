namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Разновидности результата запроса.
/// </summary>
public enum CachedResultKind
{
    /// <summary>Тип результата не определён (запись в кеше отсутствует или повреждена).</summary>
    Unknown = 0,

    //MVC (IActionResult), используется IdempotencyFilterAttribute

    /// <summary>NoContentResult/OkResult/StatusCodeResult/ActionResult без тела ответа.</summary>
    MvcStatusCodeOnly,
    /// <summary>Произвольный <see cref="Microsoft.AspNetCore.Mvc.ObjectResult"/> с телом ответа.</summary>
    MvcObjectResult,
    /// <summary>
    /// <see cref="Microsoft.AspNetCore.Mvc.CreatedAtRouteResult"/> с телом ответа и данными для
    /// построения результирующего маршрута.
    /// </summary>
    MvcCreatedAtRouteResult,

    //Minimal API (IResult/TypedResults), используется IdempotencyEndpointFilter<T>

    /// <summary>TypedResults.Ok() без тела.</summary>
    Ok,
    /// <summary>TypedResults.Ok(value) с типизированным телом T.</summary>
    OkOfT,
    /// <summary>TypedResults.Created(location) без тела.</summary>
    Created,
    /// <summary>TypedResults.CreatedAtRoute(value, ...) с типизированным телом T.</summary>
    CreatedAtRouteOfT,
    /// <summary>TypedResults.Accepted(location) без тела.</summary>
    Accepted,
    /// <summary>TypedResults.AcceptedAtRoute(value, ...) с типизированным телом T.</summary>
    AcceptedAtRouteOfT,
    /// <summary>TypedResults.NotFound().</summary>
    NotFound,
    /// <summary>TypedResults.Unauthorized().</summary>
    Unauthorized,
    /// <summary>TypedResults.UnprocessableEntity().</summary>
    UnprocessableEntity,
    /// <summary>TypedResults.Problem().</summary>
    Problem,
    /// <summary>TypedResults.BadRequest() без тела.</summary>
    BadRequest,
    /// <summary>TypedResults.BadRequest(ProblemDetails).</summary>
    BadRequestOfProblemDetails,
    /// <summary>TypedResults.BadRequest(string).</summary>
    BadRequestOfString,
    /// <summary>TypedResults.Forbid().</summary>
    Forbidden,
    /// <summary>TypedResults.Conflict().</summary>
    Conflict,
    /// <summary>TypedResults.NoContent().</summary>
    NoContent,
    /// <summary>Произвольный IStatusCodeHttpResult без тела (StatusCodeHttpResult).</summary>
    StatusCodeOnlyMinimalApi
}
