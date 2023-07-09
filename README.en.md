# Delobytes.AspNetCore.Idempotency
Idempotency control filter for web-API apps. Filter will allow to keep data consistency for clients working with your API by returning a cached response for any request duplicate. You can choose any cache provider by registering IDistributedCache implementation (memory cache, Redis etc.).

Implementation is based on Stripe docs https://stripe.com/docs/api/idempotent_requests

[RU](README.md), [EN](README.en.md)

## Usage

### MVC Resource Filter

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

Your client will start getting cached responses to 2-nd and any further requests with the same idempotency key.

### Minimal-API filter

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
        });
}
```

3. Add filter calls to any non-idempotent endpoint:

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
           .WithMetadata(new SwaggerOperationAttribute("Create tag.", "Create new tag."))
           .WithMetadata(new SwaggerResponseAttribute(StatusCodes.Status201Created, "Created tag model.", typeof(TagVm)))
           .WithMetadata(new SwaggerResponseAttribute(StatusCodes.Status400BadRequest, "Bad request."));

        return app;
    }
}
```

Your client will start getting cached responses to 2-nd and any further requests with the same idempotency key.

## License
[MIT](https://github.com/a-postx/Delobytes.AspNetCore.Idempotency/blob/master/LICENSE)