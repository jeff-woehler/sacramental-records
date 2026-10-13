namespace SacramentalRecords.Infrastructure;

public sealed class LocalWorkbookOptions
{
    public required string WorkbookPath { get; init; }
    public string PeopleTable { get; init; } = "People";
    public string SacramentsTable { get; init; } = "Sacraments";
    public string AuditTable { get; init; } = "AuditEvents";
}
