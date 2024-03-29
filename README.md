﻿# Delobytes.AspNetCore.Idempotency
Ресурсный фильтр контроля идемпотентности для веб-апи приложений. Использование фильтра позволит обеспечить целостность данных при работе с вашим АПИ - на случайные дубликаты запросов будут возвращены кешированные данные ответа. Вы можете исплользовать любого поставщика распределённого кеша, который реализует интерфейс IDistributedCache (кеш в памяти, Редис и т.д.).

Реализация на базе документации Stripe https://stripe.com/docs/api/idempotent_requests

[RU](README.md), [EN](README.en.md)

## Использование

### Ресурсный фильтр MVC

1. Обеспечьте добавление заголовка с ключом идемпотентности на запросы с клиентского приложения или АПИ-клиента. Пусть клиент посылает заголовок "idempotencykey".
2. Добавьте выбранного поставщика распределённого кеша (IDistributedCache) и фильтр в инъекции зависимостей:  

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddDistributedMemoryCache();
    services.AddIdempotencyControl(options =>
        {
            options.Enabled = true;
            options.HeaderRequired = true;
            options.IdempotencyHeader = "idempotencykey";
            options.CacheKeysPrefix = "cached-idempotency-keys";
            options.BodyOutputFormatterType = OutputFormatterType.Newtonsoft;
        });
}
```

3. Поместите атрибут IdempotencyFilterAttribute к неидемпотентному методу или ко всему контроллеру:

```csharp
[Route("[controller]")]
[ApiController]
public class HomeController : ControllerBase
{
    [HttpPost]
    [ServiceFilter(typeof(IdempotencyFilterAttribute))]
    public Task<IActionResult> PostInfoAsync(
        [FromServices] IPostClientInfoAh handler,
        [FromBody] InfoSm infoSm,
        CancellationToken cancellationToken)
    {
        return handler.ExecuteAsync(infoSm, cancellationToken);
    }
}
```

В результате клиент будет получать ответы из кеша на 2-й и последующие запросы с одинаковым ключом идемпотентности.

### Фильтр минимального АПИ

1. Обеспечьте добавление заголовка с ключом идемпотентности на запросы с клиентского приложения или АПИ-клиента. Пусть клиент посылает заголовок "idempotencykey".
2. Добавьте выбранного поставщика распределённого кеша (IDistributedCache) и фильтр в инъекции зависимостей:  

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddDistributedMemoryCache();
    services.AddIdempotencyControl(options =>
        {
            options.Enabled = true;
            options.HeaderRequired = true;
            options.IdempotencyHeader = "idempotencykey";
            options.CacheKeysPrefix = "cached-idempotency-keys";
        });
}
```

3. Добавьте вызов фильтра к неидемпотентной конечной точке:

```csharp
public static class TagsEndpoints
{
    public static IEndpointRouteBuilder MapTagsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder tags = app.MapGroup("/tags").WithTags("Tags");

        tags.MapPost("/", TagsEndpoints.PostTagAsync)
           .WithName(nameof(TagsEndpoints.PostTagAsync))
           .Accepts<TagSm>("application/json")
           .AddEndpointFilter<IdempotencyEndpointFilter<TagVm>>()
           .WithMetadata(new SwaggerOperationAttribute("Создать тег.", "Создать новый тег."))
           .WithMetadata(new SwaggerResponseAttribute(StatusCodes.Status201Created, "Модель созданного тега.", typeof(TagVm)))
           .WithMetadata(new SwaggerResponseAttribute(StatusCodes.Status400BadRequest, "Недопустимый запрос."));

        return app;
    }
}
```

## Лицензия
[МИТ](https://github.com/a-postx/Delobytes.AspNetCore.Idempotency/blob/master/LICENSE)