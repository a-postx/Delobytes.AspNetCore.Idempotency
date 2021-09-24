namespace Delobytes.AspNetCore.Idempotency
{
    /// <summary>
    /// Тип форматировщика ответа для выдачи тела ответа из кеша.
    /// </summary>
    public enum OutputFormatterType
    {
        /// <summary>
        /// <see cref="Newtonsoft.Json"/>
        /// </summary>
        Newtonsoft = 0,
        /// <summary>
        /// <see cref="System.Text"/>
        /// </summary>
        SystemText = 1
    }
}
