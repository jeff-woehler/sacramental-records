using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SacramentalRecords.Domain;

namespace SacramentalRecords.Infrastructure;

public enum FlocknoteDirection { Import, Export }
public enum FlocknoteField { Date, Name, Place, Minister, Sponsors, Volume, Page, Item, Notes }
public enum FlocknoteChangeKind { FillRegistry, FillFlocknote, Conflict, Information }
public enum FlocknoteDecision { KeepRegistry, UseFlocknote, LeaveUnchanged, ReviewLater, NotApplicable }
public enum FlocknoteMatchAction { Link, CreatePerson, Skip }

public enum FlocknoteGroup
{
    NoChanges,
    ProposedChanges,
    NewPeople,
    PossibleMatches,
    MissingFromSnapshot,
    Invalid
}

public sealed record FlocknoteRow(int RowNumber, string? FlocknoteId, string Name, IReadOnlyDictionary<string, string> Values);

public sealed record FlocknoteOption<T>(T Value, string Label);

/// <summary>A Flocknote sacrament spreadsheet. Columns are located by their row-1 header text, never by position.</summary>
public sealed class FlocknoteSnapshot
{
    public const string DateFormat = "MMMM d, yyyy";
    private static readonly string[] IdHeaders = ["flocknoteID", "Flocknote ID"];
    private static readonly object CacheLock = new();
    private static (string Path, DateTime Stamp, FlocknoteSnapshot Snapshot)? _cache;
    private readonly HashSet<string> _headerSet;

    private FlocknoteSnapshot(string filePath, DateTime lastWriteTime, IReadOnlyList<string> headers, IReadOnlyList<FlocknoteRow> rows)
    {
        FilePath = filePath;
        LastWriteTime = lastWriteTime;
        Headers = headers;
        Rows = rows;
        _headerSet = headers.Where(h => h.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string FilePath { get; }
    public DateTime LastWriteTime { get; }
    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<FlocknoteRow> Rows { get; }
    public bool HasHeader(string header) => _headerSet.Contains(header);

    public IReadOnlyList<string> MissingHeaders() =>
        FlocknoteMaps.All.SelectMany(map => Enum.GetValues<FlocknoteField>().Select(map.Header))
            .OfType<string>().Where(h => !HasHeader(h)).Distinct().ToList();

    public static FlocknoteSnapshot Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("The Flocknote sacrament file was not found.", full);
        var stamp = File.GetLastWriteTimeUtc(full);
        lock (CacheLock)
            if (_cache is { } cached && string.Equals(cached.Path, full, StringComparison.OrdinalIgnoreCase) && cached.Stamp == stamp)
                return cached.Snapshot;

        // Staff often have the export open in Excel; read it without requesting exclusive access.
        // Streamed with the OpenXML SDK: loading the whole workbook through ClosedXML takes many seconds.
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException($"'{Path.GetFileName(full)}' is not an Excel workbook.");
        var firstSheet = workbook.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault() ?? throw new InvalidDataException($"'{Path.GetFileName(full)}' has no worksheets.");
        var worksheet = (WorksheetPart)workbook.GetPartById(firstSheet.Id!.Value!);
        var shared = workbook.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? [];
        var dateStyles = DateStyleIndexes(workbook);

        var headers = new List<string>();
        var byColumn = new Dictionary<int, string>();
        string? idHeader = null;
        var rows = new List<FlocknoteRow>();
        using var reader = OpenXmlReader.Create(worksheet);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
            var row = (Row)reader.LoadCurrentElement()!;
            var number = (int)(row.RowIndex?.Value ?? (uint)(rows.Count + 2));
            var cells = row.Elements<Cell>().Select(c => (Column: ColumnNumber(c.CellReference?.Value), Text: CellText(c, shared, dateStyles))).ToList();
            if (number == 1)
            {
                var last = cells.Count == 0 ? 0 : cells.Max(c => c.Column);
                for (var column = 1; column <= last; column++)
                {
                    var header = cells.FirstOrDefault(c => c.Column == column).Text ?? string.Empty;
                    headers.Add(header);
                    if (header.Length > 0 && !byColumn.Values.Contains(header, StringComparer.OrdinalIgnoreCase)) byColumn[column] = header;
                }
                idHeader = byColumn.Values.FirstOrDefault(h => IdHeaders.Contains(h, StringComparer.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"'{Path.GetFileName(full)}' has no '{IdHeaders[0]}' column in row 1. Choose the Flocknote sacrament export.");
                continue;
            }
            if (idHeader is null) throw new InvalidDataException($"'{Path.GetFileName(full)}' has no column headers in row 1.");

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, text) in cells)
                if (text.Length > 0 && byColumn.TryGetValue(column, out var header)) values[header] = text;
            if (values.Count == 0) continue;
            var name = values.GetValueOrDefault("name")
                ?? $"{values.GetValueOrDefault("First Name")} {values.GetValueOrDefault("Last Name")}";
            rows.Add(new FlocknoteRow(number, values.GetValueOrDefault(idHeader), Regex.Replace(name, @"\s+", " ").Trim(), values));
        }
        if (idHeader is null) throw new InvalidDataException($"'{Path.GetFileName(full)}' has no column headers in row 1.");

