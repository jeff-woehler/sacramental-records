using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static readonly (string Type, string Prefix)[] PdsSacramentPrefixes =
    [
        (Baptism, "Baptism"), (FirstCommunion, "1st Comm"), (Confirmation, "Confirm"), (Marriage, "Marriage")
    ];

    private static readonly string[] SacramentColumns =
        ["SacramentDate", "Name", "Place", "Minister", "SponsorOne", "SponsorTwo", "Volume", "Page", "Item", "Details"];

    private static readonly Regex TitlePrefix = new(
        @"^(?:(?:REV|FR|FATHER|DEACON|DCN|SR|SISTER|BR|BROTHER|MSGR|DR|MR|MRS|MS|MISS)\.?\s+)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Parenthesized = new(@"\(([^)]*)\)", RegexOptions.CultureInvariant);
    private static readonly Regex SpousePrefix = new(@"^\s*(?:SPOUSE|TO)\s*[:;]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record PdsSacrament(string Type, DateOnly? Date, Dictionary<string, string?> Fields);

    private sealed record PdsPerson(
        int Row, string PdsId, string Given, string? Middle, string Family, string? Maiden, DateOnly? Birth,
        DateOnly? Deceased, string Status, HashSet<string> FirstNames, HashSet<string> Surnames, List<PdsSacrament> Sacraments)
    {
        public DateOnly? BaptismDate => Sacraments.FirstOrDefault(s => s.Type == Baptism)?.Date;
        public string Display => $"{Given} {Middle} {Family}".Replace("  ", " ");
    }

    private sealed class DbPerson
    {
        public required Guid Id { get; init; }
        public required string Given { get; init; }
        public string? Middle { get; init; }
        public required string Family { get; init; }
        public string? ChurchId { get; init; }
        public DateOnly? Birth { get; set; }
        public DateOnly? Baptism { get; set; }
        public string? Pds { get; set; }
        public string? Maiden { get; set; }
        public DateOnly? Deceased { get; set; }
        public string Describe => $"{Given} {Middle} {Family} (legacy {ChurchId ?? "none"}, birth {Birth:yyyy-MM-dd}, baptism {Baptism:yyyy-MM-dd}, RecordId {Id})";
    }

    private sealed class DbSacrament
    {
        public required Guid Id { get; init; }
        public required Guid PersonId { get; init; }
        public required string Type { get; init; }
        public Dictionary<string, string?> Fields { get; } = new(StringComparer.Ordinal);
        public DateOnly? Date => Fields["SacramentDate"] is { } raw && TryParseDate(raw, out var d) ? d : null;
    }

    private sealed class PdsState
    {
        public Dictionary<Guid, DbPerson> People { get; } = [];
        public Dictionary<string, DbPerson> ByPds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<DateOnly, List<DbPerson>> ByBirth { get; } = [];
        public Dictionary<Guid, List<DbSacrament>> SacramentsByPerson { get; } = [];
        public Dictionary<string, List<DbSacrament>> ByTypePage { get; } = new(StringComparer.Ordinal);
        public Dictionary<DateOnly, List<DbSacrament>> MarriagesByDate { get; } = [];
        public long LastChurchId { get; set; }

        public void Add(DbPerson person)
        {
            People[person.Id] = person;
            if (person.Pds is not null) ByPds[person.Pds] = person;
            if (person.Birth is { } birth) (ByBirth.TryGetValue(birth, out var list) ? list : ByBirth[birth] = []).Add(person);
        }

        public void Add(DbSacrament record)
        {
            (SacramentsByPerson.TryGetValue(record.PersonId, out var list) ? list : SacramentsByPerson[record.PersonId] = []).Add(record);
            if (NumKey(record.Fields["Page"]) is { } page)
            {
                var key = $"{record.Type}|{page}";
                (ByTypePage.TryGetValue(key, out var pageList) ? pageList : ByTypePage[key] = []).Add(record);
            }
            if (record.Type == Marriage && record.Date is { } date)
                (MarriagesByDate.TryGetValue(date, out var dateList) ? dateList : MarriagesByDate[date] = []).Add(record);
            if (record.Type == Baptism && People.TryGetValue(record.PersonId, out var person))
                person.Baptism ??= record.Date;
        }
    }

    private static bool IsPdsExport(string path)
    {
        if (!Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        var header = File.ReadLines(path).FirstOrDefault() ?? string.Empty;
        return header.Contains("\"Fam Unique ID\"", StringComparison.OrdinalIgnoreCase) &&
               header.Contains("\"Unique ID\"", StringComparison.OrdinalIgnoreCase);
    }

    private static int RunPds(Options options)
    {
        var records = ReadCsvRecords(options.Input);
        if (records.Count < 2) throw new InvalidOperationException("The PDS export has no data rows.");

        // The export repeats "ID/Env Number"; the first occurrence wins.
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < records[0].Fields.Count; i++) headers.TryAdd(records[0].Fields[i].Trim(), i);
        var missing = new[] { "Unique ID", "First Name", "Last Name", "Date of Birth" }.Where(h => !headers.ContainsKey(h)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"PDS export is missing required column(s): {string.Join(", ", missing)}.");

        var pdsPeople = new List<PdsPerson>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (line, fields) in records.Skip(1))
        {
            if (ReadPdsPerson(fields, headers, line) is not { } person) continue;
            if (!seenIds.Add(person.PdsId))
            {
                PdsNote(line, "person", "warning", "skipped-duplicate-pds-id", person.PdsId, "Unique ID", "this PDS Unique ID appears on an earlier row; row skipped");
                continue;
            }
            pdsPeople.Add(person);
        }

        using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(options.Database)}");
        connection.Open();
        Initialize(connection);
        using var transaction = connection.BeginTransaction();
        var state = LoadPdsState(connection, transaction);

        var tiers = new Dictionary<string, int>(StringComparer.Ordinal);
        int created = 0, skipped = 0, inserted = 0, updated = 0;
        foreach (var x in pdsPeople)
        {
            try
            {
                var (person, tier, problem) = MatchPds(x, state);
                if (problem is not null)
                {
                    skipped++;
                    PdsNote(x.Row, "person", "warning", "skipped-ambiguous", x.PdsId, "PersonId", $"{x.Display}: {problem}; nothing written for this row. ACTION: resolve manually");
                    continue;
                }

                if (person is null)
                {
                    person = InsertPdsPerson(connection, transaction, state, x);
                    created++;
                    PdsNote(x.Row, "person", "info", "create-person", x.PdsId, string.Empty, $"{x.Display}: no existing person matched; created");
                }
                else
                {
                    tiers[tier] = tiers.GetValueOrDefault(tier) + 1;
                    UpdatePdsPerson(connection, transaction, person, x);
                    PdsNote(x.Row, "person", "info", $"match-{tier}", x.PdsId, string.Empty, $"{x.Display} -> {person.Describe}");
                }

                foreach (var sacrament in x.Sacraments)
                {
                    var outcome = OverlaySacrament(connection, transaction, state, person, x, sacrament);
                    if (outcome == "inserted") inserted++;
                    else if (outcome == "updated") updated++;
                }
            }
            catch (Exception exception)
            {
                _errors++;
                PdsNote(x.Row, "person", "error", "skipped", x.PdsId, string.Empty, $"{x.Display}: {exception.Message}");
            }
        }

        var cleared = ClearRedundantPersonBaptismFields(connection, transaction);
        ReportUnmappedParishes();
        if (options.Commit && _errors == 0)
        {
            WriteAuditEvent(connection, transaction, pdsPeople.Count, created, inserted,
                $"PDS import: {pdsPeople.Count} rows, {pdsPeople.Count - created - skipped} people matched, {created} created, {inserted} entries added, {updated} entries filled");
            transaction.Commit();
        }
        else transaction.Rollback();

        WriteReport(options.Report, "pds");
        Console.WriteLine($"""
            PDS rows read: {records.Count - 1}; people processed: {pdsPeople.Count}
            People matched: {string.Join(", ", tiers.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value}"))}
            People created: {created}; rows skipped as ambiguous: {skipped}
            Entries {(options.Commit ? "added" : "planned")}: {inserted}; existing entries with blanks filled: {updated}
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

    private static List<(int Line, List<string> Fields)> ReadCsvRecords(string path)
    {
        var text = File.ReadAllText(path);
        var records = new List<(int, List<string>)>();
        var record = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        int line = 1, start = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') line++;
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else value.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { record.Add(RepairEncoding(value.ToString().Trim())); value.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { i++; line++; }
                record.Add(RepairEncoding(value.ToString().Trim()));
                value.Clear();
                if (record.Count > 1 || record[0].Length > 0) records.Add((start, record));
                record = [];
                start = line;
            }
            else value.Append(c);
        }
        if (value.Length > 0 || record.Count > 0)
        {
            record.Add(RepairEncoding(value.ToString().Trim()));
            records.Add((start, record));
        }
        return records;
    }

    private static PdsPerson? ReadPdsPerson(List<string> fields, Dictionary<string, int> headers, int row)
    {
        string? Raw(string header) => headers.TryGetValue(header, out var i) && i < fields.Count ? fields[i].Trim() : null;
        string? F(string header) => Clean(Raw(header));
        bool Yes(string header) => string.Equals(Raw(header), "YES", StringComparison.OrdinalIgnoreCase);

        var pdsId = F("Unique ID");
        if (pdsId is null)
        {
            PdsNote(row, "person", "warning", "skipped", string.Empty, "Unique ID", "row has no PDS Unique ID");
            return null;
        }

        var firstRaw = F("First Name") is { } f ? TitlePrefix.Replace(f, string.Empty).Trim() : null;
        var lastRaw = F("Last Name");
        if (string.IsNullOrWhiteSpace(firstRaw) || lastRaw is null)
        {
            PdsNote(row, "person", "warning", "skipped", pdsId, "First Name/Last Name", "missing first name or last name");
            return null;
        }

        var alternateSurnames = Parenthesized.Matches(lastRaw).Select(m => m.Groups[1].Value.Trim()).Where(s => s.Length > 0).ToList();
        var family = Regex.Replace(Parenthesized.Replace(lastRaw, " "), @"\s+", " ").Trim();
        if (family.Length == 0) family = alternateSurnames.FirstOrDefault() ?? lastRaw;

        var firstTokens = firstRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var given = firstTokens[0];
        var middle = string.Join(" ", firstTokens.Skip(1).Append(F("Middle Name") ?? string.Empty).Where(t => t.Length > 0));
        var maiden = F("Maiden Name");
        var baptismalName = F("Baptism Baptismal Name")?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        var firstNames = new HashSet<string>(StringComparer.Ordinal) { Normalize(given) };
        if (F("Nickname") is { } nickname) firstNames.Add(Normalize(nickname.Split(' ')[0]));
        if (baptismalName.Length > 0) firstNames.Add(Normalize(baptismalName[0]));
        var surnames = new HashSet<string>(StringComparer.Ordinal) { Normalize(family) };
        foreach (var alternate in alternateSurnames) surnames.Add(Normalize(alternate));
        if (maiden is not null) surnames.Add(Normalize(maiden));
        if (baptismalName.Length >= 2) surnames.Add(Normalize(baptismalName[^1]));
        firstNames.Remove(string.Empty);
        surnames.Remove(string.Empty);

        var birth = PdsDate(row, pdsId, "person", "Date of Birth", F("Date of Birth"), out _);
        var deceased = PdsDate(row, pdsId, "person", "Deceased Date", F("Deceased Date"), out _);
        var status = Yes("Deceased") || Yes("Inactive") ? "historical" : Yes("Non-Parishioner") ? "non_member" : "active_member";

        var sacraments = new List<PdsSacrament>();
        foreach (var (type, prefix) in PdsSacramentPrefixes)
        {
            var date = PdsDate(row, pdsId, type, $"{prefix} Date", F($"{prefix} Date"), out var yearOnly);
            if (type == Baptism && date is null && yearOnly is null && F("Baptism Year of Baptism") is { } year &&
                year.Length == 4 && year.All(char.IsDigit))
                yearOnly = year;

            var volume = F($"{prefix} Vol");
            var page = F($"{prefix} Page");
            var item = F($"{prefix} Entry");
            var performed = Yes($"{prefix} Performed");
            if (!performed && date is null && yearOnly is null && volume is null && page is null && item is null) continue;

            var placeOnly = F($"{prefix} Place Only");
            var cityState = F($"{prefix} Place City State");
            // Place alone is ambiguous (St Joseph exists in Jasper and Dale), so the city is part of the lookup key.
            var place = placeOnly is null ? null : Canonical(cityState is null ? placeOnly : $"{placeOnly}, {cityState}");

            var sponsors = (F($"{prefix} Sponsor List") ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Clean).OfType<string>()
                .DistinctBy(Normalize)
                .ToList();

            var details = new List<string>();
            string? name = null;
            if (type == Confirmation) name = F("Confirm Confirmation Name");
            if (type == Marriage)
            {
                var extra = F("Marriage Extra Info");
                var notation = F("Marriage Notation");
                if (extra is not null) name = SpousePrefix.Replace(extra, string.Empty).Trim();
                else if (notation is not null && SpousePrefix.IsMatch(notation)) { name = SpousePrefix.Replace(notation, string.Empty).Trim(); notation = null; }
                if (notation is not null) details.Add($"Notation: {notation}");
                if (F("Marriage Permission") is { } permission) details.Add($"Permission: {permission}");
                if (F("Prev Marriages") is { } previous) details.Add($"Marriages: {previous}");
            }
            else if (F($"{prefix} Extra Info") is { } extraInfo) details.Add(extraInfo);
            if (F($"{prefix} Notes") is { } notes) details.Add(notes);
            if (Yes($"{prefix} Invalid")) details.Add("Invalid");
            if (type == Baptism && Yes("Baptism Conditional Baptism")) details.Add("Conditional baptism");
            if (yearOnly is not null) details.Add($"Year: {yearOnly}");
            if (sponsors.Count > 2) details.Add($"Additional sponsors: {string.Join(", ", sponsors.Skip(2))}");

            var minister = F($"{prefix} Performed By");

            sacraments.Add(new PdsSacrament(type, date, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SacramentDate"] = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["Name"] = string.IsNullOrWhiteSpace(name) ? null : name,
                ["Place"] = place,
                ["Minister"] = minister,
                ["SponsorOne"] = sponsors.ElementAtOrDefault(0),
                ["SponsorTwo"] = sponsors.ElementAtOrDefault(1),
                ["Volume"] = volume,
                ["Page"] = page,
                ["Item"] = item,
                ["Details"] = details.Count == 0 ? null : string.Join(" | ", details)
            }));
        }

        return new PdsPerson(row, pdsId, given, middle.Length == 0 ? null : middle, family, maiden, birth, deceased, status, firstNames, surnames, sacraments);
    }

    private static DateOnly? PdsDate(int row, string pdsId, string type, string field, string? raw, out string? yearOnly)
    {
        yearOnly = null;
        if (raw is null) return null;
        if (raw.Length == 4 && raw.All(char.IsDigit))
        {
            if (raw == "0000") return null;
            yearOnly = raw;
            PdsNote(row, type, "warning", "year-only-date", pdsId, field, $"'{raw}' is a year only; date left blank");
            return null;
        }
        if (!TryParseDate(raw, out var value))
        {
            PdsNote(row, type, "warning", "unparsed-date", pdsId, field, $"could not read '{raw}' as a date; stored as blank");
            return null;
        }
        if (value.Year < EarliestPlausibleYear || value > DateOnly.FromDateTime(DateTime.Today))
            PdsNote(row, type, "warning", "implausible-date", pdsId, field, $"'{raw}' is outside {EarliestPlausibleYear}-today; imported as-is and needs review");
        return value;
    }

    private static PdsState LoadPdsState(SqliteConnection connection, SqliteTransaction transaction)
    {
        static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
        static DateOnly? Date(string? raw) => raw is not null && TryParseDate(raw, out var d) ? d : null;

        var state = new PdsState();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT RecordId,GivenName,MiddleName,FamilyName,BirthDate,BaptismDate,PdsMemberId,MaidenName,DeceasedDate,ChurchDbId FROM People";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var id)) continue;
                state.Add(new DbPerson
                {
                    Id = id, Given = reader.GetString(1), Middle = Text(reader, 2), Family = reader.GetString(3),
                    Birth = Date(Text(reader, 4)), Baptism = Date(Text(reader, 5)), Pds = Text(reader, 6),
                    Maiden = Text(reader, 7), Deceased = Date(Text(reader, 8)), ChurchId = Text(reader, 9)
                });
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT RecordId,PersonRecordId,SacramentType,{string.Join(",", SacramentColumns)} FROM Sacraments";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var id) || !Guid.TryParse(reader.GetString(1), out var personId)) continue;
                var record = new DbSacrament { Id = id, PersonId = personId, Type = reader.GetString(2) };
                for (var i = 0; i < SacramentColumns.Length; i++) record.Fields[SacramentColumns[i]] = Text(reader, i + 3);
                state.Add(record);
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COALESCE(MAX(CAST(ChurchDbId AS INTEGER)),0) FROM People";
            state.LastChurchId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return state;
    }

    private static (DbPerson? Person, string Tier, string? Problem) MatchPds(PdsPerson x, PdsState state)
    {
        if (state.ByPds.TryGetValue(x.PdsId, out var known)) return (known, "pds-id", null);

        // A person already holding another PDS ID is a different PDS member.
        static bool Eligible(DbPerson p) => p.Pds is null;
        string Ambiguous(string criterion, IEnumerable<DbPerson> people) =>
            $"{criterion} matches {people.Count()} existing people: {string.Join("; ", people.Select(p => p.Describe))}";

        var referenceHits = new HashSet<Guid>();
        foreach (var sacrament in x.Sacraments)
        {
            var page = NumKey(sacrament.Fields["Page"]);
            var volume = NumKey(sacrament.Fields["Volume"]);
            var item = NumKey(sacrament.Fields["Item"]);
            if (page is null || volume is null && item is null) continue;
            foreach (var record in state.ByTypePage.GetValueOrDefault($"{sacrament.Type}|{page}", []))
            {
                var compared = 0;
                if (volume is not null && NumKey(record.Fields["Volume"]) is { } recordVolume) { if (recordVolume != volume) continue; compared++; }
                if (item is not null && NumKey(record.Fields["Item"]) is { } recordItem) { if (recordItem != item) continue; compared++; }
                var bothDated = sacrament.Date is not null && record.Date is not null;
                if (bothDated && sacrament.Date != record.Date) continue;
                // Page plus only one of volume/entry can name several entries on the page; require the date too.
                if (compared == 0 || compared == 1 && !bothDated) continue;
                var person = state.People[record.PersonId];
                // Bride and groom share one marriage register entry, so a surname alone would match the spouse.
                var nameAgrees = sacrament.Type == Marriage ? FirstMatches(person, x) : FirstMatches(person, x) || SurnameMatches(person, x);
                if (Eligible(person) && nameAgrees) referenceHits.Add(person.Id);
            }
        }
        if (referenceHits.Count == 1) return (state.People[referenceHits.First()], "register-reference", null);
        if (referenceHits.Count > 1) return (null, string.Empty, Ambiguous("register Volume/Page/Entry", referenceHits.Select(id => state.People[id])));

        if (x.Birth is { } birth)
        {
            var pool = state.ByBirth.GetValueOrDefault(birth, [])
                .Where(p => Eligible(p) && FirstMatches(p, x) && SurnameMatches(p, x)).ToList();
            var baptism = x.BaptismDate;
            if (baptism is not null)
            {
                var strong = pool.Where(p => p.Baptism == baptism).ToList();
                if (strong.Count == 1) return (strong[0], "name-birth-baptism", null);
                if (strong.Count > 1) return (null, string.Empty, Ambiguous("name, birth date, and baptism date", strong));
            }
            if (pool.Count == 1)
            {
                if (baptism is not null && pool[0].Baptism is not null && pool[0].Baptism != baptism)
                    PdsNote(x.Row, "person", "warning", "matched-despite-baptism-date", x.PdsId, "BaptismDate",
                        $"{x.Display}: matched {pool[0].Describe} on full name and birth date although PDS baptism date is {baptism:yyyy-MM-dd}. ACTION: verify");
                return (pool[0], "name-birth", null);
            }
            if (pool.Count > 1) return (null, string.Empty, Ambiguous("name and birth date", pool));
        }

        foreach (var marriage in x.Sacraments.Where(s => s.Type == Marriage && s.Date is not null && s.Fields["Name"] is not null))
        {
            var spouse = Normalize(LastToken(marriage.Fields["Name"]!));
            var hits = state.MarriagesByDate.GetValueOrDefault(marriage.Date!.Value, [])
                .Where(r => r.Fields["Name"] is { } name && Normalize(LastToken(name)) == spouse)
                .Select(r => state.People[r.PersonId])
                .Where(p => Eligible(p) && FirstMatches(p, x) && (x.Birth is null || p.Birth is null || p.Birth == x.Birth))
                .Distinct().ToList();
            if (hits.Count == 1) return (hits[0], "marriage", null);
            if (hits.Count > 1) return (null, string.Empty, Ambiguous("wedding date and spouse", hits));
        }

        if (x.Birth is null)
        {
            var sameName = state.People.Values.Where(p => Eligible(p) && FirstMatches(p, x) && SurnameMatches(p, x)).Take(5).ToList();
            if (sameName.Count > 0)
                PdsNote(x.Row, "person", "warning", "name-only-candidates", x.PdsId, "Date of Birth",
                    $"{x.Display}: no birth date, so not matched on name alone; existing people with this name: {string.Join("; ", sameName.Select(p => p.Describe))}. ACTION: merge manually if one is this person");
        }

        return (null, "create", null);
    }

    private static bool FirstMatches(DbPerson person, PdsPerson x) => x.FirstNames.Contains(Normalize(FirstToken(person.Given)));

    private static bool SurnameMatches(DbPerson person, PdsPerson x) =>
        new[] { person.Family, person.Maiden }.OfType<string>().Select(Normalize).Where(s => s.Length > 0)
            .Any(db => x.Surnames.Any(s => s == db || s.Length >= 5 && db.Length >= 5 && Levenshtein(s, db) <= 1));

    private static string FirstToken(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
    private static string LastToken(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;

    private static string? NumKey(string? value)
    {
        if (value is null) return null;
        var key = Normalize(value).TrimStart('0');
        return key.Length == 0 ? (Normalize(value).Length > 0 ? "0" : null) : key;
    }

    private static DbPerson InsertPdsPerson(SqliteConnection connection, SqliteTransaction transaction, PdsState state, PdsPerson x)
    {
        var person = new DbPerson
        {
            Id = DeterministicId($"pds:{x.PdsId}"), Given = x.Given, Middle = x.Middle, Family = x.Family,
            Birth = x.Birth, Pds = x.PdsId, Maiden = x.Maiden, Deceased = x.Deceased,
            ChurchId = (++state.LastChurchId).ToString(CultureInfo.InvariantCulture)
        };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO People(RecordId,GivenName,MiddleName,FamilyName,BirthDate,ChurchDbId,BirthCity,BaptismDate,BaptismChurch," +
            "Status,PdsMemberId,FlocknoteId,UpdatedUtc,MaidenName,DeceasedDate) " +
            "VALUES($id,$given,$middle,$family,$birth,$churchId,NULL,NULL,NULL,$status,$pds,NULL,$updated,$maiden,$deceased)";
        Bind(command, "$id", person.Id.ToString());
        Bind(command, "$given", person.Given);
        Bind(command, "$middle", person.Middle);
        Bind(command, "$family", person.Family);
        Bind(command, "$birth", person.Birth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Bind(command, "$churchId", person.ChurchId);
        Bind(command, "$status", x.Status);
        Bind(command, "$pds", x.PdsId);
        Bind(command, "$updated", DateTimeOffset.UtcNow.ToString("O"));
        Bind(command, "$maiden", x.Maiden);
        Bind(command, "$deceased", x.Deceased?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        state.Add(person);
        return person;
    }

    private static void UpdatePdsPerson(SqliteConnection connection, SqliteTransaction transaction, DbPerson person, PdsPerson x)
    {
        if (person.Birth is not null && x.Birth is not null && person.Birth != x.Birth)
            PdsNote(x.Row, "person", "warning", "pds-conflict", x.PdsId, "BirthDate", $"{person.Describe}: register has {person.Birth:yyyy-MM-dd}, PDS has {x.Birth:yyyy-MM-dd}; register kept");
        if (person.Maiden is not null && x.Maiden is not null && Normalize(person.Maiden) != Normalize(x.Maiden))
            PdsNote(x.Row, "person", "warning", "pds-conflict", x.PdsId, "MaidenName", $"{person.Describe}: existing '{person.Maiden}', PDS '{x.Maiden}'; existing kept");
        if (person.Deceased is not null && x.Deceased is not null && person.Deceased != x.Deceased)
            PdsNote(x.Row, "person", "warning", "pds-conflict", x.PdsId, "DeceasedDate", $"{person.Describe}: existing {person.Deceased:yyyy-MM-dd}, PDS {x.Deceased:yyyy-MM-dd}; existing kept");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE People SET PdsMemberId=$pds,Status=$status,BirthDate=COALESCE(BirthDate,$birth),MaidenName=COALESCE(MaidenName,$maiden)," +
            "DeceasedDate=COALESCE(DeceasedDate,$deceased),UpdatedUtc=$updated WHERE RecordId=$id";
        Bind(command, "$id", person.Id.ToString());
        Bind(command, "$pds", x.PdsId);
        Bind(command, "$status", x.Status);
        Bind(command, "$birth", x.Birth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Bind(command, "$maiden", x.Maiden);
        Bind(command, "$deceased", x.Deceased?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Bind(command, "$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();

        person.Pds = x.PdsId;
        person.Birth ??= x.Birth;
        person.Maiden ??= x.Maiden;
        person.Deceased ??= x.Deceased;
    }

    private static string OverlaySacrament(SqliteConnection connection, SqliteTransaction transaction, PdsState state, DbPerson person, PdsPerson x, PdsSacrament sacrament)
    {
        var existing = state.SacramentsByPerson.GetValueOrDefault(person.Id, []).Where(r => r.Type == sacrament.Type).ToList();
        // A person has one baptism, first communion, and confirmation; a marriage on another date is another marriage.
        var candidates = sacrament.Type == Marriage
            ? existing.Where(r => sacrament.Date is null || r.Date is null || r.Date == sacrament.Date).ToList()
            : existing;
        if (candidates.Count > 1)
        {
            var exact = candidates.Where(r => sacrament.Date is not null && r.Date == sacrament.Date).ToList();
            if (exact.Count == 1) candidates = exact;
        }
        if (candidates.Count > 1)
        {
            PdsNote(x.Row, sacrament.Type, "warning", "skipped-ambiguous-entry", x.PdsId, sacrament.Type,
                $"{person.Describe} already has {candidates.Count} {sacrament.Type} entries and PDS does not identify which one; PDS details not applied");
            return "skipped";
        }

        if (candidates.Count == 0)
        {
            if (SacramentColumns.Where(c => c != "Details").All(c => sacrament.Fields[c] is null))
                PdsNote(x.Row, sacrament.Type, "warning", "sparse-entry", x.PdsId, sacrament.Type,
                    "PDS marks this sacrament as performed but records no date, place, minister, or register reference; an empty entry was added");
            var record = new DbSacrament { Id = DeterministicId($"pds|{x.PdsId}|{sacrament.Type}"), PersonId = person.Id, Type = sacrament.Type };
            foreach (var column in SacramentColumns) record.Fields[column] = sacrament.Fields[column];
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                $"INSERT INTO Sacraments(RecordId,PersonRecordId,SacramentType,{string.Join(",", SacramentColumns)},UpdatedUtc) " +
                $"VALUES($id,$person,$type,{string.Join(",", SacramentColumns.Select(c => "$" + c))},$updated)";
            Bind(insert, "$id", record.Id.ToString());
            Bind(insert, "$person", person.Id.ToString());
            Bind(insert, "$type", sacrament.Type);
            foreach (var column in SacramentColumns) Bind(insert, "$" + column, record.Fields[column]);
            Bind(insert, "$updated", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
            state.Add(record);
            return "inserted";
        }

        var target = candidates[0];
        var registerSponsors = new[] { target.Fields["SponsorOne"], target.Fields["SponsorTwo"] }.OfType<string>().ToList();
        var fills = new List<string>();
        foreach (var column in SacramentColumns)
        {
            var incoming = sacrament.Fields[column];
            if (incoming is null) continue;
            var current = target.Fields[column];
            if (current is null)
            {
                fills.Add(column);
                continue;
            }
            var same = column switch
            {
                "Details" => true,
                "Volume" or "Page" or "Item" => NumKey(current) == NumKey(incoming),
                "SponsorOne" or "SponsorTwo" => registerSponsors.Any(s => SameParent(s, incoming)),
                "Minister" or "Name" => SameParent(current, incoming),
                _ => Normalize(current) == Normalize(incoming)
            };
            if (!same)
                PdsNote(x.Row, sacrament.Type, "warning", "pds-conflict", x.PdsId, column,
                    $"{person.Describe}: register has '{current}', PDS has '{incoming}'; register kept");
        }
        if (fills.Count == 0) return "unchanged";

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE Sacraments SET {string.Join(",", fills.Select(c => $"{c}=${c}"))},UpdatedUtc=$updated WHERE RecordId=$id";
        foreach (var column in fills)
        {
            Bind(update, "$" + column, sacrament.Fields[column]);
            target.Fields[column] = sacrament.Fields[column];
        }
        Bind(update, "$updated", DateTimeOffset.UtcNow.ToString("O"));
        Bind(update, "$id", target.Id.ToString());
        update.ExecuteNonQuery();
        PdsNote(x.Row, sacrament.Type, "info", "fill-blanks", x.PdsId, string.Join("/", fills), $"filled from PDS on {person.Describe}");
        return "updated";
    }

    private static void PdsNote(int row, string type, string severity, string action, string pdsId, string field, string message)
    {
        if (severity == "warning") _warnings++;
        Report.Add(Csv("PDS", row.ToString(CultureInfo.InvariantCulture), type, severity, action, pdsId, field, message));
    }
}
