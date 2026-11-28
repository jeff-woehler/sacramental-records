using System.Globalization;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private sealed record FlocknoteSacrament(
        int Row, string? FlocknoteId, string Name, string SacramentType, 
        Dictionary<string, string?> Fields)
    {
        public string Display => $"{Name} ({FlocknoteId})";
        public DateOnly? Date => Fields.TryGetValue("SacramentDate", out var raw) && raw is not null && TryParseDate(raw, out var d) ? d : null;
    }

    private sealed record ExchangePerson(Guid Id, string Given, string? Middle, string Family, string? PdsId, string? FlocknoteId)
    {
        public string Describe => $"{Given} {Middle} {Family} (RecordId {Id})".Replace("  ", " ");
    }

    private static bool IsFlocknoteExchangeFile(string path)
    {
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) return false;
        
        try
        {
            using var book = new XLWorkbook(path);
            var header = book.Worksheets.FirstOrDefault()?.FirstRowUsed();
            if (header is null) return false;
            var names = header.CellsUsed().Select(c => c.GetString().Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            
            // Must have flocknoteID and at least one sacrament date column
            if (!names.Contains("flocknoteID") && !names.Contains("Flocknote ID")) return false;
            
            // Check for sacrament columns (Baptism, First Communion, Confirmation, Marriage)
            var hasSacrament = names.Any(n => 
                n.Contains("Baptism Date", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("First Communion Date", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Confirmation Date", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Marriage Date", StringComparison.OrdinalIgnoreCase));
            
            return hasSacrament;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Imports sacrament data from a Flocknote Sacrament export. Matches by Flocknote ID, then PDS ID.</summary>
    private static int RunFlocknoteExchange(Options options)
    {
        var sacraments = ReadFlocknoteExchangeFile(options.Input);

        using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(options.Database)}");
        connection.Open();
        Initialize(connection);
        using var transaction = connection.BeginTransaction();

        // Load existing people
        var byFlocknoteId = new Dictionary<string, ExchangePerson>(StringComparer.OrdinalIgnoreCase);
        var byPdsId = new Dictionary<string, List<ExchangePerson>>(StringComparer.OrdinalIgnoreCase);
        var allPeople = new List<ExchangePerson>();
        
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT RecordId,GivenName,MiddleName,FamilyName,PdsMemberId,FlocknoteId FROM People";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var id)) continue;
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i).Trim();
                var person = new ExchangePerson(id, Text(1) ?? "", Text(2), Text(3) ?? "", Text(4), Text(5));
                allPeople.Add(person);
                if (!string.IsNullOrEmpty(person.PdsId)) 
                {
                    if (!byPdsId.ContainsKey(person.PdsId))
                        byPdsId[person.PdsId] = new List<ExchangePerson>();
                    byPdsId[person.PdsId].Add(person);
                }
                if (!string.IsNullOrEmpty(person.FlocknoteId)) byFlocknoteId[person.FlocknoteId] = person;
            }
        }

        // Process each Flocknote sacrament
        int mergedCount = 0;
        int skippedCount = 0;

        foreach (var sac in sacraments)
        {
            // Match person
            ExchangePerson? person = null;
            string? matchReason = null;

            if (sac.FlocknoteId is not null && byFlocknoteId.TryGetValue(sac.FlocknoteId, out var byFid))
            {
                person = byFid;
                matchReason = "matched-by-flocknote-id";
            }
            else if (sac.FlocknoteId is null)
            {
                FlocknoteExchangeNote(sac.Row, "warning", "skipped-no-flocknote-id", "", "Flocknote ID", 
                    "Row has no Flocknote ID; cannot link");
                skippedCount++;
                continue;
            }
            else
            {
                FlocknoteExchangeNote(sac.Row, "warning", "skipped-unmatched", sac.FlocknoteId, "Flocknote ID",
                    $"{sac.Display}: no registry person is linked to this Flocknote ID");
                skippedCount++;
                continue;
            }

            // Insert or update sacrament
            try
            {
                var sac_id = Guid.NewGuid().ToString();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO Sacraments(RecordId,PersonRecordId,SacramentType,SacramentDate,Name,Place,Minister,
                        FatherName,MotherName,SponsorOne,SponsorTwo,Volume,Page,Item,Details,SacramentDatePrecision,UpdatedUtc)
                    VALUES($id,$person,$type,$date,$name,$place,$minister,$father,$mother,$s1,$s2,$vol,$page,$item,$details,$precision,$when)
                    """;
                
                Bind(command, "$id", sac_id);
                Bind(command, "$person", person.Id.ToString());
                Bind(command, "$type", sac.SacramentType);
                Bind(command, "$date", sac.Fields.GetValueOrDefault("SacramentDate"));
                Bind(command, "$name", sac.Fields.GetValueOrDefault("Name"));
                Bind(command, "$place", sac.Fields.GetValueOrDefault("Place"));
                Bind(command, "$minister", sac.Fields.GetValueOrDefault("Minister"));
                Bind(command, "$father", sac.Fields.GetValueOrDefault("FatherName"));
                Bind(command, "$mother", sac.Fields.GetValueOrDefault("MotherName"));
                Bind(command, "$s1", sac.Fields.GetValueOrDefault("SponsorOne"));
                Bind(command, "$s2", sac.Fields.GetValueOrDefault("SponsorTwo"));
                Bind(command, "$vol", sac.Fields.GetValueOrDefault("Volume"));
                Bind(command, "$page", sac.Fields.GetValueOrDefault("Page"));
                Bind(command, "$item", sac.Fields.GetValueOrDefault("Item"));
                Bind(command, "$details", sac.Fields.GetValueOrDefault("Details"));
                Bind(command, "$precision", "day");  // Flocknote assumed to have full precision
                Bind(command, "$when", DateTimeOffset.UtcNow.ToString("O"));
                
                command.ExecuteNonQuery();
                mergedCount++;
                
                FlocknoteExchangeNote(sac.Row, "info", matchReason, sac.FlocknoteId, sac.SacramentType,
                    $"{sac.Display} -> {person.Describe}");
            }
            catch (Exception ex)
            {
                _errors++;
                FlocknoteExchangeNote(sac.Row, "error", "merge-failed", sac.FlocknoteId ?? "", sac.SacramentType,
                    $"{sac.Display}: {ex.Message}");
            }
        }

        if (options.Commit && _errors == 0)
        {
            WriteAuditEvent(connection, transaction, sacraments.Count, 0, mergedCount, 
                $"Flocknote Exchange: {sacraments.Count} rows, {mergedCount} sacraments merged");
            transaction.Commit();
        }
        else if (!options.Commit || _errors > 0)
        {
            transaction.Rollback();
        }

        Console.WriteLine($"""
            Flocknote Sacrament Exchange
            Rows processed: {sacraments.Count}
            Sacraments merged: {mergedCount}
            Skipped: {skippedCount}
            Warnings: {_warnings}
            Errors: {_errors}
            {(_errors == 0 ? (options.Commit ? "Import committed." : "Dry-run only; use --commit to write changes.") : "Import rolled back because errors were found.")}
            """);

        WriteReport(options.Report, "Flocknote Exchange");
        return _errors == 0 ? 0 : 2;
    }

    private static List<FlocknoteSacrament> ReadFlocknoteExchangeFile(string path)
    {
        var sacraments = new List<FlocknoteSacrament>();
        
        using var book = new XLWorkbook(path);
        var sheet = book.Worksheets.FirstOrDefault();
        if (sheet is null) return sacraments;

        var headerRow = sheet.FirstRowUsed();
        if (headerRow is null) return sacraments;

        // Build column index (case-insensitive)
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            var text = cell.GetString().Trim();
            if (text.Length > 0)
                headers[text] = cell.Address.ColumnNumber;
        }

        // Find flocknote ID column
        string? flocknoteIdHeader = headers.Keys.FirstOrDefault(k => 
            k.Equals("flocknoteID", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("Flocknote ID", StringComparison.OrdinalIgnoreCase));
        
        if (flocknoteIdHeader is null)
        {
            FlocknoteExchangeNote(-1, "error", "missing-flocknote-id-column", "", "", 
                "No 'Flocknote ID' or 'flocknoteID' column found");
            return sacraments;
        }

        // Read data rows and extract sacraments
        int rowNum = 2;
        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var flocknoteId = GetCellText(row, headers, flocknoteIdHeader)?.Trim();
            var firstName = GetCellText(row, headers, "First Name")?.Trim() ?? "";
            var lastName = GetCellText(row, headers, "Last Name")?.Trim() ?? "";
            var name = $"{firstName} {lastName}".Trim();
            
            if (string.IsNullOrEmpty(flocknoteId))
            {
                rowNum++;
                continue;
            }

            // Extract baptism sacrament if date exists
            if (GetCellText(row, headers, "Baptism Date") is { Length: > 0 } baptismDate)
            {
                var sac = new FlocknoteSacrament(rowNum, flocknoteId, name, Baptism, new Dictionary<string, string?>
                {
                    { "SacramentDate", baptismDate },
                    { "Place", GetCellText(row, headers, "Baptism Location") },
                    { "Minister", GetCellText(row, headers, "Baptism Celebrant") },
                    { "SponsorOne", GetCellText(row, headers, "Godparents") },
                    { "Volume", GetCellText(row, headers, "Baptism Record Book") },
                    { "Page", GetCellText(row, headers, "Baptism Record Book Page") },
                    { "Item", GetCellText(row, headers, "Baptism Record Book Entry") },
                    { "Details", GetCellText(row, headers, "Baptism Notes") }
                });
                sacraments.Add(sac);
            }

            // Extract first communion if date exists
            if (GetCellText(row, headers, "First Communion Date") is { Length: > 0 } commDate)
            {
                var sac = new FlocknoteSacrament(rowNum, flocknoteId, name, FirstCommunion, new Dictionary<string, string?>
                {
                    { "SacramentDate", commDate },
                    { "Place", GetCellText(row, headers, "First Communion Location") },
                    { "Minister", GetCellText(row, headers, "First Communion Celebrant") },
                    { "Volume", GetCellText(row, headers, "First Communion Record Book") },
                    { "Page", GetCellText(row, headers, "First Communion Record Book Page") },
                    { "Item", GetCellText(row, headers, "First Communion Record Book Entry") },
                    { "Details", GetCellText(row, headers, "First Communion Notes") }
                });
                sacraments.Add(sac);
            }

            // Extract confirmation if date exists
            if (GetCellText(row, headers, "Confirmation Date") is { Length: > 0 } confDate)
            {
                var sac = new FlocknoteSacrament(rowNum, flocknoteId, name, Confirmation, new Dictionary<string, string?>
                {
                    { "SacramentDate", confDate },
                    { "Name", GetCellText(row, headers, "Confirmation Name") },
                    { "Place", GetCellText(row, headers, "Confirmation Location") },
                    { "Minister", GetCellText(row, headers, "Confirmation Celebrant") },
                    { "SponsorOne", GetCellText(row, headers, "Confirmation Sponsor") },
                    { "Volume", GetCellText(row, headers, "Confirmation Record Book") },
                    { "Page", GetCellText(row, headers, "Confirmation Record Book Page") },
                    { "Item", GetCellText(row, headers, "Confirmation Record Book Entry") },
                    { "Details", GetCellText(row, headers, "Confirmation Notes") }
                });
                sacraments.Add(sac);
            }

            // Extract marriage if date exists
            if (GetCellText(row, headers, "Marriage Date") is { Length: > 0 } marDate)
            {
                var sac = new FlocknoteSacrament(rowNum, flocknoteId, name, Marriage, new Dictionary<string, string?>
                {
                    { "SacramentDate", marDate },
                    { "Name", GetCellText(row, headers, "Marriage Spouse") },
                    { "Place", GetCellText(row, headers, "Marriage Location") },
                    { "Minister", GetCellText(row, headers, "Marriage Celebrant") },
                    { "SponsorOne", GetCellText(row, headers, "Marriage Witnesses") },
                    { "Volume", GetCellText(row, headers, "Marriage Record Book") },
                    { "Page", GetCellText(row, headers, "Marriage Record Book Page") },
                    { "Item", GetCellText(row, headers, "Marriage Record Book Entry") },
                    { "Details", GetCellText(row, headers, "Marriage Notes") }
                });
                sacraments.Add(sac);
            }

            // Extract holy orders if date exists (for completeness)
            if (GetCellText(row, headers, "Holy Orders Date") is { Length: > 0 } holyDate)
            {
                var sac = new FlocknoteSacrament(rowNum, flocknoteId, name, "holy_orders", new Dictionary<string, string?>
                {
                    { "SacramentDate", holyDate },
                    { "Place", GetCellText(row, headers, "Holy Orders Location") },
                    { "Minister", GetCellText(row, headers, "Holy Orders Celebrant") },
                    { "Volume", GetCellText(row, headers, "Holy Orders Record Book") },
                    { "Page", GetCellText(row, headers, "Holy Orders Record Book Page") },
                    { "Item", GetCellText(row, headers, "Holy Orders Record Book Entry") },
                    { "Details", GetCellText(row, headers, "Holy Orders Notes") }
                });
                sacraments.Add(sac);
            }

            rowNum++;
        }

        return sacraments;
    }

    private static string? GetCellText(IXLRow row, Dictionary<string, int> headers, string headerName)
    {
        if (!headers.TryGetValue(headerName, out var col)) return null;
        var text = row.Cell(col).GetString().Trim();
        return text.Length > 0 ? text : null;
    }

    private static void FlocknoteExchangeNote(int row, string severity, string action, string? personId, string field, string message)
    {
        if (severity == "error") _errors++;
        else if (severity == "warning") _warnings++;
        
        Report.Add(Csv("Flocknote Exchange", row < 0 ? string.Empty : row.ToString(CultureInfo.InvariantCulture), 
            "", severity, action, personId ?? string.Empty, field, message));
    }
}