        var snapshot = new FlocknoteSnapshot(full, File.GetLastWriteTime(full), headers, rows);
        lock (CacheLock) _cache = (full, stamp, snapshot);
        return snapshot;
    }

    private static int ColumnNumber(string? reference)
    {
        var column = 0;
        foreach (var c in reference ?? string.Empty)
        {
            if (!char.IsLetter(c)) break;
            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return column;
    }

    private static string CellText(Cell cell, string[] shared, HashSet<uint> dateStyles)
    {
        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;
        if (type == CellValues.InlineString) return (cell.InlineString?.InnerText ?? string.Empty).Trim();
        if (raw is null) return string.Empty;
        if (type == CellValues.SharedString)
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Length ? shared[i].Trim() : string.Empty;
        if (type == CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";
        if (type != CellValues.String && cell.StyleIndex?.Value is { } style && dateStyles.Contains(style) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is >= 1 and <= 2958465)
            return DateTime.FromOADate(serial).ToString(DateFormat, CultureInfo.InvariantCulture);
        return raw.Trim();
    }

    private static HashSet<uint> DateStyleIndexes(WorkbookPart workbook)
    {
        var result = new HashSet<uint>();
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        if (stylesheet?.CellFormats is null) return result;
        var custom = stylesheet.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null)
            .ToDictionary(f => f.NumberFormatId!.Value, f => f.FormatCode?.Value ?? string.Empty) ?? [];
        uint index = 0;
        foreach (var format in stylesheet.CellFormats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value ?? 0;
            var isDate = id is >= 14 and <= 22 or >= 45 and <= 47 ||
                         custom.TryGetValue(id, out var code) && Regex.IsMatch(Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]", string.Empty), "[dmyDMY]");
            if (isDate) result.Add(index);
            index++;
        }
        return result;
    }
}

public sealed record FlocknoteSacramentMap(string Type, string Prefix, string? SponsorsHeader, string? NameHeader)
{
    public string? Header(FlocknoteField field) => field switch
    {
        FlocknoteField.Date => $"{Prefix} Date",
        FlocknoteField.Place => $"{Prefix} Location",
        FlocknoteField.Minister => $"{Prefix} Celebrant",
        FlocknoteField.Volume => $"{Prefix} Record Book",
        FlocknoteField.Page => $"{Prefix} Record Book Page",
        FlocknoteField.Item => $"{Prefix} Record Book Entry",
        FlocknoteField.Notes => $"{Prefix} Notes",
        FlocknoteField.Sponsors => SponsorsHeader,
        FlocknoteField.Name => NameHeader,
        _ => null
    };
}

public static class FlocknoteMaps
{
    public static readonly FlocknoteSacramentMap[] All =
    [
        new("baptism", "Baptism", "Godparents", null),
        new("first_communion", "First Communion", null, null),
        new("confirmation", "Confirmation", "Confirmation Sponsor", "Confirmation Name"),
        new("marriage", "Marriage", "Marriage Witnesses", "Marriage Spouse"),
        new("holy_orders", "Holy Orders", null, null)
    ];

    public static string FieldLabel(FlocknoteField field, string type) => field switch
    {
        FlocknoteField.Date => "Date",
        FlocknoteField.Name => type == "marriage" ? "Spouse" : "Confirmation name",
        FlocknoteField.Place => "Place",
        FlocknoteField.Minister => "Minister / celebrant",
        FlocknoteField.Sponsors => type == "marriage" ? "Witnesses" : type == "baptism" ? "Godparents" : "Sponsor",
        FlocknoteField.Volume => "Register volume",
        FlocknoteField.Page => "Register page",
        FlocknoteField.Item => "Register entry",
        FlocknoteField.Notes => "Notes",
        _ => field.ToString()
    };

    public static readonly IReadOnlyList<FlocknoteOption<FlocknoteDecision>> Decisions =
    [
        new(FlocknoteDecision.KeepRegistry, "Keep registry value"),
        new(FlocknoteDecision.UseFlocknote, "Use Flocknote value"),
        new(FlocknoteDecision.LeaveUnchanged, "Leave unchanged"),
        new(FlocknoteDecision.ReviewLater, "Review later")
    ];

