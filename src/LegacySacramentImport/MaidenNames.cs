using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static readonly Dictionary<string, int> GivenNameCounts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> SurnameCounts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> FemaleNameCounts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> MaleNameCounts = new(StringComparer.Ordinal);

    private static readonly Regex GivenTrailingParenthesis = new(@"^(.*\S)\s*\(([^)]*)\)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex SurnameLeadingParenthesis = new(@"^\s*\(([^)]*)\)\s*(\S.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex SurnameTrailingParenthesis = new(@"^(.*?[^\s,])[\s,]*\(([^)]*)\)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex NeePrefix = new(@"^n[eé]e?\b[\s~.:]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Learns which tokens are given names, surnames, and female/male names from the rows being imported.</summary>
    private static void BuildNameStatistics(List<SourceRow> rows)
    {
        static void Count(Dictionary<string, int> counts, string? token)
        {
            if (!string.IsNullOrEmpty(token)) counts[token] = counts.GetValueOrDefault(token) + 1;
        }
        void FullName(string? name, Dictionary<string, int>? gender)
        {
            var tokens = NameTokens(name);
            if (tokens.Count < 2) return;
            Count(GivenNameCounts, tokens[0]);
            Count(SurnameCounts, tokens[^1]);
            if (gender is not null) Count(gender, tokens[0]);
        }

        foreach (var row in rows)
        {
            foreach (var token in NameTokens(Name(row, FirstColumns))) Count(GivenNameCounts, token);
            foreach (var token in NameTokens(Name(row, LastColumns))) Count(SurnameCounts, token);
            FullName(Value(row, row.Map.Father), MaleNameCounts);
            FullName(Value(row, row.Map.Mother), FemaleNameCounts);
            if (row.Map.Type == Baptism)
            {
                FullName(Value(row, row.Map.SponsorOne), MaleNameCounts);
                FullName(Value(row, row.Map.SponsorTwo), FemaleNameCounts);
            }
            if (row.Values.GetValueOrDefault("__WeddingSide") is { } side)
                Count(side == "B" ? FemaleNameCounts : MaleNameCounts, NameTokens(Name(row, FirstColumns)).FirstOrDefault());
        }
    }

    private static List<string> NameTokens(string? name) =>
        name is null
            ? []
            : Parenthesized.Replace(name, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize).Where(t => t.Length > 1).ToList();

    /// <summary>
    /// Pulls a parenthesized surname out of the name fields ("Mary (Smith) Jones", "Jones (nee Smith)") when it
    /// looks like a woman's maiden name rather than a nickname, an alternate spelling, or a man's other surname.
    /// </summary>
    private static (string Given, string Family, string? Maiden) GuessMaidenName(SourceRow row, string given, string family)
    {
        string candidate, newGiven = given, newFamily = family;
        bool inSurname;
        if (SurnameLeadingParenthesis.Match(family) is { Success: true } lead)
            (candidate, newFamily, inSurname) = (lead.Groups[1].Value, lead.Groups[2].Value.Trim(), true);
        else if (SurnameTrailingParenthesis.Match(family) is { Success: true } trail)
            (candidate, newFamily, inSurname) = (trail.Groups[2].Value, trail.Groups[1].Value.Trim(), true);
        else if (GivenTrailingParenthesis.Match(given) is { Success: true } end)
            (candidate, newGiven, inSurname) = (end.Groups[2].Value, end.Groups[1].Value.Trim(), false);
        else return (given, family, null);

        candidate = candidate.Trim();
        var nee = NeePrefix.Match(candidate);
        if (nee.Success) candidate = candidate[nee.Length..].Trim();
        if (!candidate.Any(char.IsLetter) || Clean(candidate) is null) return (given, family, null);

        var key = Normalize(candidate);
        var personId = Value(row, IdColumns);
        if (!nee.Success)
        {
            // Several words in parentheses are alternate forenames ("Ann (Lillian Anna)"), not a surname.
            if (candidate.Contains(' ')) return (given, family, null);
            var asSurname = SurnameCounts.GetValueOrDefault(key);
            var asGiven = GivenNameCounts.GetValueOrDefault(key);
            if (inSurname ? asSurname < asGiven : asSurname <= asGiven) return (given, family, null);
            if (IsSpellingVariant(key, Normalize(newFamily)))
            {
                MaidenNote(row, "info", "surname-variant", personId, $"'{candidate}' looks like a spelling variant of '{newFamily}'; name kept as written");
                return (given, family, null);
            }
            if (Gender(row, newGiven) == "male") return (given, family, null);
        }

        var confident = nee.Success || Gender(row, newGiven) == "female";
        MaidenNote(row, confident ? "info" : "warning", confident ? "maiden-name" : "maiden-name-guess", personId,
            $"'{given} {family}' -> name '{newGiven} {newFamily}', maiden name '{candidate}'" +
            (confident ? string.Empty : "; gender could not be inferred from the forenames. ACTION: verify"));
        return (newGiven, newFamily, candidate);
    }

    private static string? Gender(SourceRow row, string given)
    {
        if (row.Values.GetValueOrDefault("__WeddingSide") is { } side) return side == "B" ? "female" : "male";
        // Unusual first names are common, so middle names count too ("Evadean Mary").
        var votes = NameTokens(given).Select(token =>
        {
            var female = FemaleNameCounts.GetValueOrDefault(token);
            var male = MaleNameCounts.GetValueOrDefault(token);
            return female >= 3 && female > 4 * male ? "female" : male >= 3 && male > 4 * female ? "male" : null;
        }).OfType<string>().Distinct().ToList();
        return votes.Count == 1 ? votes[0] : null;
    }

    /// <summary>Young/Jung, Klee/Klueh: same consonant skeleton, so a respelling rather than another family.</summary>
    private static bool IsSpellingVariant(string left, string right)
    {
        static string Skeleton(string s)
        {
            var result = new StringBuilder(s.Length > 0 ? s[..1] : string.Empty);
            foreach (var c in s.Skip(1))
                if (!"AEIOUYHW".Contains(c) && (result.Length == 0 || result[^1] != c)) result.Append(c);
            return result.ToString();
        }
        if (left.Length == 0 || right.Length == 0) return false;
        return Levenshtein(left, right) <= 2 || Levenshtein(Skeleton(left), Skeleton(right)) <= 1;
    }

    private static void FillMaidenName(SqliteConnection connection, SqliteTransaction transaction, Guid personId, string maiden)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE People SET MaidenName=$maiden WHERE RecordId=$id AND MaidenName IS NULL";
        Bind(command, "$maiden", maiden);
        Bind(command, "$id", personId.ToString());
        command.ExecuteNonQuery();
    }

    private static void MaidenNote(SourceRow row, string severity, string action, string? personId, string message)
    {
        if (severity == "warning") _warnings++;
        Report.Add(Csv(row.Sheet, row.Number.ToString(CultureInfo.InvariantCulture), row.Map.Type, severity, action,
            personId ?? string.Empty, "FirstName/Surname", message));
    }
}
