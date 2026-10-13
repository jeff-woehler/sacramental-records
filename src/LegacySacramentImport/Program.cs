using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private const string Baptism = "baptism";
    private const string Confirmation = "confirmation";
    private const string FirstCommunion = "first_communion";

    // Column aliases are scoped to a single sacrament type. The legacy sheets repeat columns for
    // other sacraments (the Baptisms sheet carries ConfDate/ConfChurch, the First Communions sheet
    // carries B-Father), so a shared alias list silently reads the wrong sacrament's data.
    private sealed record Mapping(
        string Type,
        string[] Date,
        string[] Place,
        string[] Minister,
        string[] Father,
        string[] Mother,
        string[] SponsorOne,
        string[] SponsorTwo,
        string[] Name,
        string[] Volume,
        string[] Page,
        string[] Item,
        string[] Details);

    private static readonly Mapping BaptismMap = new(
        Baptism,
        Date: ["BaptDate", "Baptism Date", "BaptismDate"],
        Place: ["BaptChurch", "Baptism Church", "BaptismChurch"],
        Minister: ["B-Presider"],
        Father: ["B-Father"],
        Mother: ["B-Mother"],
        SponsorOne: ["Godfather"],
        SponsorTwo: ["Godmother"],
        Name: [],
        Volume: ["B-Volume"],
        Page: ["B-Page"],
        Item: ["B-Number"],
        Details: ["B-Remarks1", "B-Remarks2", "B-Misc1", "B-Misc2"]);

    private static readonly Mapping FirstCommunionMap = new(
        FirstCommunion,
        Date: ["CommDate", "CommunionDate", "FirstCommunionDate"],
        Place: ["CommChurch", "CommunionChurch", "FirstCommunionChurch"],
        Minister: ["F-Presider"],
        Father: ["F-Father"],
        Mother: ["F-Mother"],
        SponsorOne: [],
        SponsorTwo: [],
        Name: [],
        Volume: ["F-Volume"],
        Page: ["F-Page"],
        Item: ["F-Number"],
        Details: ["F-Remarks1", "F-Remarks2", "F-Misc1", "F-Misc2"]);

    private static readonly Mapping ConfirmationMap = new(
        Confirmation,
        Date: ["ConfDate", "ConfirmationDate"],
        Place: ["ConfChurch", "ConfirmationChurch"],
        Minister: ["C-Presider"],
        Father: ["C-Father"],
        Mother: ["C-Mother"],
        SponsorOne: ["Sponsor", "Sponsor1", "Sponsor 1"],
        SponsorTwo: ["Sponsor2", "Sponsor 2"],
        Name: ["ConfName", "SaintsName", "Saint's Name"],
        Volume: ["C-Volume"],
        Page: ["C-Page"],
        Item: ["C-Number"],
        Details: ["C-Remarks1", "C-Remarks2", "C-Misc1", "C-Misc2"]);

    // Person-level columns repeat identically on every sheet.
    private static readonly string[] IdColumns = ["PersonId", "Person ID", "ChurchDbId"];
    private static readonly string[] FirstColumns = ["FirstName", "First Name", "GivenName"];
    private static readonly string[] LastColumns = ["Surname", "LastName", "Last Name", "FamilyName"];
    private static readonly string[] BirthColumns = ["BirthDate", "Birth Date", "DOB"];
    private static readonly string[] BirthCityColumns = ["BirthCity", "Birth City"];
    private static readonly string[] PersonBaptDateColumns = ["BaptDate", "Baptism Date", "BaptismDate"];
    private static readonly string[] PersonBaptPlaceColumns = ["BaptChurch", "Baptism Church", "BaptismChurch"];

    // Legacy placeholders that mean "blank"; storing them would look like real data. Compared after
    // stripping spacing and punctuation so "N / A", "N.A." and "--" all match. The misspellings are
    // the ones that actually occur in the workbook.
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "NOTNOTED", "NOTKNOWN", "NOTLISTED", "NOTRECORDED", "NOTGIVEN", "NOTSTATED",
        "NOTAVAILABLE", "NOTAPPLICABLE", "NONENOTED", "NONELISTED", "NONEGIVEN", "NONERECORDED",
        "NOTHINGNOTED", "NONOTATION", "NONOTATIONS", "NOTNOTE", "NOTNOTES", "NOTNOTTED", "NOTNOTEDD",
        "NOTNOTEED", "NOTNTOED", "NOTNOED", "NOTNMOTED", "NOTAVIALABLE", "UNKNOWN", "NA", "NONE",
        "BLANK", "NIL", string.Empty
    };

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-M-d", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm",
        "MM/dd/yyyy", "M/d/yyyy", "MM-dd-yyyy", "M-d-yyyy", "MM/dd/yy", "M/d/yy", "MM-dd-yy", "M-d-yy",
        "M/d/yyyy H:mm", "M/d/yy H:mm", "M/d/yyyy h:mm tt", "M/d/yy h:mm tt"
    ];

    private const int EarliestPlausibleYear = 1800;

    private sealed record Options(string Input, string Database, bool Commit, string Report, string? Lookup);
    private sealed record SourceRow(string Sheet, int Number, Mapping Map, Dictionary<string, string> Values);
    private sealed record KnownPerson(Guid Id, string? ChurchId, string Given, string? Middle, string Family, DateOnly? Birth);

    private static readonly List<string> Report = ["Sheet,Row,Type,Severity,Action,PersonId,Field,Message"];
    private static readonly Dictionary<string, string> ParishSpellings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LooseParishSpellings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> AmbiguousParishOutputs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> UnmappedParishes = new(StringComparer.Ordinal);
    private static int _errors;
    private static int _warnings;

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (!File.Exists(options.Input)) throw new FileNotFoundException("Input workbook not found", options.Input);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Database))!);
            if (options.Lookup is not null) LoadParishSpellings(options.Lookup);
            if (options.Commit) Backup(options.Database);

            using var workbook = new XLWorkbook(options.Input);
            var rows = ReadWorkbook(workbook);
            if (rows.Count == 0) throw new InvalidOperationException("No sacramental worksheets were found.");

            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(options.Database)}");
            connection.Open();
            Initialize(connection);
            using var transaction = connection.BeginTransaction();

            var people = LoadPeople(connection, transaction);
            var byChurchId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var byNameKey = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
            foreach (var person in people.Values) Index(person, byChurchId, byNameKey);

            var seenSacraments = new HashSet<(Guid Person, string Type)>();
            int created = 0, matched = 0, sacraments = 0;

            foreach (var row in rows)
            {
                try
                {
                    var given = Name(row, FirstColumns);
                    var family = Name(row, LastColumns);
                    if (string.IsNullOrWhiteSpace(given) || string.IsNullOrWhiteSpace(family))
                        throw new InvalidDataException("missing first name or surname");
                    if (Clean(given) is null || Clean(family) is null)
                        Warn(row, "placeholder-name", Value(row, IdColumns), "FirstName/Surname",
                            $"the register records no name ('{given} {family}'); kept verbatim and needs review");

                    var (firstName, middleName) = SplitName(given);
                    var churchId = Value(row, IdColumns);
                    var birth = DateField(row, BirthColumns, "BirthDate");
                    var person = Resolve(row, people, byChurchId, byNameKey, churchId, given, family, birth);
                    var isNew = person is null;
                    if (person is null)
                    {
                        person = new KnownPerson(NewPersonId(churchId, given, family, birth), churchId, firstName, middleName, family, birth);
                        InsertPerson(connection, transaction, person, row);
                        people[person.Id] = person;
                        Index(person, byChurchId, byNameKey);
                        created++;
                    }
                    else matched++;

                    if (!seenSacraments.Add((person.Id, row.Map.Type)))
                        Warn(row, "duplicate", churchId, row.Map.Type,
                            "the same person already has a record of this sacrament from another row; both were kept");

                    var recordId = DeterministicId($"sacrament|{row.Sheet}|{row.Number}|{row.Map.Type}");
                    InsertSacrament(connection, transaction, recordId, person.Id, row);
                    sacraments++;

                    Report.Add(Csv(row.Sheet, row.Number.ToString(CultureInfo.InvariantCulture), row.Map.Type, "info",
                        isNew ? "create-person" : "match-person", churchId ?? string.Empty, string.Empty,
                        churchId is null ? "matched or created by name and birth date" : "keyed on legacy PersonId"));
                }
                catch (Exception exception)
                {
                    _errors++;
                    var message = exception is SqliteException { SqliteErrorCode: 19 }
                        ? "this source row has already been imported into this database; import into an empty database"
                        : exception.Message;
                    Report.Add(Csv(row.Sheet, row.Number.ToString(CultureInfo.InvariantCulture), row.Map.Type, "error",
                        "skipped", Value(row, IdColumns) ?? string.Empty, string.Empty, message));
                }
            }

            // The write path runs even for a dry run so that key collisions and constraint violations
            // surface before the production import; the transaction is simply rolled back afterwards.
            var cleared = ClearRedundantPersonBaptismFields(connection, transaction);
            ReportUnmappedParishes();
            if (options.Commit && _errors == 0)
            {
                WriteAuditEvent(connection, transaction, rows.Count, created, sacraments);
                transaction.Commit();
            }
            else transaction.Rollback();

            File.WriteAllLines(options.Report, Report, new UTF8Encoding(true));
            Console.WriteLine($"""
                Rows read: {rows.Count}
                People created: {created}; people matched: {matched}
                Sacrament records {(options.Commit ? "written" : "planned")}: {sacraments}
                Parish spellings canonicalized from: {(options.Lookup is null ? "(no lookup supplied)" : $"{ParishSpellings.Count} mappings, {UnmappedParishes.Count} spellings unmapped")}
                Person-level baptism fields cleared (superseded by a baptism record): {cleared}
                Warnings: {_warnings}
                Errors: {_errors}
                Report: {Path.GetFullPath(options.Report)}
                {(options.Commit
                    ? _errors == 0 ? "Import committed." : "Import rolled back because errors were found."
                    : "Dry run only; no database changes were made.")}
                """);
            return _errors == 0 ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static List<SourceRow> ReadWorkbook(XLWorkbook workbook)
    {
        var rows = new List<SourceRow>();
        foreach (var sheet in workbook.Worksheets)
        {
            var map = MapFor(sheet.Name);
            if (map is null || sheet.FirstRowUsed() is null) continue;

            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in sheet.FirstRowUsed()!.CellsUsed())
            {
                var header = cell.GetString().Trim();
                if (header.Length > 0) headers.TryAdd(header, cell.Address.ColumnNumber);
            }
            if (!headers.Keys.Any(h => IdColumns.Contains(h, StringComparer.OrdinalIgnoreCase)) &&
                !headers.Keys.Any(h => FirstColumns.Contains(h, StringComparer.OrdinalIgnoreCase)))
                continue;

            RequireColumns(sheet.Name, headers, map);

            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (header, column) in headers) values[header] = CellText(row.Cell(column));
                var candidate = new SourceRow(sheet.Name, row.RowNumber(), map, values);
                if (Value(candidate, IdColumns) is null &&
                    Name(candidate, FirstColumns) is null &&
                    Name(candidate, LastColumns) is null)
                    continue;
                rows.Add(candidate);
            }
        }
        return rows;
    }

    /// <summary>Fails the run rather than writing NULLs when the workbook is missing a mapped column.</summary>
    private static void RequireColumns(string sheetName, Dictionary<string, int> headers, Mapping map)
    {
        var required = new (string Label, string[] Aliases)[]
        {
            ("sacrament date", map.Date), ("place", map.Place),
            ("first name", FirstColumns), ("surname", LastColumns), ("person id", IdColumns)
        };
        var missing = required
            .Where(r => !r.Aliases.Any(a => headers.ContainsKey(a)))
            .Select(r => $"{r.Label} ({string.Join(" / ", r.Aliases)})")
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Sheet '{sheetName}' is missing required column(s): {string.Join("; ", missing)}.");
    }

    private static Mapping? MapFor(string sheetName)
    {
        var name = Normalize(sheetName);
        if (name.Contains("BAPT")) return BaptismMap;
        if (name.Contains("CONFIRM")) return ConfirmationMap;
        if (name.Contains("COMMUN")) return FirstCommunionMap;
        return null;
    }

    private static void LoadParishSpellings(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Lookup workbook not found", path);
        using var book = new XLWorkbook(path);
        var sheet = book.Worksheets.FirstOrDefault(s => Normalize(s.Name) == "PARISHSPELLINGS")
            ?? throw new InvalidOperationException($"'{path}' has no ParishSpellings worksheet.");

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in sheet.FirstRowUsed()!.CellsUsed()) headers.TryAdd(cell.GetString().Trim(), cell.Address.ColumnNumber);
        if (!headers.TryGetValue("Church Spelling", out var from) || !headers.TryGetValue("Output Spelling", out var to))
            throw new InvalidOperationException($"'{path}' ParishSpellings needs 'Church Spelling' and 'Output Spelling' columns.");
        headers.TryGetValue("Occurrences", out var occurrences);

        var conflicts = new List<string>();
        var groups = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var spelling = CellText(row.Cell(from));
            var output = CellText(row.Cell(to));
            if (Clean(spelling) is null || Clean(output) is null) continue;
            if (ParishSpellings.TryGetValue(spelling, out var existing) && existing != output)
                conflicts.Add($"'{spelling}' maps to both '{existing}' and '{output}'");
            ParishSpellings[spelling] = output;

            var weight = occurrences > 0 && int.TryParse(CellText(row.Cell(occurrences)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
            var group = groups.TryGetValue(Normalize(spelling), out var bucket) ? bucket : groups[Normalize(spelling)] = [];
            group[output] = group.GetValueOrDefault(output) + weight;
        }
        if (conflicts.Count > 0)
            throw new InvalidOperationException($"ParishSpellings has conflicting mappings: {string.Join("; ", conflicts)}.");

        // Punctuation- and case-insensitive fallback. Where variants of one spelling map to outputs that
        // differ only in punctuation the most-used output wins; genuinely different outputs are reported.
        foreach (var (key, outputs) in groups)
        {
            if (outputs.Count == 1) { LooseParishSpellings[key] = outputs.Keys.First(); continue; }
            if (outputs.Keys.Select(Normalize).Distinct().Count() == 1)
            {
                LooseParishSpellings[key] = outputs.MaxBy(o => o.Value).Key;
                continue;
            }
            AmbiguousParishOutputs[key] = string.Join(" / ", outputs.Keys.Order());
        }
    }

    private static string? Canonical(string? place)
    {
        if (place is null || ParishSpellings.Count == 0) return place;
        if (ParishSpellings.TryGetValue(place, out var exact)) return exact;
        if (LooseParishSpellings.TryGetValue(Normalize(place), out var loose)) return loose;
        UnmappedParishes[place] = UnmappedParishes.GetValueOrDefault(place) + 1;
        return place;
    }

    /// <summary>One report line per unmapped spelling, so the rows can be added to the lookup in bulk.</summary>
    private static void ReportUnmappedParishes()
    {
        foreach (var (spelling, count) in UnmappedParishes.OrderByDescending(p => p.Value))
        {
            _warnings++;
            var ambiguous = AmbiguousParishOutputs.GetValueOrDefault(Normalize(spelling));
            Report.Add(Csv("(lookup)", string.Empty, string.Empty, "warning", "unmapped-parish", string.Empty, spelling,
                ambiguous is null
                    ? $"used {count} time(s); not in ParishSpellings, imported as written"
                    : $"used {count} time(s); ParishSpellings gives conflicting outputs ({ambiguous}), imported as written"));
        }
    }

    /// <summary>Preserves the cell's underlying value so dates never arrive as serial numbers or locale-formatted text.</summary>
    private static string CellText(IXLCell cell)
    {
        if (cell.DataType == XLDataType.DateTime && cell.TryGetValue<DateTime>(out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (cell.DataType == XLDataType.Boolean && cell.TryGetValue<bool>(out var flag))
            return flag ? "TRUE" : "FALSE";
        return RepairEncoding(cell.GetFormattedString().Trim());
    }

    /// <summary>Undoes the UTF-8-read-as-Latin-1 damage in the legacy remarks columns (e.g. "RecÂ´d").</summary>
    private static string RepairEncoding(string text)
    {
        if (!text.Contains('Â') && !text.Contains("â€")) return text;
        try
        {
            var repaired = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(text));
            return repaired.Contains('\uFFFD') ? text : repaired;
        }
        catch { return text; }
    }

    private static string? Value(SourceRow row, params string[] aliases)
    {
        foreach (var alias in aliases)
            if (row.Values.TryGetValue(alias, out var raw) && Clean(raw) is { } value)
                return value;
        return null;
    }

    /// <summary>Names are taken verbatim: the register itself records entries such as "Not Known" for a stillborn infant.</summary>
    private static string? Name(SourceRow row, params string[] aliases)
    {
        foreach (var alias in aliases)
            if (row.Values.TryGetValue(alias, out var raw) && !string.IsNullOrWhiteSpace(raw))
                return raw.Trim();
        return null;
    }

    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        return Placeholders.Contains(Normalize(value)) ? null : value;
    }

    private static DateOnly? DateField(SourceRow row, string[] aliases, string label)
    {
        var raw = Value(row, aliases);
        if (raw is null) return null;
        if (!TryParseDate(raw, out var value))
        {
            Warn(row, "unparsed-date", Value(row, IdColumns), label, $"could not read '{raw}' as a date; stored as blank");
            return null;
        }
        if (value.Year < EarliestPlausibleYear || value > DateOnly.FromDateTime(DateTime.Today))
            Warn(row, "implausible-date", Value(row, IdColumns), label, $"'{raw}' is outside {EarliestPlausibleYear}-today; imported as-is and needs review");
        return value;
    }

    private static bool TryParseDate(string text, out DateOnly value)
    {
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
        {
            value = DateOnly.FromDateTime(exact);
            return true;
        }
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            value = DateOnly.FromDateTime(parsed);
            return true;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is >= 1 and <= 2958465)
        {
            value = DateOnly.FromDateTime(DateTime.FromOADate(serial));
            return true;
        }
        value = default;
        return false;
    }

    private static KnownPerson? Resolve(
        SourceRow row,
        Dictionary<Guid, KnownPerson> people,
        Dictionary<string, Guid> byChurchId,
        Dictionary<string, List<Guid>> byNameKey,
        string? churchId,
        string given,
        string family,
        DateOnly? birth)
    {
        if (churchId is not null)
            return byChurchId.TryGetValue(churchId, out var id) ? people[id] : null;

        if (birth is null)
            throw new InvalidDataException("no PersonId and no birth date; the person cannot be identified safely");

        if (!byNameKey.TryGetValue(NameKey(given, family, birth), out var candidates) || candidates.Count == 0)
            return null;
        if (candidates.Count > 1)
            throw new InvalidDataException($"no PersonId and {candidates.Count} existing people share this name and birth date");

        Warn(row, "name-match", null, "PersonId", "no legacy PersonId; matched on name and birth date");
        return people[candidates[0]];
    }

    private static void Index(KnownPerson person, Dictionary<string, Guid> byChurchId, Dictionary<string, List<Guid>> byNameKey)
    {
        if (person.ChurchId is not null) byChurchId.TryAdd(person.ChurchId, person.Id);
        var key = NameKey($"{person.Given} {person.Middle}", person.Family, person.Birth);
        if (!byNameKey.TryGetValue(key, out var list)) byNameKey[key] = list = [];
        if (!list.Contains(person.Id)) list.Add(person.Id);
    }

    /// <summary>The legacy FirstName column holds every forename; the first token is the given name.</summary>
    private static (string First, string? Middle) SplitName(string name)
    {
        if (Clean(name) is null) return (name, null);
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length <= 1 ? (name, null) : (parts[0], string.Join(" ", parts.Skip(1)));
    }

    private static string NameKey(string given, string family, DateOnly? birth) =>
        $"{Normalize(given)}|{Normalize(family)}|{birth:yyyy-MM-dd}";

    private static Guid NewPersonId(string? churchId, string given, string family, DateOnly? birth) =>
        DeterministicId(churchId is not null ? $"church:{churchId}" : $"name:{NameKey(given, family, birth)}");

    private static Dictionary<Guid, KnownPerson> LoadPeople(SqliteConnection connection, SqliteTransaction transaction)
    {
        var people = new Dictionary<Guid, KnownPerson>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT RecordId,ChurchDbId,GivenName,MiddleName,FamilyName,BirthDate FROM People";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(0), out var id)) continue;
            DateOnly? birth = null;
            if (!reader.IsDBNull(5) && TryParseDate(reader.GetString(5), out var parsedBirth)) birth = parsedBirth;
            people[id] = new KnownPerson(
                id,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                birth);
        }
        return people;
    }

    private static void InsertPerson(SqliteConnection connection, SqliteTransaction transaction, KnownPerson person, SourceRow row)
    {
        // A baptism row supplies the baptism sacrament record instead, which is the system of record
        // for baptism details; leaving the person-level copy populated would duplicate (and diverge from) it.
        var isBaptismRow = row.Map.Type == Baptism;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO People(RecordId,GivenName,MiddleName,FamilyName,BirthDate,ChurchDbId,BirthCity," +
            "BaptismDate,BaptismChurch,Status,PdsMemberId,FlocknoteId,UpdatedUtc) " +
            "VALUES($id,$given,$middle,$family,$birth,$churchId,$city,$baptismDate,$baptismChurch,'historical',NULL,NULL,$updated)";
        Bind(command, "$id", person.Id.ToString());
        Bind(command, "$given", person.Given);
        Bind(command, "$middle", person.Middle);
        Bind(command, "$family", person.Family);
        Bind(command, "$birth", person.Birth?.ToString("yyyy-MM-dd"));
        Bind(command, "$churchId", person.ChurchId);
        Bind(command, "$city", Value(row, BirthCityColumns));
        Bind(command, "$baptismDate", isBaptismRow ? null : DateField(row, PersonBaptDateColumns, "BaptDate")?.ToString("yyyy-MM-dd"));
        Bind(command, "$baptismChurch", isBaptismRow ? null : Canonical(Value(row, PersonBaptPlaceColumns)));
        Bind(command, "$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void InsertSacrament(SqliteConnection connection, SqliteTransaction transaction, Guid recordId, Guid personId, SourceRow row)
    {
        var map = row.Map;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO Sacraments(RecordId,PersonRecordId,SacramentType,SacramentDate,Name,Place,Minister," +
            "FatherName,MotherName,SponsorOne,SponsorTwo,Volume,Page,Item,Details,CanonicalNotations,UpdatedUtc) " +
            "VALUES($id,$person,$type,$date,$name,$place,$minister,$father,$mother,$sponsor1,$sponsor2,$volume,$page,$item,$details,$notations,$updated)";
        Bind(command, "$id", recordId.ToString());
        Bind(command, "$person", personId.ToString());
        Bind(command, "$type", map.Type);
        Bind(command, "$date", DateField(row, map.Date, map.Date[0])?.ToString("yyyy-MM-dd"));
        Bind(command, "$name", Value(row, map.Name));
        Bind(command, "$place", Canonical(Value(row, map.Place)));
        Bind(command, "$minister", Value(row, map.Minister));
        Bind(command, "$father", Value(row, map.Father));
        Bind(command, "$mother", Value(row, map.Mother));
        Bind(command, "$sponsor1", Value(row, map.SponsorOne));
        Bind(command, "$sponsor2", Value(row, map.SponsorTwo));
        Bind(command, "$volume", Value(row, map.Volume));
        Bind(command, "$page", Value(row, map.Page));
        Bind(command, "$item", Value(row, map.Item));
        Bind(command, "$details", BuildDetails(row));
        Bind(command, "$notations", BuildNotations(row));
        Bind(command, "$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string? BuildDetails(SourceRow row)
    {
        var parts = new List<string>();
        foreach (var column in row.Map.Details)
            if (Value(row, column) is { } note) parts.Add(note);

        if (row.Map.Type == Baptism)
        {
            if (Value(row, "B-ProxyF") is { } proxyFather) parts.Add($"Proxy/witness (godfather): {proxyFather}");
            if (Value(row, "B-ProxyM") is { } proxyMother) parts.Add($"Proxy/witness (godmother): {proxyMother}");
        }
        else
        {
            var residence = Value(row, "F-Residence", "C-Residence");
            var city = Value(row, "F-City", "C-City");
            if (residence is not null || city is not null)
                parts.Add($"Residence: {string.Join(", ", new[] { residence, city }.Where(v => v is not null))}");
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    /// <summary>Marginal notations belong to the baptism record; the confirmation name is a Name, not a notation.</summary>
    private static string? BuildNotations(SourceRow row)
    {
        if (row.Map.Type != Baptism) return null;

        var parts = new List<string>();
        var spouse = Value(row, "Spouse");
        var eventDate = DateField(row, ["MarrDate"], "MarrDate");
        var eventChurch = Canonical(Value(row, "MarrChurch"));
        var deacon = IsTrue(Value(row, "Deaconate"));
        var professed = IsTrue(Value(row, "ReligProf"));
        var married = IsTrue(Value(row, "Marriage"));
        var detail = string.Join(" ", new[]
        {
            spouse,
            eventDate?.ToString("yyyy-MM-dd"),
            eventChurch is null ? null : $"at {eventChurch}"
        }.Where(v => v is not null));

        if (deacon) parts.Add("Ordained to the diaconate");
        if (professed) parts.Add("Religious profession");

        if (deacon || professed)
        {
            // On ordination and profession rows the parish reused the Spouse/MarrDate/MarrChurch columns
            // for the ordination details, so those values are reported without assuming they are a marriage.
            if (detail.Length > 0)
            {
                parts.Add($"Register note: {detail}");
                Warn(row, "reused-columns", Value(row, IdColumns), "Spouse/MarrDate/MarrChurch",
                    "ordination or profession row: the marriage columns hold the ordination details; verify the notation");
            }
        }
        else if (married || detail.Length > 0)
            parts.Add(detail.Length > 0 ? $"Marriage to {detail}" : "Marriage");

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private static bool IsTrue(string? value) =>
        value is not null && (value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
                              value == "1");

    /// <summary>Enforces the application invariant that a baptism record supersedes the person-level baptism fields.</summary>
    private static int ClearRedundantPersonBaptismFields(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE People SET BaptismDate=NULL,BaptismChurch=NULL WHERE (BaptismDate IS NOT NULL OR BaptismChurch IS NOT NULL) " +
            "AND EXISTS (SELECT 1 FROM Sacraments s WHERE s.PersonRecordId=People.RecordId AND s.SacramentType='baptism')";
        return command.ExecuteNonQuery();
    }

    private static void WriteAuditEvent(SqliteConnection connection, SqliteTransaction transaction, int rows, int people, int sacraments)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO AuditEvents(EventId,RecordId,User,Operation,OccurredUtc,Summary) VALUES($id,$record,$user,'import',$when,$summary)";
        Bind(command, "$id", Guid.NewGuid().ToString());
        Bind(command, "$record", Guid.Empty.ToString());
        Bind(command, "$user", Environment.UserName);
        Bind(command, "$when", DateTimeOffset.UtcNow.ToString("O"));
        Bind(command, "$summary", $"Legacy import: {rows} rows, {people} people created, {sacraments} sacrament records");
        command.ExecuteNonQuery();
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS People (RecordId TEXT PRIMARY KEY, GivenName TEXT NOT NULL, MiddleName TEXT, FamilyName TEXT NOT NULL, BirthDate TEXT, ChurchDbId TEXT, BirthCity TEXT, BaptismDate TEXT, BaptismChurch TEXT, Status TEXT NOT NULL, PdsMemberId TEXT, FlocknoteId TEXT, UpdatedUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Sacraments (RecordId TEXT PRIMARY KEY, PersonRecordId TEXT NOT NULL REFERENCES People(RecordId) ON DELETE CASCADE, SacramentType TEXT NOT NULL, SacramentDate TEXT, Name TEXT, Place TEXT, Minister TEXT, FatherName TEXT, MotherName TEXT, SponsorOne TEXT, SponsorTwo TEXT, Volume TEXT, Page TEXT, Item TEXT, Details TEXT, CanonicalNotations TEXT, UpdatedUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AuditEvents (EventId TEXT PRIMARY KEY, RecordId TEXT NOT NULL, User TEXT NOT NULL, Operation TEXT NOT NULL, OccurredUtc TEXT NOT NULL, Summary TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Sacraments_Person ON Sacraments(PersonRecordId);
            """;
        command.ExecuteNonQuery();
    }

    private static Options ParseArguments(string[] args)
    {
        const string usage = "Usage: --input <legacy-sacraments.xlsm> --database <MemberRecords.sqlite> [--lookup Lookup.xlsx] [--commit] [--report report.csv]";
        string? input = null, database = null, report = null, lookup = null;
        var commit = false;
        for (var n = 0; n < args.Length; n++)
        {
            switch (args[n].ToLowerInvariant())
            {
                case "--input": input = Next(args, ref n); break;
                case "--database": database = Next(args, ref n); break;
                case "--report": report = Next(args, ref n); break;
                case "--lookup": lookup = Next(args, ref n); break;
                case "--commit": commit = true; break;
                case "--help" or "-h" or "/?": Console.WriteLine(usage); Environment.Exit(0); break;
                default: throw new InvalidOperationException($"Unknown argument: {args[n]}");
            }
        }
        if (input is null || database is null) throw new InvalidOperationException(usage);
        return new Options(input, database, commit, report ?? "legacy-sacrament-import.csv", lookup);
    }

    private static string Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) throw new InvalidOperationException($"Missing value after {args[index]}.");
        return args[++index];
    }

    private static void Backup(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        if (File.Exists(full)) File.Copy(full, $"{full}.{DateTime.Now:yyyyMMdd-HHmmss}.bak", false);
    }

    private static void Warn(SourceRow row, string action, string? personId, string field, string message)
    {
        _warnings++;
        Report.Add(Csv(row.Sheet, row.Number.ToString(CultureInfo.InvariantCulture), row.Map.Type, "warning", action, personId ?? string.Empty, field, message));
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static Guid DeterministicId(string seed) => new(SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16]);

    private static string Csv(params string[] fields) =>
        string.Join(",", fields.Select(f => "\"" + f.Replace("\"", "\"\"") + "\""));
}
