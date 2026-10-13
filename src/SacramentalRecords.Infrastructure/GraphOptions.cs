namespace SacramentalRecords.Infrastructure;

public sealed class GraphOptions
{
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string DriveId { get; init; }
    public required string WorkbookItemId { get; init; }
    public string PeopleTable { get; init; } = "People";
    public string SacramentsTable { get; init; } = "Sacraments";
    public string AuditTable { get; init; } = "AuditEvents";
}

// The concrete Graph implementation belongs here. Keep Graph calls behind IRegistryStore so
// the WPF UI never knows whether records came from Excel, SharePoint Lists, or a future API.
public sealed class GraphWorkbookStore
{
    public GraphOptions Options { get; }
    public GraphWorkbookStore(GraphOptions options) => Options = options;
}
