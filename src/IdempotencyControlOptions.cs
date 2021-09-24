namespace Delobytes.AspNetCore.Idempotency
{
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
        /// Заголовок идемпотентности, значение которого нужно обрабатывать.
        /// </para>
        /// <para>Default: Idempotency-Key</para>
        /// </summary>
        public string IdempotencyHeader { get; set; } = "Idempotency-Key";

        /// <summary>
        /// <para>
        /// Префикс, который будет добавляться ко всем ключам в распределённом кеше.
        /// </para>
        /// <para>Default: Idempotency-Key</para>
        /// </summary>
        public string CacheKeysPrefix { get; set; } = "idempotency_keys";

        /// <summary>
        /// <para>
        /// Форматировщик вывода тела запроса при возвращении запроса из кеша.
        /// </para>
        /// <para>Default: <see cref="OutputFormatterType.Newtonsoft"/></para>
        /// </summary>
        public OutputFormatterType BodyOutputFormatterType { get; set; } = OutputFormatterType.Newtonsoft;
    }
}
