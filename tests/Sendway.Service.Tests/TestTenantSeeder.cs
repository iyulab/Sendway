using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sendway.Core;

namespace Sendway.Service.Tests;

internal static class TestTenantSeeder
{
    public static async Task<(Guid TenantId, string ApiKey)> SeedAsync(WebApplicationFactory<Program> factory, string name = "test-tenant")
    {
        var tenantStore = factory.Services.GetRequiredService<ITenantStore>();
        var (plaintextKey, hash) = ApiKeyGenerator.Generate();
        var tenant = await tenantStore.CreateAsync(name, hash);

        return (tenant.Id, plaintextKey);
    }
}
