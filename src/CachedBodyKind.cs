namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Разновидности тела ответа для (Minimal API).
/// </summary>
public enum CachedBodyKind
{
    /// <summary>Тело ответа отсутствует.</summary>
    None = 0,
    /// <summary>Тело ответа имеет тип T.</summary>
    TValue,
    /// <summary>Тело ответа имеет тип <see cref="string"/>.</summary>
    StringValue,
    /// <summary>Тело ответа имеет тип <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/>.</summary>
    ProblemDetailsValue
}
