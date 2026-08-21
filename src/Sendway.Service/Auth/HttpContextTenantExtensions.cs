using Sendway.Core;

namespace Sendway.Service.Auth;

public static class HttpContextTenantExtensions
{
    private const string TenantItemKey = "Sendway.Tenant";

    public static void SetTenant(this HttpContext context, Tenant tenant)
    {
        context.Items[TenantItemKey] = tenant;
    }

    public static Tenant GetTenant(this HttpContext context)
    {
        return (Tenant)context.Items[TenantItemKey]!;
    }
}