    public static string GroupLabel(FlocknoteGroup group) => group switch
    {
        FlocknoteGroup.NoChanges => "Linked people with no changes",
        FlocknoteGroup.ProposedChanges => "Linked people with proposed changes",
        FlocknoteGroup.NewPeople => "New people",
        FlocknoteGroup.PossibleMatches => "Possible matches and duplicates",
        FlocknoteGroup.MissingFromSnapshot => "Previously linked people missing from this snapshot",
        FlocknoteGroup.Invalid => "Invalid or incomplete rows",
        _ => group.ToString()
    };
}

public sealed class FlocknoteFieldChange : INotifyPropertyChanged
{
    private FlocknoteDecision _decision;

    public required FlocknoteGroup Group { get; init; }
    public required string PersonName { get; init; }
    public Guid? PersonId { get; init; }
    public string? FlocknoteId { get; init; }
    public FlocknoteRow? Row { get; init; }
    public string? SacramentType { get; init; }
    public Guid? EntryId { get; init; }
    public FlocknoteField? Field { get; init; }
    public string? Header { get; init; }
    public string FieldLabel { get; init; } = "—";
    public string? RegistryValue { get; init; }
    public string? FlocknoteValue { get; init; }
    public FlocknoteChangeKind Kind { get; init; } = FlocknoteChangeKind.Information;
    public string? Note { get; init; }

    public bool IsDecidable => Kind != FlocknoteChangeKind.Information;
    public string GroupLabel => FlocknoteMaps.GroupLabel(Group);
    public string EntryLabel => FlocknoteMaps.All.FirstOrDefault(m => m.Type == SacramentType)?.Prefix ?? "—";

