using Sendway.Core;

namespace Sendway.Service.Auth;

public sealed class TenantAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        if (!httpContext.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyValues) ||
            string.IsNullOrWhiteSpace(apiKeyValues))
        {
            return Results.Unauthorized();
        }

        var tenantStore = httpContext.RequestServices.GetRequiredService<ITenantStore>();
        var hash = ApiKeyGenerator.Hash(apiKeyValues.ToString());
        var tenant = await tenantStore.GetByApiKeyHashAsync(hash, httpContext.RequestAborted);

        if (tenant is null || !tenant.Active)
        {
            return Results.Unauthorized();
        }

        httpContext.SetTenant(tenant);
        return await next(context);
    }
}
