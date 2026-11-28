namespace SacramentalRecords.Domain;

/// <summary>
/// Indicates the precision/completeness level of a partial date.
/// Used to preserve historical accuracy when only year or year-month information is available.
/// </summary>
public enum DatePrecision
{
    /// <summary>Full date with year, month, and day.</summary>
    Day = 0,
    
    /// <summary>Partial date with only year and month (no day).</summary>
    YearMonth = 1,
    
    /// <summary>Partial date with only year (no month or day).</summary>
    YearOnly = 2
}

/// <summary>
/// Represents a date with explicit precision information, supporting partial dates where only
/// year or year-month information is available. The underlying DateOnly is set to a default day
/// (day 1 for year-month, day 1 of Jan 1 for year-only) but the Precision property indicates
/// whether the full date is meaningful.
/// </summary>
public readonly struct PartialDateOnly : IEquatable<PartialDateOnly>
{
    /// <summary>The date value. For partial dates, the missing components default to 1 (Jan 1 for year-only, day 1 for year-month).</summary>
    public DateOnly Date { get; }
    
    /// <summary>Indicates the precision level of this date.</summary>
    public DatePrecision Precision { get; }
    
    private PartialDateOnly(DateOnly date, DatePrecision precision)
    {
        Date = date;
        Precision = precision;
    }
    
    /// <summary>Creates a full-precision date (year, month, and day are meaningful).</summary>
    public static PartialDateOnly CreateFullDate(DateOnly date) => new(date, DatePrecision.Day);
    
    /// <summary>Creates a year-month precision date. Day is set to 1.</summary>
    public static PartialDateOnly CreateYearMonth(int year, int month) => 
        new(new DateOnly(year, month, 1), DatePrecision.YearMonth);
    
    /// <summary>Creates a year-only precision date. Month and day are set to 1 (January 1).</summary>
    public static PartialDateOnly CreateYearOnly(int year) => 
        new(new DateOnly(year, 1, 1), DatePrecision.YearOnly);
    
    /// <summary>Gets a string representation respecting precision. E.g., "ca. 1965" for year-only, "1965-10" for year-month.</summary>
    public string ToDisplayString() => Precision switch
    {
        DatePrecision.YearOnly => $"ca. {Date.Year}",
        DatePrecision.YearMonth => $"{Date.Year}-{Date.Month:D2}",
        _ => Date.ToString("yyyy-MM-dd")
    };
    
    /// <summary>Gets an ISO-like string representation: "1965" for year-only, "1965-10" for year-month, "1965-10-15" for full date.</summary>
    public string ToIsoString() => Precision switch
    {
        DatePrecision.YearOnly => $"{Date.Year}",
        DatePrecision.YearMonth => $"{Date.Year}-{Date.Month:D2}",
        _ => Date.ToString("yyyy-MM-dd")
    };
    
    /// <summary>Parses a date string with precision. Supports "1965", "1965-10", and "1965-10-15" formats.</summary>
    public static bool TryParse(string? text, out PartialDateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        
        var trimmed = text.Trim();
        
        // Try YYYY format
        if (trimmed.Length == 4 && int.TryParse(trimmed, out var yearOnly) && yearOnly >= 1800)
        {
            result = CreateYearOnly(yearOnly);
            return true;
        }
        
        // Try YYYY-MM format
        if (trimmed.Length == 7 && trimmed[4] == '-' && 
            int.TryParse(trimmed.Substring(0, 4), out var year) &&
            int.TryParse(trimmed.Substring(5, 2), out var month) &&
            year >= 1800 && month >= 1 && month <= 12)
        {
            result = CreateYearMonth(year, month);
            return true;
        }
        
        // Try YYYY-MM-DD format
        if (DateOnly.TryParse(trimmed, out var fullDate))
        {
            result = CreateFullDate(fullDate);
            return true;
        }
        
        return false;
    }
    
    public bool Equals(PartialDateOnly other) => Date == other.Date && Precision == other.Precision;
    public override bool Equals(object? obj) => obj is PartialDateOnly other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Date, Precision);
    public override string ToString() => ToDisplayString();
    
    public static bool operator ==(PartialDateOnly left, PartialDateOnly right) => left.Equals(right);
    public static bool operator !=(PartialDateOnly left, PartialDateOnly right) => !left.Equals(right);
}

public sealed record Person(
    Guid RecordId,
    string GivenName,
    string? MiddleName,
    string FamilyName,
    DateOnly? BirthDate,
    string? ChurchDbId,
    string? BirthCity,
    DateOnly? BaptismDate,
    string? BaptismChurch,
    string Status,
    string? PdsMemberId,
    string? FlocknoteId,
    DateTimeOffset UpdatedUtc,
    string? MaidenName = null,
    DateOnly? DeceasedDate = null);

public sealed record SacramentRecord(
    Guid RecordId,
    Guid PersonRecordId,
    string SacramentType,
    DateOnly? SacramentDate,
    string? Name,
    string? Place,
    string? Minister,
    string? FatherName,
    string? MotherName,
    string? SponsorOne,
    string? SponsorTwo,
    bool SponsorTwoIsChristianWitness,
    string? ProxyOne,
    string? ProxyTwo,
    string? Volume,
    string? Page,
    string? Item,
    string? Details,
    string? CanonicalNotations,
    DateTimeOffset UpdatedUtc);

public sealed record AuditEvent(Guid EventId, Guid RecordId, string User, string Operation, DateTimeOffset OccurredUtc, string Summary);
