using System;
using System.Collections.Generic;
using System.Reflection;

namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// <para>
/// Реестр типов тела ответа, которые <see cref="IdempotencyFilterAttribute"/> (MVC) может безопасно
/// восстанавливать из кеша при воспроизведении ответа на повторный запрос.
/// </para>
/// <para>
/// Поддерживает два взаимодополняющих способа регистрации типов:
/// <list type="bullet">
/// <item>
/// <description>
/// Явную регистрацию конкретных типов через <see cref="Add{T}"/> — подходит когда набор DTO небольшой и известен заранее.
/// </description>
/// </item>
/// <item>
/// <description>
/// Добавление резолвера-делегата (<see cref="SetResolver(Func{string, Type?})"/>). Он вызывается для ключей, отсутствующих в явном словаре
/// и позволяет подключать большое количество своих DTO (например через сканирование библиотеки).
/// </description>
/// </item>
/// </list>
/// </para>
/// </summary>
public sealed class IdempotencyBodyTypeRegistry
{
    private readonly Dictionary<string, Type> _typesByKey = new(StringComparer.Ordinal);
    private Func<string, Type?>? _resolver;

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
    /// Регистрирует тип <typeparamref name="T"/> в словаре как разрешённый для восстановления тела ответа из кеша.
    /// </summary>
    /// <typeparam name="T">Тип DTO, который контроллер может вернуть в теле ответа.</typeparam>
    /// <returns>Реестр.</returns>
    public IdempotencyBodyTypeRegistry Add<T>()
    {
        Type type = typeof(T);
        _typesByKey[GetKey(type)] = type;
        return this;
    }

    /// <summary>
    /// Задаёт резолвер—делегат, который будет вызван для ключей типов, отсутствующих в явном словаре.
    /// </summary>
    /// <param name="resolver">Делегат резолвинга типа по ключу.</param>
    /// <returns>Этот же реестр.</returns>
    public IdempotencyBodyTypeRegistry SetResolver(Func<string, Type?> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        return this;
    }

    /// <summary>
    /// Создаёт готовый резолвер, который ищет тип по ключу (<see cref="Type.FullName"/>) только среди
    /// явно переданных сборок через <see cref="Assembly.GetType(string, bool)"/>.
    /// </summary>
    /// <param name="assemblies">Доверенные сборки, в которых следует искать типы DTO приложения.</param>
    /// <example>
    /// <code>
    /// options.BodyTypeRegistry.SetResolver(
    ///     IdempotencyBodyTypeRegistry.CreateAssemblyResolver(typeof(MyResponseDto).Assembly));
    /// </code>
    /// </example>
    public static Func<string, Type?> CreateAssemblyResolver(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        Assembly[] snapshot = (Assembly[])assemblies.Clone();

        return key =>
        {
            foreach (Assembly assembly in snapshot)
            {
                Type? type = assembly.GetType(key, throwOnError: false);

                if (type is not null)
                {
                    return type;
                }
            }

            return null;
        };
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
    /// Найти тип по ключу.
    /// </summary>
    public bool TryResolve(string key, out Type? type)
    {
        if (_typesByKey.TryGetValue(key, out type))
        {
            return true;
        }

        if (_resolver is not null)
        {
            type = _resolver(key);
            return type is not null;
        }

        type = null;
        return false;
    }
}
