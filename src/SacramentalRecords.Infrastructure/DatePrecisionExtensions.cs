using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SacramentalRecords.Infrastructure;

/// <summary>
/// Extensions for formatting dates with precision information from the database.
/// Supports partial dates where only year or year-month information is available.
/// </summary>
public static class DatePrecisionExtensions
{
    /// <summary>
    /// Reads a date and its precision from a database record and formats it for display.
    /// Precision determines how the date is displayed: "ca. 1965" for year-only, "1965-10" for year-month, etc.
    /// </summary>
    /// <param name="reader">The SqliteDataReader to read from.</param>
    /// <param name="dateColumnName">The name of the date column (e.g., "BirthDate").</param>
    /// <param name="precisionColumnName">The name of the precision column (e.g., "BirthDatePrecision").</param>
    /// <returns>A formatted display string respecting precision, or null if the date column is null/DBNull.</returns>
    public static string? GetFormattedDateWithPrecision(
        this SqliteDataReader reader,
        string dateColumnName,
        string precisionColumnName)
    {
        try
        {
            var dateIndex = reader.GetOrdinal(dateColumnName);
            var dateValue = reader.IsDBNull(dateIndex) ? null : reader.GetString(dateIndex);
            
            if (string.IsNullOrWhiteSpace(dateValue))
                return null;
            
            var precisionIndex = -1;
            var precision = "day";
            try
            {
                precisionIndex = reader.GetOrdinal(precisionColumnName);
                if (!reader.IsDBNull(precisionIndex))
                    precision = reader.GetString(precisionIndex) ?? "day";
            }
            catch (IndexOutOfRangeException)
            {
                // Column doesn't exist; default to "day"
            }
            
            return FormatDateWithPrecision(dateValue, precision);
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Formats a date string according to its precision level.
    /// </summary>
    /// <param name="dateValue">ISO date string (YYYY-MM-DD format).</param>
    /// <param name="precision">Precision level: "day", "year-month", or "year-only".</param>
    /// <returns>A formatted display string, or the original date if precision is unrecognized.</returns>
    public static string FormatDateWithPrecision(string? dateValue, string? precision = "day")
    {
        if (string.IsNullOrWhiteSpace(dateValue))
            return string.Empty;
        
        // Try to parse as DateOnly
        if (!DateOnly.TryParse(dateValue, out var date))
            return dateValue;
        
        // Format based on precision
        return (precision ?? "day").ToLowerInvariant() switch
        {
            "year-only" => $"ca. {date.Year}",
            "year-month" => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
    }
}
