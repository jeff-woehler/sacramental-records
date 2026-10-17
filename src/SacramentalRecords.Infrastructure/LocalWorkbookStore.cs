using ClosedXML.Excel;
using SacramentalRecords.Application;
using SacramentalRecords.Domain;

namespace SacramentalRecords.Infrastructure;

/// <summary>Offline adapter. It deliberately opens the workbook with write access: local mode is administrator-only.</summary>
public sealed class LocalWorkbookStore(LocalWorkbookOptions options) : IRegistryStore
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private readonly string _path = Path.GetFullPath(options.WorkbookPath);

    public async Task<IReadOnlyList<Person>> SearchPeopleAsync(string search, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var book = Open();
            var sheet = FindSheet(book, options.PeopleTable);
            var headers = Headers(sheet);
            return Rows(sheet, headers)
                .Select(row => ToPerson(row, headers))
                .Where(person => string.IsNullOrWhiteSpace(search) ||
                    ($"{person.GivenName} {person.MiddleName} {person.FamilyName} {person.MaidenName} {person.PdsMemberId} {person.FlocknoteId}")
                    .Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        finally { FileLock.Release(); }
    }

    public async Task<Person?> GetPersonAsync(Guid recordId, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var book = Open();
            var sheet = FindSheet(book, options.PeopleTable);
            var headers = Headers(sheet);
            var row = FindRow(sheet, headers, recordId);
            return row is null ? null : ToPerson(row, headers);
        }
        finally { FileLock.Release(); }
    }

    public Task<Person> SavePersonAsync(Person person, DateTimeOffset? expectedUpdatedUtc, CancellationToken cancellationToken) =>
        SaveAsync(person, expectedUpdatedUtc, cancellationToken);

    public async Task DeletePersonAsync(Guid recordId, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var book = Open();
            var peopleSheet = FindSheet(book, options.PeopleTable);
            var peopleHeaders = Headers(peopleSheet);
            var personRow = FindRow(peopleSheet, peopleHeaders, recordId);
            if (personRow is null) return;

            var sacramentSheet = FindSheet(book, options.SacramentsTable);
            var sacramentHeaders = Headers(sacramentSheet);
            foreach (var row in Rows(sacramentSheet, sacramentHeaders)
                .Where(row => Guid.TryParse(Value(row, sacramentHeaders, "PersonRecordId"), out var id) && id == recordId)
                .OrderByDescending(row => row.RowNumber()).ToList()) row.Delete();
            personRow.Delete();
            ResizeTable(book, sacramentSheet, options.SacramentsTable);
            ResizeTable(book, peopleSheet, options.PeopleTable);

            var auditSheet = FindSheet(book, options.AuditTable);
            var auditHeaders = Headers(auditSheet);
            var auditRow = AddRow(auditSheet, auditHeaders);
            Put(auditRow, auditHeaders, "EventId", Guid.NewGuid());
            Put(auditRow, auditHeaders, "RecordId", recordId);
            Put(auditRow, auditHeaders, "User", Environment.UserName);
            Put(auditRow, auditHeaders, "Operation", "delete");
            Put(auditRow, auditHeaders, "OccurredUtc", DateTimeOffset.UtcNow);
            Put(auditRow, auditHeaders, "Summary", "Deleted person and associated entries");
            ResizeTable(book, auditSheet, options.AuditTable);
            book.Save();
        }
        finally { FileLock.Release(); }
    }

    public async Task<IReadOnlyList<SacramentRecord>> GetSacramentsAsync(Guid personRecordId, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var book = Open();
            var sheet = FindSheet(book, options.SacramentsTable);
            var headers = Headers(sheet);
            return Rows(sheet, headers).Select(row => ToSacrament(row, headers))
                .Where(record => record.PersonRecordId == personRecordId).ToList();
        }
        finally { FileLock.Release(); }
    }

    public Task<SacramentRecord> SaveSacramentAsync(SacramentRecord record, DateTimeOffset? expectedUpdatedUtc, CancellationToken cancellationToken) =>
        SaveAsync(record, expectedUpdatedUtc, cancellationToken);

    public async Task DeleteSacramentAsync(Guid recordId, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var book = Open();
            var sheet = FindSheet(book, options.SacramentsTable);
            var headers = Headers(sheet);
            var row = FindRow(sheet, headers, recordId);
            if (row is null) return;

            row.Delete();
            ResizeTable(book, sheet, options.SacramentsTable);

            var auditSheet = FindSheet(book, options.AuditTable);
            var auditHeaders = Headers(auditSheet);
            var auditRow = AddRow(auditSheet, auditHeaders);
            Put(auditRow, auditHeaders, "EventId", Guid.NewGuid());
            Put(auditRow, auditHeaders, "RecordId", recordId);
            Put(auditRow, auditHeaders, "User", Environment.UserName);
            Put(auditRow, auditHeaders, "Operation", "delete");
            Put(auditRow, auditHeaders, "OccurredUtc", DateTimeOffset.UtcNow);
            Put(auditRow, auditHeaders, "Summary", "Deleted entry");
            ResizeTable(book, auditSheet, options.AuditTable);
            book.Save();
        }
        finally { FileLock.Release(); }
    }

    private async Task<Person> SaveAsync(Person person, DateTimeOffset? expected, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var book = Open();
            var sheet = FindSheet(book, options.PeopleTable);
            var headers = Headers(sheet);
            var row = FindRow(sheet, headers, person.RecordId) ?? AddRow(sheet, headers);
            CheckVersion(row, headers, expected);
            var churchDbId = string.IsNullOrWhiteSpace(person.ChurchDbId)
                ? NextChurchDbId(sheet, headers)
                : person.ChurchDbId;
            var hasBaptismRecord = HasBaptismRecord(book, person.RecordId);
            var saved = person with
            {
                ChurchDbId = churchDbId,
                BaptismDate = hasBaptismRecord ? null : person.BaptismDate,
                BaptismChurch = hasBaptismRecord ? null : person.BaptismChurch,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            WritePerson(row, headers, saved);
            SaveTable(book, sheet, options.PeopleTable);
            return saved;
        }
        finally { FileLock.Release(); }
    }

    private async Task<SacramentRecord> SaveAsync(SacramentRecord record, DateTimeOffset? expected, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            using var book = Open();
            var sheet = FindSheet(book, options.SacramentsTable);
            var headers = Headers(sheet);
            var row = FindRow(sheet, headers, record.RecordId) ?? AddRow(sheet, headers);
            CheckVersion(row, headers, expected);
            var saved = record with { UpdatedUtc = DateTimeOffset.UtcNow };
            WriteSacrament(row, headers, saved);

            if (IsBaptism(saved.SacramentType))
                ClearPersonBaptismFields(book, saved.PersonRecordId);

            SaveTable(book, sheet, options.SacramentsTable);
            return saved;
        }
        finally { FileLock.Release(); }
    }

    private XLWorkbook Open()
    {
        if (!File.Exists(_path)) throw new FileNotFoundException("The local Excel workbook was not found.", _path);

        var book = new XLWorkbook(_path);
        try
        {
            ValidateRequiredColumns(book, options.PeopleTable, PeopleColumns);
            ValidateRequiredColumns(book, options.SacramentsTable, SacramentColumns);
            ValidateRequiredColumns(book, options.AuditTable, AuditColumns);
            return book;
        }
        catch
        {
            book.Dispose();
            throw;
        }
    }

    private static readonly string[] PeopleColumns =
    [
        "RecordId", "GivenName", "MiddleName", "FamilyName", "BirthDate", "ChurchDbId",
        "BirthCity", "BaptismDate", "BaptismChurch", "Status", "PdsMemberId", "FlocknoteId", "UpdatedUtc"
    ];

    private static readonly string[] SacramentColumns =
    [
        "RecordId", "PersonRecordId", "SacramentType", "SacramentDate", "Name", "Place", "Minister",
        "FatherName", "MotherName", "SponsorOne", "SponsorTwo", "Volume", "Page", "Item", "Details",
        "CanonicalNotations", "UpdatedUtc"
    ];

    private static readonly string[] AuditColumns =
        ["EventId", "RecordId", "User", "Operation", "OccurredUtc", "Summary"];

    private static void ValidateRequiredColumns(XLWorkbook book, string tableName, IEnumerable<string> requiredColumns)
    {
        var sheet = FindSheet(book, tableName);
        var headers = Headers(sheet);
        var missing = requiredColumns.Where(column => !headers.ContainsKey(column)).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Workbook table '{tableName}' is missing required column(s): {string.Join(", ", missing)}.");
    }

    private static IXLWorksheet FindSheet(XLWorkbook book, string name) =>
        book.Worksheets.FirstOrDefault(sheet => sheet.Tables.Any(table => table.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        ?? throw new InvalidOperationException($"Workbook table '{name}' was not found.");

    private static Dictionary<string, int> Headers(IXLWorksheet sheet) =>
        (sheet.FirstRowUsed() ?? throw new InvalidOperationException("The workbook table has no header row."))
            .CellsUsed().ToDictionary(cell => cell.GetString().Trim(), cell => cell.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);

    private bool HasBaptismRecord(XLWorkbook book, Guid personRecordId)
    {
        var sheet = FindSheet(book, options.SacramentsTable);
        var headers = Headers(sheet);
        return Rows(sheet, headers).Any(row =>
            Guid.TryParse(Value(row, headers, "PersonRecordId"), out var id) && id == personRecordId &&
            IsBaptism(Value(row, headers, "SacramentType")));
    }

    private void ClearPersonBaptismFields(XLWorkbook book, Guid personRecordId)
    {
        var sheet = FindSheet(book, options.PeopleTable);
        var headers = Headers(sheet);
        var row = FindRow(sheet, headers, personRecordId);
        if (row is null) return;

        Put(row, headers, "BaptismDate", null);
        Put(row, headers, "BaptismChurch", null);
        Put(row, headers, "UpdatedUtc", DateTimeOffset.UtcNow);
        SaveTable(book, sheet, options.PeopleTable);
    }

    private static bool IsBaptism(string? value) =>
        string.Equals(value?.Trim().Replace('-', '_').Replace(' ', '_'), "baptism", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<IXLRow> Rows(IXLWorksheet sheet, Dictionary<string, int> headers) =>
        sheet.RowsUsed().Skip(1).Where(row => row.CellsUsed().Any());

    private static IXLRow? FindRow(IXLWorksheet sheet, Dictionary<string, int> headers, Guid id) =>
        Rows(sheet, headers).FirstOrDefault(row => Guid.TryParse(Value(row, headers, "RecordId"), out var found) && found == id);

    private static IXLRow AddRow(IXLWorksheet sheet, Dictionary<string, int> headers) =>
        sheet.Row((sheet.LastRowUsed()?.RowNumber() ?? 1) + 1);

    private static string Value(IXLRow row, Dictionary<string, int> headers, string name) =>
        headers.TryGetValue(name, out var column) ? row.Cell(column).GetString() : "";

    private static DateTimeOffset? DateTimeValue(IXLRow row, Dictionary<string, int> headers, string name) =>
        DateTimeOffset.TryParse(Value(row, headers, name), out var value) ? value : null;

    private static DateOnly? DateValue(IXLRow row, Dictionary<string, int> headers, string name) =>
        DateOnly.TryParse(Value(row, headers, name), out var value) ? value : null;

    private static string NextChurchDbId(IXLWorksheet sheet, Dictionary<string, int> headers)
    {
        var highest = Rows(sheet, headers)
            .Select(row => long.TryParse(Value(row, headers, "ChurchDbId"), out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();
        return (highest + 1).ToString();
    }

    private static void CheckVersion(IXLRow row, Dictionary<string, int> headers, DateTimeOffset? expected)
    {
        if (expected is null) return;
        var actual = DateTimeValue(row, headers, "UpdatedUtc");
        if (actual is not null && actual.Value != expected.Value)
            throw new RecordConflictException("The workbook record changed in another session. Refresh before saving.");
    }

    private static Person ToPerson(IXLRow row, Dictionary<string, int> h) => new(
        Guid.Parse(Value(row, h, "RecordId")), Value(row, h, "GivenName"), Null(Value(row, h, "MiddleName")),
        Value(row, h, "FamilyName"), DateValue(row, h, "BirthDate"), Null(Value(row, h, "ChurchDbId")),
        Null(Value(row, h, "BirthCity")), DateValue(row, h, "BaptismDate"), Null(Value(row, h, "BaptismChurch")),
        Value(row, h, "Status"), Null(Value(row, h, "PdsMemberId")),
        Null(Value(row, h, "FlocknoteId")), DateTimeValue(row, h, "UpdatedUtc") ?? DateTimeOffset.MinValue,
        Null(Value(row, h, "MaidenName")), DateValue(row, h, "DeceasedDate"));

    private static SacramentRecord ToSacrament(IXLRow row, Dictionary<string, int> h) => new(
        Guid.Parse(Value(row, h, "RecordId")), Guid.Parse(Value(row, h, "PersonRecordId")), Value(row, h, "SacramentType"),
        DateValue(row, h, "SacramentDate"), Null(Value(row, h, "Name")), Null(Value(row, h, "Place")), Null(Value(row, h, "Minister")), Null(Value(row, h, "FatherName")),
        Null(Value(row, h, "MotherName")), Null(Value(row, h, "SponsorOne")), Null(Value(row, h, "SponsorTwo")),
        IsTrue(Value(row, h, "SponsorTwoIsChristianWitness")), Null(Value(row, h, "ProxyOne")), Null(Value(row, h, "ProxyTwo")), Null(Value(row, h, "Volume")),
        Null(Value(row, h, "Page")), Null(Value(row, h, "Item")), Null(Value(row, h, "Details")), Null(Value(row, h, "CanonicalNotations")),
        DateTimeValue(row, h, "UpdatedUtc") ?? DateTimeOffset.MinValue);

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static bool IsTrue(string value) => value.Trim() is "1" || bool.TryParse(value, out var b) && b;
    private static void Put(IXLRow row, Dictionary<string, int> h, string name, object? value) { if (h.TryGetValue(name, out var c)) row.Cell(c).Value = value?.ToString() ?? ""; }
    private static void WritePerson(IXLRow row, Dictionary<string, int> h, Person p)
    { Put(row,h,"RecordId",p.RecordId); Put(row,h,"GivenName",p.GivenName); Put(row,h,"MiddleName",p.MiddleName); Put(row,h,"FamilyName",p.FamilyName); Put(row,h,"BirthDate",p.BirthDate); Put(row,h,"ChurchDbId",p.ChurchDbId); Put(row,h,"BirthCity",p.BirthCity); Put(row,h,"BaptismDate",p.BaptismDate); Put(row,h,"BaptismChurch",p.BaptismChurch); Put(row,h,"Status",p.Status); Put(row,h,"PdsMemberId",p.PdsMemberId); Put(row,h,"FlocknoteId",p.FlocknoteId); Put(row,h,"UpdatedUtc",p.UpdatedUtc); Put(row,h,"MaidenName",p.MaidenName); Put(row,h,"DeceasedDate",p.DeceasedDate); }
    private static void WriteSacrament(IXLRow row, Dictionary<string, int> h, SacramentRecord r)
    { Put(row,h,"RecordId",r.RecordId); Put(row,h,"PersonRecordId",r.PersonRecordId); Put(row,h,"SacramentType",r.SacramentType); Put(row,h,"SacramentDate",r.SacramentDate); Put(row,h,"Name",r.Name); Put(row,h,"Place",r.Place); Put(row,h,"Minister",r.Minister); Put(row,h,"FatherName",r.FatherName); Put(row,h,"MotherName",r.MotherName); Put(row,h,"SponsorOne",r.SponsorOne); Put(row,h,"SponsorTwo",r.SponsorTwo); Put(row,h,"SponsorTwoIsChristianWitness",r.SponsorTwoIsChristianWitness); Put(row,h,"ProxyOne",r.ProxyOne); Put(row,h,"ProxyTwo",r.ProxyTwo); Put(row,h,"Volume",r.Volume); Put(row,h,"Page",r.Page); Put(row,h,"Item",r.Item); Put(row,h,"Details",r.Details); Put(row,h,"CanonicalNotations",r.CanonicalNotations); Put(row,h,"UpdatedUtc",r.UpdatedUtc); }
    private static void SaveTable(XLWorkbook book, IXLWorksheet sheet, string tableName)
    {
        ResizeTable(book, sheet, tableName);
        book.Save();
    }

    private static void ResizeTable(XLWorkbook book, IXLWorksheet sheet, string tableName)
    {
        var table = sheet.Tables.First(t => t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        var address = table.RangeAddress;
        var lastRow = Math.Max(sheet.LastRowUsed()?.RowNumber() ?? address.FirstAddress.RowNumber, address.FirstAddress.RowNumber);
        table.Resize(sheet.Range(address.FirstAddress.RowNumber, address.FirstAddress.ColumnNumber,
            lastRow, address.LastAddress.ColumnNumber));
    }
}
