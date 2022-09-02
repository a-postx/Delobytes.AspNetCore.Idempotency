namespace Delobytes.AspNetCore.Idempotency;

/// <summary>
/// Настройки контроля идемпотентности.
/// </summary>
public class IdempotencyControlOptions
{
    /// <summary>
    /// <para>
    /// Включает контроль идемпотентности.
    /// </para>
    /// <para>Default: Idempotency-Key</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <para>
    /// Флаг, разрешающий благополучную отработку запроса если на уровне идемпотентности произошёл сбой.
    /// Если контроль идемпотентности более важен, чем отказ в обслуживании (финансовое приложение),
    /// то вы можете отключить этот флаг.
    /// </para>
    /// <para>Default: true</para>
    /// </summary>
    public bool Optional { get; set; } = true;

    /// <summary>
    /// <para>
    /// Включает обязательность заголовка идемпотентности. Когда включено, на запросы без
    /// соответствующего заголовка будет возвращаться код 400.
    /// </para>
    /// <para>Default: false</para>
    /// </summary>
    public bool HeaderRequired { get; set; } = false;

    /// <summary>
    /// <para>
    /// Заголовок идемпотентности, значение которого нужно обрабатывать.
    /// </para>
    /// <para>Default: Idempotency-Key</para>
    /// </summary>
    public string IdempotencyHeader { get; set; } = "Idempotency-Key";

    /// <summary>
    /// <para>
    /// Префикс, который будет добавляться ко всем ключам в распределённом кеше.
    /// </para>
    /// <para>Default: idempotency_keys</para>
    /// </summary>
    public string CacheKeysPrefix { get; set; } = "idempotency_keys";

    /// <summary>
    /// <para>
    /// Тип используемого форматировщика вывода тела запроса при возвращении запроса из кеша.
    /// </para>
    /// <para>Default: <see cref="OutputFormatterType.Newtonsoft"/></para>
    /// </summary>
    public OutputFormatterType BodyOutputFormatterType { get; set; } = OutputFormatterType.Newtonsoft;
}
