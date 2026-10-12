using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sendway.Core;

namespace Sendway.Service.Tests;

/// <summary>
/// Tests run with the background delivery worker switched off (see TestIsolatedStorage) and drive
/// delivery themselves through this helper, so "accepted but not yet delivered" and "delivered"
/// are both states a test can observe without waiting or racing a background loop.
/// </summary>
internal static class TestDelivery
{
    public static async Task DeliverPendingAsync(WebApplicationFactory<Program> factory)
    {
        var processor = factory.Services.GetRequiredService<MessageDeliveryProcessor>();

        while (await processor.ProcessNextAsync())
        {
        }
    }
}
