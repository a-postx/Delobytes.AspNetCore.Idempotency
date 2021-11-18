# Delobytes.AspNetCore.Idempotency
Ресурсный фильтр контроля идемпотентности для веб-апи приложений. Использование фильтра позволит обеспечить целостность данных при работе с вашим АПИ - на случайные дубликаты запросов будут возвращены кешированные данные ответа. Вы можете исплользовать любого поставщика распределённого кеша, который реализует интерфейс IDistributedCache (кеш в памяти, Редис и т.д.).

[RU](README.md), [EN](README.en.md)

## Использование
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

```
[Route("[controller]")]
[ApiController]
public class HomeController : ControllerBase
{
    [HttpPost]
    [ServiceFilter(typeof(IdempotencyFilterAttribute))] //<-- !
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

## Лицензия
[МИТ](https://github.com/a-postx/Delobytes.AspNetCore.Idempotency/blob/master/LICENSE)