    public FlocknoteDecision Decision
    {
        get => _decision;
        set { if (_decision != value) { _decision = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FlocknoteMatchOption(FlocknoteMatchAction action, Person? person, string label)
{
    public FlocknoteMatchAction Action { get; } = action;
    public Person? Person { get; } = person;
    public string Label { get; } = label;
}

public sealed class FlocknoteMatch : INotifyPropertyChanged
{
    private FlocknoteMatchOption _selected;

    public FlocknoteMatch(FlocknoteRow row, IReadOnlyList<FlocknoteMatchOption> options, int selected, string reason, bool possibleDuplicate = false)
    {
        Row = row;
        Options = options;
        _selected = Options[selected];
        Reason = reason;
        PossibleDuplicate = possibleDuplicate;
    }

    public FlocknoteRow Row { get; }
    public IReadOnlyList<FlocknoteMatchOption> Options { get; }
    public string Reason { get; internal set; }
    public bool PossibleDuplicate { get; }
    public bool HasCandidates => PossibleDuplicate || Options.Any(o => o.Action == FlocknoteMatchAction.Link);
    public Person? NewPerson { get; set; }

    public FlocknoteMatchOption Selected
    {
        get => _selected;
        set { if (value is not null && _selected != value) { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record FlocknoteImportBatch(IReadOnlyList<Person> People, IReadOnlyList<SacramentRecord> Records, int Linked, int Created, int FieldsApplied);

/// <summary>Compares the registry with a Flocknote snapshot. The registry is authoritative for sacramental records.</summary>
public sealed class FlocknoteExchange
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "NOTNOTED", "NOTHINGNOTED", "NOTKNOWN", "NOTLISTED", "NOTRECORDED", "NOTGIVEN", "NOTAVAILABLE", "NOTAPPLICABLE",
        "NONENOTED", "NONELISTED", "NONEGIVEN", "UNKNOWN", "NA", "NONE", "BLANK", "NIL"
    };
    private static readonly HashSet<string> PlaceStopWords = new(StringComparer.Ordinal) { "CHURCH", "CATHOLIC", "PARISH", "OF", "THE", "AND" };
    private static readonly Regex TitlePrefix = new(@"^(?:(?:REV|REVEREND|FR|FATHER|DEACON|DCN|MSGR|MONSIGNOR|BISHOP|MOST|BR|SR)\.?\s+)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SpousePrefix = new(@"^\s*(?:SPOUSE|TO)\s*[:;]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] DateFormats = [FlocknoteSnapshot.DateFormat, "MMMM dd, yyyy", "MMM d, yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd"];

    private readonly Dictionary<Guid, List<SacramentRecord>> _sacraments;
    private readonly Dictionary<Guid, Person> _peopleById;
    private readonly Dictionary<string, Person> _byFlocknoteId;

    private FlocknoteExchange(FlocknoteSnapshot snapshot, IReadOnlyList<Person> people, IReadOnlyList<SacramentRecord> sacraments, Guid? onlyPerson)
    {
        Snapshot = snapshot;
        _peopleById = people.ToDictionary(p => p.RecordId);
        _sacraments = sacraments.GroupBy(s => s.PersonRecordId).ToDictionary(g => g.Key, g => g.ToList());
        _byFlocknoteId = people.Where(p => !string.IsNullOrWhiteSpace(p.FlocknoteId))
            .GroupBy(p => p.FlocknoteId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = people.Where(p => string.IsNullOrWhiteSpace(p.FlocknoteId))
            .SelectMany(p => NameKeys(p).Select(k => (Key: k, Person: p)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Select(x => x.Person).Distinct().ToList());
        var linkedByName = _byFlocknoteId.Values
            .SelectMany(p => NameKeys(p).Select(k => (Key: k, Person: p)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Person);

        foreach (var row in snapshot.Rows)
        {
            if (row.FlocknoteId is null || row.Name.Length == 0)
            {
                if (onlyPerson is null) Invalid.Add(Info(FlocknoteGroup.Invalid, row.Name.Length == 0 ? $"Row {row.RowNumber}" : row.Name, row, null,
                    row.FlocknoteId is null ? "The row has no Flocknote ID." : "The row has no name."));
                continue;
            }
            if (!seen.Add(row.FlocknoteId))
            {
                if (onlyPerson is null) Invalid.Add(Info(FlocknoteGroup.Invalid, row.Name, row, null, $"Flocknote ID {row.FlocknoteId} appears on an earlier row; this row is ignored."));
                continue;
            }
            if (_byFlocknoteId.TryGetValue(row.FlocknoteId, out var person))
            {
                if (onlyPerson is null || person.RecordId == onlyPerson) Linked.Add((person, row));
                continue;
            }
            if (onlyPerson is not null) continue;

            var pool = NameKeys(row.Name).SelectMany(k => candidates.GetValueOrDefault(k, [])).Distinct().ToList();
            var flocknoteBaptism = ParseDate(Clean(row.Values.GetValueOrDefault("Baptism Date")));
            var agreeing = pool.Where(p => flocknoteBaptism is null || RegistryBaptism(p) is not { } b || b == flocknoteBaptism).ToList();
            var options = new List<FlocknoteMatchOption>();
            foreach (var candidate in pool)
                options.Add(new(FlocknoteMatchAction.Link, candidate, $"Link to {Describe(candidate)}"));
            options.Add(new(FlocknoteMatchAction.CreatePerson, null, "Create new person (Active member)"));
            options.Add(new(FlocknoteMatchAction.Skip, null, "Skip for now"));

            int selected;
            string reason;
            var sameNameLinked = pool.Count == 0 ? NameKeys(row.Name).Select(k => linkedByName.GetValueOrDefault(k)).FirstOrDefault(p => p is not null) : null;
            if (sameNameLinked is not null)
            {
                selected = pool.Count + 1;
                reason = $"Same name as {Describe(sameNameLinked)}, already linked to Flocknote ID {sameNameLinked.FlocknoteId}. Possible duplicate profile in Flocknote.";
            }
            else if (pool.Count == 0)
            {
                selected = HasSacramentData(row) ? pool.Count : pool.Count + 1;
                reason = HasSacramentData(row) ? "No registry person has this name." : "No registry person has this name and the row has no sacrament details.";
            }
            else if (pool.Count == 1 && agreeing.Count == 1)
            {
                selected = 0;
                reason = flocknoteBaptism is null || RegistryBaptism(pool[0]) is null ? "One unlinked person has this name." : "Name and baptism date agree.";
            }
            else
            {
                selected = pool.Count + 1;
                reason = pool.Count == 1 ? "Same name, but the baptism dates differ." : $"{pool.Count} unlinked people have this name.";
            }
            Matches.Add(new FlocknoteMatch(row, options, selected, reason, sameNameLinked is not null));
        }

        // Two Flocknote profiles proposing the same registry person are a Flocknote duplicate; let staff decide.
        foreach (var contested in Matches.Where(m => m.Selected.Action == FlocknoteMatchAction.Link)
                     .GroupBy(m => m.Selected.Person!.RecordId).Where(g => g.Count() > 1))
            foreach (var match in contested)
            {
                match.Selected = match.Options.First(o => o.Action == FlocknoteMatchAction.Skip);
                match.Reason = $"{contested.Count()} Flocknote rows match this person. Possible duplicate profile in Flocknote; link only one.";
            }

        var snapshotIds = snapshot.Rows.Select(r => r.FlocknoteId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Missing.AddRange(_byFlocknoteId.Values.Where(p => !snapshotIds.Contains(p.FlocknoteId!.Trim()) && (onlyPerson is null || p.RecordId == onlyPerson)));
    }

    public FlocknoteSnapshot Snapshot { get; }
    public List<(Person Person, FlocknoteRow Row)> Linked { get; } = [];
    public List<FlocknoteMatch> Matches { get; } = [];
    public List<Person> Missing { get; } = [];
    public List<FlocknoteFieldChange> Invalid { get; } = [];

    public static FlocknoteExchange Build(FlocknoteSnapshot snapshot, IReadOnlyList<Person> people, IReadOnlyList<SacramentRecord> sacraments, Guid? onlyPerson = null) =>
        new(snapshot, people, sacraments, onlyPerson);

    /// <summary>Builds the review list. Unlinked rows are included according to the decisions made on the Match step.</summary>
    public List<FlocknoteFieldChange> BuildReview(FlocknoteDirection direction, bool includeUnlinked)
    {
        var review = new List<FlocknoteFieldChange>(Invalid);
        foreach (var (person, row) in Linked)
        {
            var changes = Compare(person, row, FlocknoteGroup.ProposedChanges, direction, review);
            if (changes == 0) review.Add(Info(FlocknoteGroup.NoChanges, Describe(person), row, person.RecordId, "Registry and Flocknote agree."));
        }

        var linkedThisReview = new HashSet<Guid>();
        foreach (var match in Matches)
        {
            var group = match.HasCandidates ? FlocknoteGroup.PossibleMatches : FlocknoteGroup.NewPeople;
            if (!includeUnlinked || direction == FlocknoteDirection.Export)
            {
                review.Add(Info(group, match.Row.Name, match.Row, null, direction == FlocknoteDirection.Export
                    ? "Not linked to a registry person; link it with an import first."
                    : "Not linked; outside the selected scope."));
                continue;
            }
            switch (match.Selected.Action)
            {
                case FlocknoteMatchAction.Link when match.Selected.Person is { } candidate && !linkedThisReview.Add(candidate.RecordId):
                    review.Add(Info(group, match.Row.Name, match.Row, null, $"{Describe(candidate)} is already chosen for another Flocknote row; this row will be skipped."));
                    break;
                case FlocknoteMatchAction.Link when match.Selected.Person is { } candidate:
                    var count = Compare(candidate, match.Row, group, direction, review);
                    review.Add(Info(group, Describe(candidate), match.Row, candidate.RecordId,
                        $"Will be linked to Flocknote ID {match.Row.FlocknoteId}{(count == 0 ? "; no other differences." : ".")}"));
                    break;
                case FlocknoteMatchAction.CreatePerson:
                    match.NewPerson ??= NewPerson(match.Row);
                    Compare(match.NewPerson, match.Row, group, direction, review);
                    review.Add(Info(group, match.Row.Name, match.Row, match.NewPerson.RecordId, "Will be added to the registry as an active member."));
                    break;
                default:
                    review.Add(Info(group, match.Row.Name, match.Row, null, "Skipped on the Match step; nothing will be imported."));
                    break;
            }
        }

        foreach (var person in Missing)
            review.Add(Info(FlocknoteGroup.MissingFromSnapshot, Describe(person), null, person.RecordId,
                $"Linked to Flocknote ID {person.FlocknoteId}, which is not in this snapshot."));
        return review;
    }

    /// <summary>Counts differences for one linked person (used by the person card).</summary>
    public int PendingDifferences(Person person)
    {
        var row = Linked.FirstOrDefault(l => l.Person.RecordId == person.RecordId).Row;
        if (row is null) return 0;
        var sink = new List<FlocknoteFieldChange>();
        return Compare(person, row, FlocknoteGroup.ProposedChanges, FlocknoteDirection.Import, sink);
    }

    public FlocknoteImportBatch BuildImport(IReadOnlyList<FlocknoteFieldChange> review, bool includeUnlinked)
    {
        var people = new Dictionary<Guid, Person>();
        int linked = 0, created = 0, applied = 0;
        foreach (var match in includeUnlinked ? Matches : [])
        {
            if (match.Selected.Action == FlocknoteMatchAction.Link && match.Selected.Person is { } person && !people.ContainsKey(person.RecordId))
            {
                people[person.RecordId] = person with { FlocknoteId = match.Row.FlocknoteId };
                linked++;
            }
            else if (match.Selected.Action == FlocknoteMatchAction.CreatePerson && match.NewPerson is { } newPerson)
            {
                people[newPerson.RecordId] = newPerson;
                created++;
            }
        }

        var records = new Dictionary<(Guid Person, string Type), SacramentRecord>();
        foreach (var change in review.Where(c => c.Decision == FlocknoteDecision.UseFlocknote && c.Field is not null && c.PersonId is not null && c.SacramentType is not null))
        {
            var key = (change.PersonId!.Value, change.SacramentType!);
            if (!records.TryGetValue(key, out var record))
            {
                var existing = change.EntryId is { } entryId ? _sacraments.GetValueOrDefault(key.Item1, []).FirstOrDefault(s => s.RecordId == entryId) : null;
                record = existing ?? NewRecord(key.Item1, key.Item2);
            }
            records[key] = Apply(record, change.Field!.Value, change.FlocknoteValue);
            applied++;
        }
        return new FlocknoteImportBatch(people.Values.ToList(), records.Values.ToList(), linked, created, applied);
    }

    /// <summary>Writes a new spreadsheet with the snapshot's headers. Never writes to the snapshot file itself.</summary>
    public int WriteExport(string path, IReadOnlyList<FlocknoteFieldChange> review, bool allLinkedPeople)
    {
        var target = Path.GetFullPath(path);
        if (string.Equals(target, Snapshot.FilePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The update file cannot replace the Flocknote snapshot. Choose a different file name.");

        var outbound = review.Where(c => c.Decision == FlocknoteDecision.KeepRegistry && c.Header is not null && c.RegistryValue is not null && c.Kind != FlocknoteChangeKind.Information && c.Row is not null)
            .GroupBy(c => c.Row!).ToDictionary(g => g.Key, g => g.ToList());
        var rows = allLinkedPeople ? Linked.Select(l => l.Row).ToList() : Linked.Select(l => l.Row).Where(outbound.ContainsKey).ToList();

        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Sheet1");
        for (var c = 0; c < Snapshot.Headers.Count; c++) sheet.Cell(1, c + 1).SetValue(Snapshot.Headers[c]);
        var index = Snapshot.Headers.Select((h, i) => (h, i)).Where(x => x.h.Length > 0)
            .GroupBy(x => x.h, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i + 1, StringComparer.OrdinalIgnoreCase);
        var number = 2;
        foreach (var row in rows)
        {
            var values = new Dictionary<string, string>(row.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var change in outbound.GetValueOrDefault(row, [])) values[change.Header!] = change.RegistryValue!;
            foreach (var (header, value) in values)
                if (index.TryGetValue(header, out var column)) sheet.Cell(number, column).SetValue(value);
            number++;
        }
        sheet.Row(1).Style.Font.Bold = true;
        book.SaveAs(target);
        return rows.Count;
    }

    private int Compare(Person person, FlocknoteRow row, FlocknoteGroup group, FlocknoteDirection direction, List<FlocknoteFieldChange> sink)
    {
        var count = 0;
        var entries = _sacraments.GetValueOrDefault(person.RecordId, []);
        foreach (var map in FlocknoteMaps.All)
        {
            var flocknoteDate = map.Header(FlocknoteField.Date) is { } dateHeader ? ParseDate(Clean(row.Values.GetValueOrDefault(dateHeader))) : null;
            var entry = SelectEntry(entries, map.Type, flocknoteDate);
            foreach (var field in Enum.GetValues<FlocknoteField>())
            {
                if (map.Header(field) is not { } header || !Snapshot.HasHeader(header)) continue;
                var raw = Clean(row.Values.GetValueOrDefault(header));
                var flocknote = FlocknoteText(field, raw);
                if (raw is not null && flocknote is null && field == FlocknoteField.Date && !sink.Any(c => c.Row == row && c.Header == header))
                    sink.Add(Info(FlocknoteGroup.Invalid, Describe(person), row, person.RecordId, $"{header} '{raw}' could not be read as a date."));
                var registry = RegistryText(person, entry, map.Type, field);
                if (flocknote is null && registry is null) continue;
                if (flocknote is not null && registry is not null && Same(field, registry, flocknote)) continue;

                var kind = registry is null ? FlocknoteChangeKind.FillRegistry : flocknote is null ? FlocknoteChangeKind.FillFlocknote : FlocknoteChangeKind.Conflict;
                sink.Add(new FlocknoteFieldChange
                {
                    Group = group, PersonName = Describe(person), PersonId = person.RecordId, FlocknoteId = row.FlocknoteId, Row = row,
                    SacramentType = map.Type, EntryId = entry?.RecordId, Field = field, Header = header,
                    FieldLabel = FlocknoteMaps.FieldLabel(field, map.Type), RegistryValue = registry, FlocknoteValue = flocknote, Kind = kind,
                    Note = field == FlocknoteField.Notes && direction == FlocknoteDirection.Export
                        ? "Registry notes can contain confidential remarks and Flocknote shows sacrament details to the member; send only after review."
                        : kind == FlocknoteChangeKind.Conflict ? "Registry is the record of authority; keep it until supporting evidence is reviewed." : null,
                    Decision = field == FlocknoteField.Notes && direction == FlocknoteDirection.Export ? FlocknoteDecision.LeaveUnchanged : DefaultDecision(kind, direction)
                });
                count++;
            }
        }
        return count;
    }

    private static FlocknoteDecision DefaultDecision(FlocknoteChangeKind kind, FlocknoteDirection direction) => (kind, direction) switch
    {
        (FlocknoteChangeKind.FillRegistry, FlocknoteDirection.Import) => FlocknoteDecision.UseFlocknote,
        (FlocknoteChangeKind.FillRegistry, FlocknoteDirection.Export) => FlocknoteDecision.LeaveUnchanged,
        (FlocknoteChangeKind.FillFlocknote, FlocknoteDirection.Import) => FlocknoteDecision.LeaveUnchanged,
        _ => FlocknoteDecision.KeepRegistry
    };

    private static SacramentRecord? SelectEntry(List<SacramentRecord> entries, string type, DateOnly? flocknoteDate)
    {
        var ofType = entries.Where(e => string.Equals(e.SacramentType, type, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ofType.Count <= 1) return ofType.FirstOrDefault();
        return ofType.FirstOrDefault(e => flocknoteDate is not null && e.SacramentDate == flocknoteDate)
            ?? ofType.OrderByDescending(e => e.SacramentDate).First();
    }

    private static string? RegistryText(Person person, SacramentRecord? entry, string type, FlocknoteField field)
    {
        var personBaptism = entry is null && type == "baptism";
        return Blank(field switch
        {
            FlocknoteField.Date => (entry?.SacramentDate ?? (personBaptism ? person.BaptismDate : null))?.ToString(FlocknoteSnapshot.DateFormat, CultureInfo.InvariantCulture),
            FlocknoteField.Place => entry?.Place ?? (personBaptism ? person.BaptismChurch : null),
            FlocknoteField.Name => entry?.Name,
            FlocknoteField.Minister => entry?.Minister,
            FlocknoteField.Sponsors => string.Join("|", new[] { entry?.SponsorOne, entry?.SponsorTwo }.Where(s => !string.IsNullOrWhiteSpace(s))),
            FlocknoteField.Volume => entry?.Volume,
            FlocknoteField.Page => entry?.Page,
            FlocknoteField.Item => entry?.Item,
            FlocknoteField.Notes => entry?.Details,
            _ => null
        });
    }

    private static string? FlocknoteText(FlocknoteField field, string? raw) => raw is null ? null : field switch
    {
        FlocknoteField.Date => ParseDate(raw)?.ToString(FlocknoteSnapshot.DateFormat, CultureInfo.InvariantCulture),
        FlocknoteField.Name => Blank(SpousePrefix.Replace(raw, string.Empty)),
        // Celebrant is sometimes a yes/no flag rather than a name.
        FlocknoteField.Minister => raw.Equals("YES", StringComparison.OrdinalIgnoreCase) || raw.Equals("NO", StringComparison.OrdinalIgnoreCase) ? null : raw,
        FlocknoteField.Sponsors => Blank(string.Join("|", raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Clean).OfType<string>())),
        _ => raw
    };

    private static bool Same(FlocknoteField field, string registry, string flocknote) => field switch
    {
        FlocknoteField.Date => registry == flocknote,
        FlocknoteField.Volume or FlocknoteField.Page or FlocknoteField.Item => NumKey(registry) == NumKey(flocknote),
        FlocknoteField.Sponsors => SameSet(registry.Split('|'), flocknote.Split('|')),
        FlocknoteField.Minister => TokensAgree(TitlePrefix.Replace(registry, string.Empty), TitlePrefix.Replace(flocknote, string.Empty), false),
        FlocknoteField.Place => TokensAgree(registry, flocknote, true),
        _ => TokensAgree(registry, flocknote, false)
    };

    private static SacramentRecord Apply(SacramentRecord record, FlocknoteField field, string? value)
    {
        switch (field)
        {
            case FlocknoteField.Date: return record with { SacramentDate = ParseDate(value) };
            case FlocknoteField.Name: return record with { Name = value };
            case FlocknoteField.Place: return record with { Place = value };
            case FlocknoteField.Minister: return record with { Minister = value };
            case FlocknoteField.Volume: return record with { Volume = value };
            case FlocknoteField.Page: return record with { Page = value };
            case FlocknoteField.Item: return record with { Item = value };
            case FlocknoteField.Notes: return record with { Details = value };
            case FlocknoteField.Sponsors:
                var names = (value ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return record with { SponsorOne = names.ElementAtOrDefault(0), SponsorTwo = names.Length > 1 ? string.Join(", ", names.Skip(1)) : null };
            default: return record;
        }
    }

    private SacramentRecord NewRecord(Guid personId, string type)
    {
        // A new baptism entry supersedes the person-level baptism fields, so carry them over.
        var person = type == "baptism" ? _peopleById.GetValueOrDefault(personId) : null;
        return new(Guid.NewGuid(), personId, type, person?.BaptismDate, null, person?.BaptismChurch, null, null, null, null, null, false, null, null, null, null, null, null, null, DateTimeOffset.MinValue);
    }

    private static Person NewPerson(FlocknoteRow row)
    {
        var name = Regex.Replace(row.Name, @"\(([^)]*)\)", " ");
        var maiden = Regex.Match(row.Name, @"\(([^)]*)\)") is { Success: true } m ? Blank(m.Groups[1].Value) : null;
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var given = tokens.FirstOrDefault() ?? row.Name;
        var family = tokens.Length > 1 ? tokens[^1] : row.Name;
        var middle = tokens.Length > 2 ? string.Join(" ", tokens[1..^1]) : null;
        return new Person(Guid.NewGuid(), given, middle, family, null, null, null, null, null, "active_member", null, row.FlocknoteId, DateTimeOffset.MinValue, maiden);
    }

    private static FlocknoteFieldChange Info(FlocknoteGroup group, string name, FlocknoteRow? row, Guid? personId, string note) => new()
    {
        Group = group, PersonName = name, PersonId = personId, FlocknoteId = row?.FlocknoteId, Row = row, Note = note,
        Decision = FlocknoteDecision.NotApplicable
    };

    private DateOnly? RegistryBaptism(Person person) =>
        _sacraments.GetValueOrDefault(person.RecordId, []).FirstOrDefault(s => s.SacramentType == "baptism")?.SacramentDate ?? person.BaptismDate;

    private static bool HasSacramentData(FlocknoteRow row) =>
        FlocknoteMaps.All.Any(map => Enum.GetValues<FlocknoteField>().Where(f => f != FlocknoteField.Notes).Select(map.Header)
            .OfType<string>().Any(h => Clean(row.Values.GetValueOrDefault(h)) is not null));

    private static IEnumerable<string> NameKeys(Person person) =>
        new[] { person.FamilyName, person.MaidenName }.OfType<string>().Select(f => Key(person.GivenName, f)).Where(k => k.Length > 1);

    private static IEnumerable<string> NameKeys(string name)
    {
        var tokens = Regex.Replace(name, @"[()]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) yield break;
        foreach (var surname in tokens.Skip(1)) yield return Key(tokens[0], surname);
    }

    private static string Key(string given, string family) =>
        $"{Normalize(given.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? given)}|{Normalize(family)}";

    public static string Describe(Person person)
    {
        var name = string.Join(" ", new[] { person.GivenName, person.MiddleName, person.FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return person.BirthDate is { } birth ? $"{name} (born {birth.ToString("d", CultureInfo.CurrentCulture)})" : name;
    }

    private static DateOnly? ParseDate(string? text)
    {
        if (text is null) return null;
        if (DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact)) return DateOnly.FromDateTime(exact);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) && text.Trim().Length > 4 ? DateOnly.FromDateTime(parsed) : null;
    }

    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        return Placeholders.Contains(Normalize(value)) ? null : value;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? NumKey(string value)
    {
        var key = Normalize(value).TrimStart('0');
        return key.Length == 0 ? "0" : key;
    }

    private static bool SameSet(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Select(Normalize).Where(s => s.Length > 0).ToHashSet().SetEquals(right.Select(Normalize).Where(s => s.Length > 0));

    /// <summary>Equal when every word of the shorter value appears in the longer one ("Rev Raymond A Brenner" vs "Raymond Brenner").</summary>
    private static bool TokensAgree(string left, string right, bool place)
    {
        HashSet<string> Tokens(string value) => Regex.Split(value.ToUpperInvariant(), @"[^\p{L}\p{N}]+")
            .Select(t => place && t == "SAINT" ? "ST" : t)
            .Where(t => t.Length > 0 && !(place && PlaceStopWords.Contains(t))).ToHashSet(StringComparer.Ordinal);
        var a = Tokens(left);
        var b = Tokens(right);
        if (a.Count == 0 || b.Count == 0) return a.Count == b.Count;
        return a.Count <= b.Count ? a.IsSubsetOf(b) : b.IsSubsetOf(a);
    }
}
