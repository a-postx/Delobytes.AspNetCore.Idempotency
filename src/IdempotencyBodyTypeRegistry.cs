using System;
using System.Collections.Generic;

namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Реестр типов тела ответа, которые <see cref="IdempotencyFilterAttribute"/> (MVC) может безопасно
/// восстанавливать из кеша при воспроизведении ответа на повторный запрос.
/// </summary>
public sealed class IdempotencyBodyTypeRegistry
{
    private readonly Dictionary<string, Type> _typesByKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Конструктор. Регистрирует набор безопасных системных типов по умолчанию.
    /// </summary>
    public IdempotencyBodyTypeRegistry()
    {
        Add<string>();
        Add<bool>();
        Add<byte>();
        Add<short>();
        Add<int>();
        Add<long>();
        Add<float>();
        Add<double>();
        Add<decimal>();
        Add<Guid>();
        Add<DateTime>();
        Add<DateTimeOffset>();
        Add<TimeSpan>();
        Add<Microsoft.AspNetCore.Mvc.ProblemDetails>();
    }

    /// <summary>
    /// Регистрирует тип <typeparamref name="T"/> как разрешённый для восстановления тела ответа из кеша.
    /// </summary>
    /// <typeparam name="T">Тип DTO, который контроллер может вернуть в теле ответа.</typeparam>
    /// <returns>Этот же реестр (для цепочки вызовов).</returns>
    public IdempotencyBodyTypeRegistry Add<T>()
    {
        Type type = typeof(T);
        _typesByKey[GetKey(type)] = type;
        return this;
    }

    /// <summary>
    /// Возвращает строковый ключ, под которым тип будет сохранён в кеше.
    /// </summary>
    public static string GetKey(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.FullName ?? type.Name;
    }

    /// <summary>
    /// Пытается найти тип по ключу среди зарегистрированных.
    /// </summary>
    public bool TryResolve(string key, out Type? type)
    {
        return _typesByKey.TryGetValue(key, out type);
    }
}
