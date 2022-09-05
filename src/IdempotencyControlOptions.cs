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
    /// <para>Default: true</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <para>
    /// Флаг, разрешающий благополучную отработку запроса если на уровне идемпотентности произошёл сбой.
    /// Если контроль идемпотентности более важен, чем отказ в обслуживании (напр. финансовое приложение),
    /// то вы можете отключить эту опцию.
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
    /// Заголовок идемпотентности, значение которого нужно обрабатывать как идентификатор запроса.
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
    /// Время (в часах), по прошествии которого значение будет удалено из кеша. 
    /// </para>
    /// <para>Default: 24</para>
    /// </summary>
    public int CacheAbsoluteExpirationHrs { get; set; } = 24;

    /// <summary>
    /// <para>
    /// Таймаут обращений к кешу.
    /// Если больше нуля, то запрос к кешу будет отменён через указанное кол-во милисекунд.
    /// Параметром можно контролировать скорость отключения обработчика в случае проблем с кешем. 
    /// </para>
    /// <para>Default: 0</para>
    /// </summary>
    public int CacheRequestTimeoutMs { get; set; } = 0;

    /// <summary>
    /// <para>
    /// Тип используемого форматировщика вывода тела запроса при возвращении запроса из кеша.
    /// </para>
    /// <para>Default: <see cref="OutputFormatterType.Newtonsoft"/></para>
    /// </summary>
    public OutputFormatterType BodyOutputFormatterType { get; set; } = OutputFormatterType.Newtonsoft;
}
