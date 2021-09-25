# Delobytes.AspNetCore.Idempotency
Idempotency control resource filter for web-API apps on .Net 5. Filter will allow to keep data consistency for clients working with your API by returning a cached response for any request duplicate. You can choose any cache provider by registering IDistributedCache implementation (mamory cahce, Redis etc.).

[RU](README.md), [EN](README.en.md)

## Usage
1. Add a header with idempotency key on a client app or API consumer side. Let's say it will be header "idempotencykey".
2. Add your distributed cache provider and filter to DI:  

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

3. Add attribute IdempotencyFilterAttribute to any non-idempotent method or to the whole controller:

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

Your client will start getting cached responses to 2-nd and any further requests with the same idempotency key.

## License
[MIT](https://github.com/a-postx/Delobytes.AspNetCore.Idempotency/blob/master/LICENSE)