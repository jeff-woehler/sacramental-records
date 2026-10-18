using System.Globalization;
using System.Security.Cryptography;

using System.Text;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private const string Baptism = "baptism";
    private const string Confirmation = "confirmation";
    private const string FirstCommunion = "first_communion";
    private const string Marriage = "marriage";


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

        private static readonly Mapping MarriageMap = new(
        Marriage,
        Date: ["WedDate", "WeddingDate", "MarriageDate"],
        Place: ["WedPlace", "WeddingPlace", "MarriagePlace"],
        Minister: ["Presider", "Minister"],
        Father: ["__Father"], Mother: ["__Mother"],
        SponsorOne: ["Witness1"], SponsorTwo: ["Witness2"],
        Name: ["__Spouse"], Volume: ["Volume"], Page: ["Page"],
        Item: ["Number", "Item"],
        Details: ["Remarks1", "Remarks2", "Misc1", "Misc2", "Chancery", "Bans", "MixRel", "DispCult", "FormMarr"]);

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
    private static readonly string[] IdColumns = ["PersonId", "Person ID", "Person Id", "ChurchDbId"];
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
        private sealed record KnownPerson(Guid Id, string? ChurchId, string Given, string? Middle, string Family, DateOnly? Birth, DateOnly? Baptism, string? Father, string? Mother);
        private sealed record SacramentEvidence(Guid PersonId, string Type, string? Volume, string? Page, string? Item);



    private const string ReportHeader = "Source,Sheet,Row,Type,Severity,Action,PersonId,Field,Message";
    private static readonly List<string> Report = [];
    private static readonly Dictionary<string, string> ParishSpellings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LooseParishSpellings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> AmbiguousParishOutputs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> UnmappedParishes = new(StringComparer.Ordinal);
    private static readonly Dictionary<Guid, (string? Father, string? Mother)> KnownParents = [];
    private static readonly Dictionary<Guid, HashSet<string>> KnownSacramentTypes = [];
    private static int _errors;
    private static int _warnings;

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
                        if (!File.Exists(options.Input)) throw new FileNotFoundException("Input workbook or wedding CSV not found", options.Input);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Database))!);
            if (options.Lookup is not null) LoadParishSpellings(options.Lookup);
            if (options.Commit) Backup(options.Database);
            if (IsPdsExport(options.Input)) return RunPds(options);
            if (IsFlocknoteMemberExport(options.Input)) return RunFlocknoteMembers(options);

            var isCsv = Path.GetExtension(options.Input).Equals(".csv", StringComparison.OrdinalIgnoreCase);
            var rows = isCsv
                ? ReadWeddingCsv(options.Input)
                : ReadWorkbook(new XLWorkbook(options.Input));

            if (rows.Count == 0) throw new InvalidOperationException("No sacramental worksheets were found.");
            BuildNameStatistics(rows);

            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(options.Database)}");
            connection.Open();
            Initialize(connection);
            using var transaction = connection.BeginTransaction();

                        var people = LoadPeople(connection, transaction);
                        LoadBaptismDates(connection, transaction, people);
            ReportDuplicatePeople(connection, transaction, people);
            LoadKnownParentsAndTypes(connection, transaction);
            var byChurchId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            var byNameKey = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
                        var byNameBaptism = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
            var byBirthBaptismFamily = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
            foreach (var person in people.Values) Index(person, byChurchId, byNameKey, byNameBaptism, byBirthBaptismFamily);



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

                    (given, family, var maiden) = GuessMaidenName(row, given, family);
                    var (firstName, middleName) = SplitName(given);
                    var churchId = Value(row, IdColumns);
                    var birth = DateField(row, BirthColumns, "BirthDate");
                                        var baptism = DateField(row, PersonBaptDateColumns, "BaptDate");
                                        var person = Resolve(row, people, byChurchId, byNameKey, byNameBaptism, byBirthBaptismFamily, churchId, given, family, birth, baptism);
                    // A bride's baptism is registered under her maiden name.
                    if (person is null && churchId is null && maiden is not null)
                        person = Resolve(row, people, byChurchId, byNameKey, byNameBaptism, byBirthBaptismFamily, churchId, given, maiden, birth, baptism);


                    var isNew = person is null;
                    if (person is null)
                    {
                                                person = new KnownPerson(NewPersonId(churchId, given, family, birth, baptism, row), churchId, firstName, middleName, family, birth, baptism, null, null);


                        InsertPerson(connection, transaction, person, row, maiden);
                        people[person.Id] = person;
                                                Index(person, byChurchId, byNameKey, byNameBaptism, byBirthBaptismFamily);


                        created++;
                    }
                    else
                    {
                        matched++;
                        if (maiden is not null) FillMaidenName(connection, transaction, person.Id, maiden);
                    }

                                        if (!seenSacraments.Add((person.Id, row.Map.Type)))
                        Warn(row, "duplicate", churchId, row.Map.Type,
                            $"this row resolved to {PersonIdentity(person)}, which already has a {row.Map.Type} record from another source row; both were kept. ACTION: review the two source rows and retain only the correct sacrament entry if they are duplicates");

                                        WarnSuspiciousMarriage(row, person, birth, baptism);
                    var recordId = DeterministicId($"sacrament|{row.Sheet}|{row.Number}|{row.Map.Type}|{row.Values.GetValueOrDefault("__WeddingSide", "single")}");

                    InsertSacrament(connection, transaction, recordId, person.Id, row);
                    RememberSacrament(person.Id, row);
                    sacraments++;

                    Report.Add(Csv(row.Sheet, row.Number.ToString(CultureInfo.InvariantCulture), row.Map.Type, "info",
                        isNew ? "create-person" : "match-person", churchId ?? string.Empty, string.Empty,
                                                churchId is null
                            ? row.Map.Type == Marriage ? "matched or created using name and baptism date where available" : "matched or created by name and birth date"
                            : "keyed on legacy PersonId"));

                }
                catch (Exception exception)
                {
                    _errors++;
                                        var message = exception is SqliteException { SqliteErrorCode: 19 }
                        ? "this source row has already been imported into this database; import into an empty database"
                        : exception.Message;
                                        if (row.Map.Type == Marriage && !message.StartsWith(WeddingSubject(row) + ":", StringComparison.Ordinal))
                        message = $"{WeddingSubject(row)}: {message}";


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

            WriteReport(options.Report, isCsv ? "weddings" : "legacy-sacraments");
            Console.WriteLine($"""
                Rows read: {rows.Count}
                People created: {created}; people matched: {matched}
                Entries {(options.Commit ? "written" : "planned")}: {sacraments}
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

        private static List<SourceRow> ReadWeddingCsv(string path)
    {
        var lines = File.ReadLines(path).Where(line => line.Length > 0).ToList();
        if (lines.Count == 0) return [];
        var headers = ParseCsvLine(lines[0]);
        var rows = new List<SourceRow>();
        for (var n = 1; n < lines.Count; n++)
        {
            var fields = ParseCsvLine(lines[n]);
            var values = headers.Select((header, i) => (header, value: i < fields.Count ? fields[i] : string.Empty))
                .ToDictionary(p => p.header, p => p.value, StringComparer.OrdinalIgnoreCase);
            AddWeddingSide(rows, values, n + 1, "G", "FirstNameG", "SurnameG", "FatherG", "MotherG", "BaptDateG", "BaptChurchG", "FirstNameB", "SurnameB");
            AddWeddingSide(rows, values, n + 1, "B", "FirstNameB", "SurnameB", "FatherB", "MotherB", "BaptDateB", "BaptChurchB", "FirstNameG", "SurnameG");
        }
        return rows;
    }

    private static void AddWeddingSide(List<SourceRow> rows, Dictionary<string, string> source, int number, string side,
        string first, string surname, string father, string mother, string baptismDate, string baptismChurch,
        string spouseFirst, string spouseSurname)
    {
        var values = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = source.GetValueOrDefault(first, ""), ["Surname"] = source.GetValueOrDefault(surname, ""),
            ["BaptDate"] = source.GetValueOrDefault(baptismDate, ""), ["BaptChurch"] = source.GetValueOrDefault(baptismChurch, ""),
            ["__Father"] = source.GetValueOrDefault(father, ""), ["__Mother"] = source.GetValueOrDefault(mother, ""),
            ["__Spouse"] = $"{source.GetValueOrDefault(spouseFirst, "").Trim()} {source.GetValueOrDefault(spouseSurname, "").Trim()}".Trim(),
            ["__WeddingSide"] = side
        };
        rows.Add(new SourceRow("Weddings", number, MarriageMap, values));
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { result.Add(RepairEncoding(value.ToString().Trim())); value.Clear(); }
            else value.Append(c);
        }
        result.Add(RepairEncoding(value.ToString().Trim()));
        return result;
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
                        if (!headers.Keys.Any(h => HasAlias(h, IdColumns)) &&
                !headers.Keys.Any(h => HasAlias(h, FirstColumns)))
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
            .Where(r => !headers.Keys.Any(h => HasAlias(h, r.Aliases)))
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
        if (name.Contains("WEDD") || name.Contains("MARR")) return MarriageMap;
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

        // Legacy headers vary in spacing and punctuation (for example, Person Id,
        // Person-ID, and PersonID). Use the normalized header as a fallback so a
        // visually correct ID column cannot silently become a name match.
        foreach (var (header, raw) in row.Values)
            if (aliases.Any(alias => Normalize(header) == Normalize(alias)) && Clean(raw) is { } value)
                return value;
        return null;
    }

    private static bool HasAlias(string header, IEnumerable<string> aliases) =>
        aliases.Any(alias => Normalize(header) == Normalize(alias));

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
        // A bare year would otherwise fall through to the OLE serial branch ("1979" -> 1905-06-01).
        if (text.Length == 4 && text.All(char.IsDigit))
        {
            value = default;
            return false;
        }
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
                Dictionary<string, List<Guid>> byNameBaptism,
        Dictionary<string, List<Guid>> byBirthBaptismFamily,
        string? churchId,

        string given,
        string family,
        DateOnly? birth,
        DateOnly? baptism)

    {
                if (churchId is not null && byChurchId.TryGetValue(churchId, out var id))
            return people[id];

                // Legacy PersonId values are not globally reliable: the workbook contains the same
        // person under different IDs (and occasional spelling variants). Dates and surname alone
        // are not safe enough, however: twins can share all of them. Require the normalized full
        // name as well before reconciling an unknown legacy ID.
        if (churchId is not null && birth is not null && baptism is not null)
        {
            var legacyCandidates = byBirthBaptismFamily.GetValueOrDefault(BirthBaptismFamilyKey(family, birth.Value, baptism.Value), [])
                .Where(candidateId => NameKeyNoDate(given, family) ==
                    NameKeyNoDate($"{people[candidateId].Given} {people[candidateId].Middle}", people[candidateId].Family))
                .ToList();
            if (legacyCandidates.Count == 1)
            {
                var matchedPerson = people[legacyCandidates[0]];
                Warn(row, "legacy-id-reconciled", churchId, "PersonId",
                    $"source legacy PersonId {churchId} did not match an existing PersonId, but was reconciled to {PersonIdentity(matchedPerson)} using full name, surname, birth date, and baptism date. ACTION: verify that {churchId} and {matchedPerson.ChurchId ?? "(no legacy ID)"} refer to the same person; no new person was created");
                return matchedPerson;
            }
                }

        // Same person registered under different legacy IDs for different sacraments. Require full name +
        // (birth or baptism date) + both parents to agree, a single candidate, and that the candidate has
        // no record of this sacrament type yet; twins differ by name and are never merged.
        if (churchId is not null && (birth is not null || baptism is not null))
        {
            var father = Value(row, row.Map.Father);
            var mother = Value(row, row.Map.Mother);
            if (father is not null && mother is not null)
            {
                // A candidate may lack the birth date (or baptism date) the row has; one agreeing date is
                // enough as long as neither side contradicts the other.
                var pool = byNameBaptism.GetValueOrDefault(NameKeyNoDate(given, family), [])
                    .Where(candidateId =>
                        (birth is null || people[candidateId].Birth is null || people[candidateId].Birth == birth) &&
                        (baptism is null || people[candidateId].Baptism is null || people[candidateId].Baptism == baptism) &&
                        (birth is not null && people[candidateId].Birth == birth || baptism is not null && people[candidateId].Baptism == baptism));
                var sameParents = pool
                    .Where(candidateId =>
                        people[candidateId].ChurchId != churchId &&
                        !(KnownSacramentTypes.TryGetValue(candidateId, out var types) && types.Contains(row.Map.Type)) &&
                        KnownParents.TryGetValue(candidateId, out var parents) &&
                        parents.Father is not null && parents.Mother is not null &&
                        SameParent(parents.Father, father) && SameParent(parents.Mother, mother))
                    .ToList();
                if (sameParents.Count == 1)
                {
                    var matchedPerson = people[sameParents[0]];
                    Warn(row, "legacy-id-reconciled", churchId, "PersonId",
                        $"source legacy PersonId {churchId} did not match an existing PersonId, but was reconciled to {PersonIdentity(matchedPerson)} using full name, birth/baptism date, and both parents' names. ACTION: verify that {churchId} and {matchedPerson.ChurchId ?? "(no legacy ID)"} refer to the same person; no new person was created");
                    return matchedPerson;
                }
            }
        }

        // An unrecognized legacy ID must not fall through to name/date matching. Doing so can
        // attach a twin's sacrament to the first person encountered with the same dates and name.
        // Preserve the source ID by creating a separate person instead.
        if (churchId is not null) return null;

        var candidates = new List<Guid>();

        if (baptism is not null)
            candidates = byNameBaptism.GetValueOrDefault(NameKeyNoDate(given, family), [])
                .Where(id => people[id].Baptism == baptism).ToList();
        else if (birth is not null)
            candidates = byNameKey.GetValueOrDefault(NameKey(given, family, birth), []);
                else if (row.Map.Type == Marriage)
        {
            candidates = byNameBaptism.GetValueOrDefault(NameKeyNoDate(given, family), []);
            if (candidates.Count == 1)
                Warn(row, "weak-name-match", null, "PersonId", "marriage row has no birth or baptism date; matched on unique normalized name only");
        }
        else throw new InvalidDataException("no PersonId and no birth date; the person cannot be identified safely");

        if (candidates.Count == 0) return null;
        if (row.Map.Type == Marriage)
                        candidates = FilterMarriageParentCandidates(row, candidates, people);
            candidates = FilterMarriageCandidates(row, candidates, people);

        if (candidates.Count == 0) return null;

                if (candidates.Count > 1)

        {
            var criterion = baptism is not null ? $"name and baptism date {baptism:yyyy-MM-dd}" :
                birth is not null ? $"name and birth date {birth:yyyy-MM-dd}" : "normalized name";
                        var candidateDetails = string.Join("; ", candidates.Select(candidateId =>
            {
                var candidate = people[candidateId];
                return $"{candidate.Id} (birth {candidate.Birth:yyyy-MM-dd}, baptism {candidate.Baptism:yyyy-MM-dd}, legacy id {candidate.ChurchId ?? "none"})";
            }));
            throw new InvalidDataException($"{WeddingSubject(row)}: no PersonId and {candidates.Count} existing people share this {criterion}; candidates: {candidateDetails}");

        }


                Warn(row, "name-match", null, "PersonId", baptism is null
            ? "no legacy PersonId; matched on name and birth date"
            : "no legacy PersonId; matched on normalized name and baptism date");

        return people[candidates[0]];
    }

        private static void Index(KnownPerson person, Dictionary<string, Guid> byChurchId, Dictionary<string, List<Guid>> byNameKey,
            Dictionary<string, List<Guid>> byNameBaptism, Dictionary<string, List<Guid>> byBirthBaptismFamily)


    {
        if (person.ChurchId is not null) byChurchId.TryAdd(person.ChurchId, person.Id);
        var key = NameKey($"{person.Given} {person.Middle}", person.Family, person.Birth);
                if (!byNameKey.TryGetValue(key, out var list)) byNameKey[key] = list = [];
        if (!list.Contains(person.Id)) list.Add(person.Id);
        var name = NameKeyNoDate($"{person.Given} {person.Middle}", person.Family);
                if (!byNameBaptism.TryGetValue(name, out var baptismList)) byNameBaptism[name] = baptismList = [];
        if (!baptismList.Contains(person.Id)) baptismList.Add(person.Id);
        if (person.Birth is not null && person.Baptism is not null)
        {
            var birthBaptismKey = BirthBaptismFamilyKey(person.Family, person.Birth.Value, person.Baptism.Value);
            if (!byBirthBaptismFamily.TryGetValue(birthBaptismKey, out var familyList)) byBirthBaptismFamily[birthBaptismKey] = familyList = [];
            if (!familyList.Contains(person.Id)) familyList.Add(person.Id);
        }



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

        private static string NameKeyNoDate(string given, string family) =>
        $"{Normalize(given)}|{Normalize(family)}";

    private static string BirthBaptismFamilyKey(string family, DateOnly birth, DateOnly baptism) =>
        $"{Normalize(family)}|{birth:yyyy-MM-dd}|{baptism:yyyy-MM-dd}";




        private static Guid NewPersonId(string? churchId, string given, string family, DateOnly? birth, DateOnly? baptism, SourceRow row) =>
        DeterministicId(churchId is not null
            ? $"church:{churchId}"
            : birth is not null ? $"name:{NameKey(given, family, birth)}"
            : baptism is not null ? $"name-baptism:{NameKeyNoDate(given, family)}|{baptism:yyyy-MM-dd}"
            : $"name-row:{row.Sheet}|{row.Number}|{row.Values.GetValueOrDefault("__WeddingSide", "single")}");


    private static Dictionary<Guid, KnownPerson> LoadPeople(SqliteConnection connection, SqliteTransaction transaction)
    {
        var people = new Dictionary<Guid, KnownPerson>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
                command.CommandText = "SELECT RecordId,ChurchDbId,GivenName,MiddleName,FamilyName,BirthDate,BaptismDate FROM People";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(0), out var id)) continue;
            DateOnly? birth = null;
                        DateOnly? baptism = null;
            if (!reader.IsDBNull(5) && TryParseDate(reader.GetString(5), out var parsedBirth)) birth = parsedBirth;
            if (!reader.IsDBNull(6) && TryParseDate(reader.GetString(6), out var parsedBaptism)) baptism = parsedBaptism;
            people[id] = new KnownPerson(

                id,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                                reader.GetString(4),
                birth, baptism, null, null);



        }
                return people;
    }

        private static void ReportDuplicatePeople(SqliteConnection connection, SqliteTransaction transaction, Dictionary<Guid, KnownPerson> people)
        {
            // This is deliberately a report-only pass. It never merges records automatically. Matching
            // family, dates, parents, and similar names is only a candidate signal: twins commonly share
            // all of those values. Existing sacrament evidence is therefore checked before warning.
            var evidence = LoadSacramentEvidence(connection, transaction);
            var groups = people.Values
            .Where(p => p.Birth is not null && p.Baptism is not null && p.Father is not null && p.Mother is not null)
            .GroupBy(p => $"{Normalize(p.Family)}|{p.Birth:yyyy-MM-dd}|{p.Baptism:yyyy-MM-dd}|{Normalize(p.Father!)}|{Normalize(p.Mother!)}");
        foreach (var group in groups)
        {
            var members = group.ToList();
            for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                var left = Normalize($"{members[i].Given} {members[i].Middle}");
                var right = Normalize($"{members[j].Given} {members[j].Middle}");
                                if ((left == right || Levenshtein(left, right) <= 1) &&
                    DistinctSacramentEvidence(members[i].Id, members[j].Id, evidence) is null)
                {
                    _warnings++;
                    Report.Add(Csv("(preflight)", string.Empty, string.Empty, "warning", "duplicate-person", members[i].Id.ToString(),
                        members[j].Id.ToString(),
                        $"possible duplicate ({Confidence(left, right)} confidence): {PersonSummary(members[i])} / {PersonSummary(members[j])}; same family, birth/baptism dates, and parents; no conflicting sacrament register evidence was found; review before merging"));
                }
            }
        }
    }

        private static string PersonSummary(KnownPerson person) =>
        $"{person.Given} {person.Middle} {person.Family} [birth {person.Birth:yyyy-MM-dd}, baptism {person.Baptism:yyyy-MM-dd}, legacy {person.ChurchId ?? "none"}]";

        private static string PersonIdentity(KnownPerson person) =>
        $"existing person legacy PersonId {person.ChurchId ?? "(none)"} (RecordId {person.Id})";

    private static Dictionary<Guid, List<SacramentEvidence>> LoadSacramentEvidence(SqliteConnection connection, SqliteTransaction transaction)
    {
        var evidence = new Dictionary<Guid, List<SacramentEvidence>>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT PersonRecordId,SacramentType,Volume,Page,Item FROM Sacraments";
        using var reader = command.ExecuteReader();
        while (reader.Read() && Guid.TryParse(reader.GetString(0), out var personId))
        {
            var item = new SacramentEvidence(
                personId,
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
            evidence.TryGetValue(personId, out var records);
            (records ??= evidence[personId] = []).Add(item);
        }
        return evidence;
    }

    private static string? DistinctSacramentEvidence(Guid leftId, Guid rightId, Dictionary<Guid, List<SacramentEvidence>> evidence)
    {
        var left = evidence.GetValueOrDefault(leftId, []);
        var right = evidence.GetValueOrDefault(rightId, []);

        // A confirmation for each person is strong evidence that these are two distinct people.
        if (left.Any(r => r.Type == Confirmation) && right.Any(r => r.Type == Confirmation))
            return "both people have confirmation records";

        // Different register references identify separate entries even when the people have the
        // same family, dates, parents, and nearly identical names. Ignore comparisons where all
        // three fields are blank because those records provide no distinguishing evidence.
        foreach (var leftRecord in left)
        foreach (var rightRecord in right.Where(r => r.Type == leftRecord.Type))
        {
            var leftReference = (leftRecord.Volume, leftRecord.Page, leftRecord.Item);
            var rightReference = (rightRecord.Volume, rightRecord.Page, rightRecord.Item);
            if (leftReference != (null, null, null) && rightReference != (null, null, null) && leftReference != rightReference)
                return $"different {leftRecord.Type} register Volume/Page/Item values";
        }
        return null;
    }

    private static string Confidence(string left, string right) => left == right ? "very-high" : "high";

    private static int Levenshtein(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[right.Length];
    }

    private static void LoadKnownParentsAndTypes(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT PersonRecordId,SacramentType,FatherName,MotherName FROM Sacraments";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(0), out var id)) continue;
            if (!KnownSacramentTypes.TryGetValue(id, out var types)) KnownSacramentTypes[id] = types = [];
            types.Add(reader.GetString(1));
            RememberParents(id, reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        }
    }

    // Registers spell parents differently (middle initials, married surnames); accept when one name's tokens are within the other's.
    private static bool SameParent(string left, string right)
    {
        if (Normalize(left) == Normalize(right)) return true;
        static HashSet<string> Tokens(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        var (a, b) = (Tokens(left), Tokens(right));
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        if (small.Count >= 2 && small.IsSubsetOf(large)) return true;
        // Nicknames (Dave / David): same surname and same first initial.
        var (l, r) = (Normalize(left.Trim().Split(' ')[0]), Normalize(right.Trim().Split(' ')[0]));
        var (ll, rl) = (Normalize(left.Trim().Split(' ')[^1]), Normalize(right.Trim().Split(' ')[^1]));
        return l.Length > 0 && r.Length > 0 && l[0] == r[0] && ll == rl && ll.Length > 0;
    }

    private static void RememberParents(Guid id, string? father, string? mother)
    {
        var current = KnownParents.GetValueOrDefault(id);
        KnownParents[id] = (current.Father ?? father, current.Mother ?? mother);
    }

    private static void RememberSacrament(Guid id, SourceRow row)
    {
        if (!KnownSacramentTypes.TryGetValue(id, out var types)) KnownSacramentTypes[id] = types = [];
        types.Add(row.Map.Type);
        RememberParents(id, Value(row, row.Map.Father), Value(row, row.Map.Mother));
    }

    private static void LoadBaptismDates(SqliteConnection connection, SqliteTransaction transaction, Dictionary<Guid, KnownPerson> people)

    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
                command.CommandText = "SELECT PersonRecordId,SacramentDate,FatherName,MotherName FROM Sacraments WHERE SacramentType='baptism' AND SacramentDate IS NOT NULL";

        using var reader = command.ExecuteReader();
                while (reader.Read() && Guid.TryParse(reader.GetString(0), out var id) && people.TryGetValue(id, out var person) && TryParseDate(reader.GetString(1), out var date))
        {
            var father = reader.IsDBNull(2) ? null : reader.GetString(2);
            var mother = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (person.Baptism is null || person.Father is null || person.Mother is null)
                people[id] = person with
                {
                    Baptism = person.Baptism ?? date,
                    Father = person.Father ?? father,
                    Mother = person.Mother ?? mother
                };
        }

    }

                private static List<Guid> FilterMarriageParentCandidates(SourceRow row, List<Guid> candidates, Dictionary<Guid, KnownPerson> people)
        {
            var sourceFather = Value(row, row.Map.Father);
            var sourceMother = Value(row, row.Map.Mother);
            if (sourceFather is null && sourceMother is null) return candidates;

                        var matches = candidates.Where(id =>
                (sourceFather is null || people[id].Father is not null && Normalize(people[id].Father!) == Normalize(sourceFather)) &&
                (sourceMother is null || people[id].Mother is not null && Normalize(people[id].Mother!) == Normalize(sourceMother))).ToList();

            if (matches.Count == 0 || matches.Count == candidates.Count) return candidates;

            Warn(row, "parent-filtered-match", null, "PersonId",
                $"parent names narrowed {candidates.Count} possible people to {matches.Count}; source parents: {sourceFather ?? "(blank)"} / {sourceMother ?? "(blank)"}");
            return matches;
        }

        private static List<Guid> FilterMarriageCandidates(SourceRow row, List<Guid> candidates, Dictionary<Guid, KnownPerson> people)

        {
            var wedding = DateField(row, row.Map.Date, "WedDate");
            if (wedding is null) return candidates;
            var rejected = new List<(Guid Id, string Reason)>();
            var survivors = new List<Guid>();
            foreach (var id in candidates)
            {
                var person = people[id];
                if (person.Birth is { } birth)
                {
                    var age = wedding.Value.Year - birth.Year - (wedding.Value < birth.AddYears(wedding.Value.Year - birth.Year) ? 1 : 0);
                    if (birth > wedding) { rejected.Add((id, $"birth date {birth:yyyy-MM-dd} is after wedding")); continue; }
                    if (age < 10) { rejected.Add((id, $"age would be {age}, below 10")); continue; }
                    if (age > 100) { rejected.Add((id, $"age would be {age}, above 100")); continue; }
                }
                if (person.Baptism is { } baptism && baptism > wedding)
                {
                    rejected.Add((id, $"baptism date {baptism:yyyy-MM-dd} is after wedding"));
                    continue;
                }
                survivors.Add(id);
            }
            if (rejected.Count > 0)
                Warn(row, "date-filtered-candidate", null, "PersonId",
                    $"rejected {rejected.Count} of {candidates.Count} possible people: {string.Join("; ", rejected.Select(r => r.Reason))}");
            if (rejected.Count > 0 && survivors.Count == 1)
                Warn(row, "date-filtered-match", null, "PersonId", "selected the only candidate with a plausible birth/baptism chronology");
            return survivors;
        }

        private static string WeddingSubject(SourceRow row)

        {
            if (row.Map.Type != Marriage) return "source row";
            var side = row.Values.GetValueOrDefault("__WeddingSide") switch
            {
                "G" => "groom",
                "B" => "bride",
                _ => "marriage party"
            };
            return $"{side} '{Name(row, FirstColumns)} {Name(row, LastColumns)}'";
        }

        private static void WarnSuspiciousMarriage(SourceRow row, KnownPerson person, DateOnly? birth, DateOnly? baptism)

    {
        if (row.Map.Type != Marriage) return;
        var wedding = DateField(row, row.Map.Date, "WedDate");
        if (wedding is null)
            Warn(row, "missing-marriage-date", person.ChurchId, "WedDate", "marriage record has no usable wedding date");
        if (Value(row, row.Map.Place) is null)
            Warn(row, "missing-marriage-place", person.ChurchId, "WedPlace", "marriage record has no usable place");
        if (Value(row, row.Map.SponsorOne) is null && Value(row, row.Map.SponsorTwo) is null)
            Warn(row, "missing-witnesses", person.ChurchId, "Witness1/Witness2", "marriage record has no witnesses recorded");

        if (wedding is not null && baptism is not null && baptism > wedding)
            Warn(row, "suspicious-date-order", person.ChurchId, "BaptDate/WedDate", $"baptism date {baptism:yyyy-MM-dd} is after wedding date {wedding:yyyy-MM-dd}");
        if (baptism is null)
            Warn(row, "missing-baptism-date", person.ChurchId, "BaptDate", "marriage person has no baptism date; identity match is less reliable");
        if (birth is not null && wedding is not null && birth > wedding)
            Warn(row, "suspicious-date-order", person.ChurchId, "BirthDate/WedDate", $"birth date {birth:yyyy-MM-dd} is after wedding date {wedding:yyyy-MM-dd}");
    }

    private static void InsertPerson(SqliteConnection connection, SqliteTransaction transaction, KnownPerson person, SourceRow row, string? maiden)
    {
        // A baptism row supplies the baptism sacrament record instead, which is the system of record
        // for baptism details; leaving the person-level copy populated would duplicate (and diverge from) it.
        var isBaptismRow = row.Map.Type == Baptism;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO People(RecordId,GivenName,MiddleName,FamilyName,BirthDate,ChurchDbId,BirthCity," +
            "BaptismDate,BaptismChurch,Status,PdsMemberId,FlocknoteId,UpdatedUtc,MaidenName) " +
            "VALUES($id,$given,$middle,$family,$birth,$churchId,$city,$baptismDate,$baptismChurch,'historical',NULL,NULL,$updated,$maiden)";
        Bind(command, "$maiden", maiden);
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
        else if (row.Map.Type == Marriage)
        {
            var side = row.Values.GetValueOrDefault("__WeddingSide", "G");
            var residence = Value(row, side == "G" ? "ResidenceG" : "ResidenceB");
            var city = Value(row, side == "G" ? "CityG" : "CityB");
            if (residence is not null || city is not null)
                parts.Add($"Residence: {string.Join(", ", new[] { residence, city }.Where(v => v is not null))}");
            foreach (var column in new[] { "Chancery", "Bans", "MixRel", "DispCult", "FormMarr" })
                if (Value(row, column) is { } flag && !flag.Equals("FALSE", StringComparison.OrdinalIgnoreCase) && flag != "0")
                    parts.Add($"{column}: {flag}");
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

    private static void WriteAuditEvent(SqliteConnection connection, SqliteTransaction transaction, int rows, int people, int sacraments, string? summary = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO AuditEvents(EventId,RecordId,User,Operation,OccurredUtc,Summary) VALUES($id,$record,$user,'import',$when,$summary)";
        Bind(command, "$id", Guid.NewGuid().ToString());
        Bind(command, "$record", Guid.Empty.ToString());
        Bind(command, "$user", Environment.UserName);
        Bind(command, "$when", DateTimeOffset.UtcNow.ToString("O"));
        Bind(command, "$summary", summary ?? $"Legacy import: {rows} rows, {people} people created, {sacraments} entries");
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
        foreach (var column in new[] { "MaidenName", "DeceasedDate" })
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('People') WHERE name=$column";
            Bind(check, "$column", column);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE People ADD COLUMN {column} TEXT";
            alter.ExecuteNonQuery();
        }
    }

    private static Options ParseArguments(string[] args)
    {
                const string usage = "Usage: --input <legacy-sacraments.xlsm|Weddings.csv|PDS-export.csv|Flocknote Member.xlsx> --database <MemberRecords.sqlite> [--lookup Lookup.xlsx] [--commit] [--report report.csv]";

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
        return new Options(input, database, commit, report ?? "import-audit.csv", lookup);
    }

    /// <summary>Replaces this source's rows in the shared report and keeps the rows from the other sources.</summary>
    private static void WriteReport(string path, string source)
    {
        var prefix = Csv(source) + ",";
        var kept = new List<string>();
        if (File.Exists(path))
        {
            var existing = File.ReadAllLines(path);
            if (existing.Length > 0 && existing[0].Replace("\"", string.Empty) != ReportHeader)
                throw new InvalidOperationException($"'{path}' is not an import report (unexpected header); choose another --report file.");
            kept.AddRange(existing.Skip(1).Where(line => !line.StartsWith(prefix, StringComparison.Ordinal)));
        }
        File.WriteAllLines(path, kept.Prepend(ReportHeader).Concat(Report.Select(line => prefix + line)), new UTF8Encoding(true));
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
