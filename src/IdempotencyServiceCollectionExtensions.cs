using System;
using System.Linq;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Delobytes.AspNetCore.Idempotency
{
    /// <summary>
    /// Расширения <see cref="IServiceCollection"/> для регистрации сервисов.
    /// </summary>
    public static class IdempotencyServiceCollectionExtensions
    {
        /// <summary>
        /// Добавляет в <see cref="IServiceCollection"/> атрибут контроля идемпотентности.
        /// </summary>
        /// <param name="services"><see cref="IServiceCollection"/> в которую нужно добавить контроль идемпотентности.</param>
        /// <param name="configure"><see cref="Action{IdempotencyControlOptions}"/> для настройки <see cref="IdempotencyControlOptions"/>.</param>
        /// <returns>Ссылка на этот экземпляр после завершения операции.</returns>
        public static IServiceCollection AddIdempotencyControl(this IServiceCollection services, Action<IdempotencyControlOptions> configure = null)
        {
            if (services is null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (!services.Any(x => x.ServiceType == typeof(IDistributedCache)))
            {
                throw new InvalidOperationException("An IDistributedCache provider must be registered for idempotency control.");
            }

            if (configure is not null)
            {
                services.Configure(configure);
            }

            services.AddScoped<IdempotencyFilterAttribute>();

            return services;
        }
    }
}
