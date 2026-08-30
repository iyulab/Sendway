namespace Sendway.Core;

public sealed class GraphOptions
{
    // Azure AD's own "Directory (tenant) ID" — deliberately not named TenantId, which throughout
    // this codebase means Sendway's own Tenant.Id (a different, unrelated identifier).
    public required string DirectoryId { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string FromAddress { get; init; }
}
