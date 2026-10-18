using System.Globalization;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private const string FlocknoteIdHeader = "Flocknote ID";
    private const string FlocknotePdsHeader = "Member Unique ID (PDS)";

    private sealed record FlocknoteMember(int Row, string FlocknoteId, string? PdsId, string First, string Last, HashSet<string> FirstNames, HashSet<string> Surnames)
    {
        public string Display => $"{First} {Last} ({FlocknoteId})".Trim();
    }

    private sealed record LinkCandidate(Guid Id, string Given, string Family, string? Maiden, string Pds, string? FlocknoteId)
    {
        public string Describe => $"{Given} {Family} (PDS {Pds}, RecordId {Id})";
    }

    private static bool IsFlocknoteMemberExport(string path)
    {
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) return false;
        using var book = new XLWorkbook(path);
        var header = book.Worksheets.FirstOrDefault()?.FirstRowUsed();
        if (header is null) return false;
        var names = header.CellsUsed().Select(c => c.GetString().Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains(FlocknoteIdHeader) && names.Contains(FlocknotePdsHeader);
    }

    /// <summary>Links existing people to Flocknote by PDS member ID. Never creates people or sacrament entries.</summary>
    private static int RunFlocknoteMembers(Options options)
    {
        var members = ReadFlocknoteMembers(options.Input, out var withoutPds);

        using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(options.Database)}");
        connection.Open();
        Initialize(connection);
        using var transaction = connection.BeginTransaction();

        var byPds = new Dictionary<string, List<LinkCandidate>>(StringComparer.OrdinalIgnoreCase);
        var byFlocknote = new Dictionary<string, LinkCandidate>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT RecordId,GivenName,FamilyName,MaidenName,PdsMemberId,FlocknoteId FROM People";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var id)) continue;
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i).Trim();
                var person = new LinkCandidate(id, reader.GetString(1), reader.GetString(2), Text(3), Text(4) ?? string.Empty, Text(5));
                if (person.Pds.Length > 0) (byPds.TryGetValue(person.Pds, out var list) ? list : byPds[person.Pds] = []).Add(person);
                if (person.FlocknoteId is { Length: > 0 } fid) byFlocknote[fid] = person;
            }
        }

        int linked = 0, unchanged = 0, skipped = 0, notInRegistry = 0;
        foreach (var group in members.GroupBy(m => m.PdsId!, StringComparer.OrdinalIgnoreCase))
        {
            var pdsId = group.Key;
            if (!byPds.TryGetValue(pdsId, out var people))
            {
                notInRegistry += group.Count();
                foreach (var m in group)
                    FlocknoteNote(m.Row, "info", "no-registry-person", m.FlocknoteId, "Member Unique ID (PDS)", $"{m.Display}: no registry person has PDS ID {pdsId}; not linked");
                continue;
            }
            if (people.Count > 1)
            {
                skipped += group.Count();
                FlocknoteNote(group.First().Row, "warning", "skipped-ambiguous", group.First().FlocknoteId, "PdsMemberId",
                    $"PDS ID {pdsId} is held by {people.Count} registry people: {string.Join("; ", people.Select(p => p.Describe))}. ACTION: merge the duplicates, then rerun");
                continue;
            }

            var person = people[0];
            var agreeing = group.Where(m => NamesAgree(person, m)).ToList();
            if (agreeing.Count != 1)
            {
                skipped += group.Count();
                var reason = agreeing.Count == 0
                    ? "the Flocknote name does not agree with the registry name"
                    : $"{agreeing.Count} Flocknote members share this PDS ID and name";
                foreach (var m in group)
                    FlocknoteNote(m.Row, "warning", "skipped-name-mismatch", m.FlocknoteId, "Name",
                        $"{m.Display} -> {person.Describe}: {reason}; not linked. ACTION: link manually in Sacramental Records");
                continue;
            }

            var member = agreeing[0];
            foreach (var other in group.Where(m => m != member))
                FlocknoteNote(other.Row, "warning", "skipped-shared-pds-id", other.FlocknoteId, "Member Unique ID (PDS)",
                    $"{other.Display} shares PDS ID {pdsId} with {member.Display}, whose name matches {person.Describe}; this row was not linked. ACTION: correct the PDS ID in Flocknote");

            if (string.Equals(person.FlocknoteId, member.FlocknoteId, StringComparison.OrdinalIgnoreCase))
            {
                unchanged++;
                continue;
            }
            if (person.FlocknoteId is { Length: > 0 } existing)
            {
                skipped++;
                FlocknoteNote(member.Row, "warning", "flocknote-conflict", member.FlocknoteId, "FlocknoteId",
                    $"{person.Describe} is already linked to Flocknote ID {existing}; Flocknote now reports {member.FlocknoteId}; existing link kept. ACTION: verify in Flocknote");
                continue;
            }
            if (byFlocknote.TryGetValue(member.FlocknoteId, out var holder) && holder.Id != person.Id)
            {
                skipped++;
                FlocknoteNote(member.Row, "warning", "flocknote-id-in-use", member.FlocknoteId, "FlocknoteId",
                    $"{member.Display} is already linked to {holder.Describe}; not linked to {person.Describe}. ACTION: verify which person is correct");
                continue;
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE People SET FlocknoteId=$fid,UpdatedUtc=$updated WHERE RecordId=$id";
            Bind(update, "$fid", member.FlocknoteId);
            Bind(update, "$updated", DateTimeOffset.UtcNow.ToString("O"));
            Bind(update, "$id", person.Id.ToString());
            update.ExecuteNonQuery();
            byFlocknote[member.FlocknoteId] = person with { FlocknoteId = member.FlocknoteId };
            linked++;
            FlocknoteNote(member.Row, "info", "link", member.FlocknoteId, "FlocknoteId", $"{member.Display} -> {person.Describe}");
        }

        if (options.Commit && _errors == 0)
        {
            WriteAuditEvent(connection, transaction, members.Count + withoutPds, 0, 0,
                $"Flocknote member link: {members.Count + withoutPds} rows, {linked} people linked, {unchanged} already linked, {skipped} skipped");
            transaction.Commit();
        }
        else transaction.Rollback();

        WriteReport(options.Report, "flocknote-members");
        Console.WriteLine($"""
            Flocknote rows read: {members.Count + withoutPds}; rows without a PDS ID (not linkable): {withoutPds}
            People {(options.Commit ? "linked" : "to link")}: {linked}; already linked: {unchanged}
            Rows skipped for review: {skipped}; rows whose PDS ID is not in the registry: {notInRegistry}
            Warnings: {_warnings}
            Errors: {_errors}
            Report: {Path.GetFullPath(options.Report)}
            {(options.Commit
                ? _errors == 0 ? "Import committed." : "Import rolled back because errors were found."
                : "Dry run only; no database changes were made.")}
            """);
        return _errors == 0 ? 0 : 2;
    }

    private static List<FlocknoteMember> ReadFlocknoteMembers(string path, out int withoutPds)
    {
        using var book = new XLWorkbook(path);
        var sheet = book.Worksheets.First();
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in sheet.FirstRowUsed()!.CellsUsed()) headers.TryAdd(cell.GetString().Trim(), cell.Address.ColumnNumber);
        var missing = new[] { FlocknoteIdHeader, FlocknotePdsHeader, "First Name", "Last Name" }.Where(h => !headers.ContainsKey(h)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"Flocknote member export is missing required column(s): {string.Join(", ", missing)}.");

        var members = new List<FlocknoteMember>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        withoutPds = 0;
        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            string? F(string header) => headers.TryGetValue(header, out var column) ? Clean(CellText(row.Cell(column))) : null;
            var fid = F(FlocknoteIdHeader);
            if (fid is null) continue;
            if (!seen.Add(fid))
            {
                FlocknoteNote(row.RowNumber(), "warning", "skipped-duplicate-flocknote-id", fid, FlocknoteIdHeader, "this Flocknote ID appears on an earlier row; row skipped");
                continue;
            }
            var pds = F(FlocknotePdsHeader);
            if (pds is null) { withoutPds++; continue; }

            var first = F("First Name") ?? string.Empty;
            var last = F("Last Name") ?? string.Empty;
            var firstNames = new HashSet<string>(StringComparer.Ordinal) { Normalize(FirstToken(first)) };
            if (F("Nickname") is { } nickname) firstNames.Add(Normalize(FirstToken(nickname)));
            if (F("Informal Sal") is { } informal) firstNames.Add(Normalize(FirstToken(informal)));
            var surnames = new HashSet<string>(StringComparer.Ordinal) { Normalize(last) };
            foreach (var match in Parenthesized.Matches(last).Cast<System.Text.RegularExpressions.Match>()) surnames.Add(Normalize(match.Groups[1].Value));
            surnames.Add(Normalize(Parenthesized.Replace(last, " ")));
            if (F("Maiden Name") is { } maiden) surnames.Add(Normalize(maiden));
            firstNames.Remove(string.Empty);
            surnames.Remove(string.Empty);
            members.Add(new FlocknoteMember(row.RowNumber(), fid, pds, first, last, firstNames, surnames));
        }
        return members;
    }

    private static bool NamesAgree(LinkCandidate person, FlocknoteMember member)
    {
        var first = Normalize(FirstToken(person.Given));
        var registrySurnames = new[] { person.Family, person.Maiden }.OfType<string>().Select(Normalize).Where(s => s.Length > 0).ToList();
        var firstAgrees = member.FirstNames.Contains(first);
        var surnameAgrees = registrySurnames.Any(db => member.Surnames.Any(s => s == db || s.Length >= 5 && db.Length >= 5 && Levenshtein(s, db) <= 1));
        // A changed surname (marriage) or a nickname alone is normal; both disagreeing means a different person.
        return firstAgrees || surnameAgrees;
    }

    private static void FlocknoteNote(int row, string severity, string action, string flocknoteId, string field, string message)
    {
        if (severity == "warning") _warnings++;
        Report.Add(Csv("Flocknote", row.ToString(CultureInfo.InvariantCulture), "person", severity, action, flocknoteId, field, message));
    }
}
