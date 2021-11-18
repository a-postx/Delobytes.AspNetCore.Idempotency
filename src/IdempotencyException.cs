using System;

namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Исключение идемпотентности.
/// </summary>
public class IdempotencyException : Exception
{
    /// <summary>
    /// Конструктор.
    /// </summary>
    public IdempotencyException()
    {
    }

    /// <summary>
    /// Конструктор.
    /// </summary>
    public IdempotencyException(string message) : base(message)
    {
    }

    /// <summary>
    /// Конструктор.
    /// </summary>
    public IdempotencyException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
