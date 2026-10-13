using System.Globalization;
using System.IO;
using System.Text;
using SacramentalRecords.Domain;

namespace SacramentalRecords.App;

/// <summary>
/// Writes the baptism certificate as a self-contained, landscape letter-size PDF.
/// Keeping the renderer here avoids making the registry dependent on a document package.
/// </summary>
internal static class BaptismCertificateGenerator
{
    private const double PageWidth = 792;
    private const double PageHeight = 612;

    public static void Write(string fileName, Person person, SacramentRecord baptism)
    {
        var fullName = Join(person.GivenName, person.MiddleName, person.FamilyName);
        var certificate = new StringBuilder();

        CenteredText(certificate, 58, 28, "Certificate of Baptism", false);
        CenteredText(certificate, 112, 19, fullName, false);
        Line(certificate, 116, 128, 676, 128);

        CenteredText(certificate, 164, 14, $"Child of {Value(baptism.FatherName)} and {Value(baptism.MotherName)}", false);
        CenteredText(certificate, 204, 14, $"Born in {Value(person.BirthCity)}", false);
        CenteredText(certificate, 238, 14, $"on the {OrdinalDay(person.BirthDate)} day of {Month(person.BirthDate)}", false);

        CenteredText(certificate, 294, 22, "Baptized", false);
        CenteredText(certificate, 342, 14, $"on the {OrdinalDay(baptism.SacramentDate)} day of {Month(baptism.SacramentDate)}", false);
        CenteredText(certificate, 386, 13, $"According to the Rite of the Roman Catholic Church by {Value(baptism.Minister)}", false);
        CenteredText(certificate, 426, 13, $"The Sponsors being {Value(baptism.SponsorOne)} and {Value(baptism.SponsorTwo)}", false);

        Line(certificate, 110, 456, 682, 456);
        CenteredText(certificate, 480, 11, "This certifies that the baptism is recorded in the parish register", false);
        CenteredText(certificate, 504, 11, $"in volume {Value(baptism.Volume)} on page {Value(baptism.Page)}.", false);
        CenteredText(certificate, 536, 11, "St. Joseph Jasper, 1029 Kundek Avenue, Jasper, IN 47546", false);
        CenteredText(certificate, 564, 11, $"Date: {DateTime.Today:MMMM d, yyyy}", false);

        var notations = new StringBuilder();
        CenteredText(notations, 42, 11, "ST. JOSEPH PARISH", true);
        CenteredText(notations, 74, 25, "NOTATIONS", true);
        Line(notations, 54, 100, 738, 100);
        Field(notations, 76, 126, 420, "NAME", fullName);
        Field(notations, 520, 126, 196, "DATE OF BAPTISM", FormatDate(baptism.SacramentDate));
        Field(notations, 76, 174, 640, "REGISTER REFERENCE", $"Volume {Value(baptism.Volume)}   Page {Value(baptism.Page)}   Number {Value(baptism.Item)}");
        Rectangle(notations, 54, 222, 684, 300);
        Text(notations, 76, 244, 9, "CANONICAL NOTATIONS", true);
        WrappedText(notations, 76, 270, 11, Value(baptism.CanonicalNotations), 640, 19);
        CenteredText(notations, 558, 9, "This page is part of the baptismal record certified on the preceding page.", false);

        File.WriteAllBytes(fileName, BuildPdf(certificate.ToString(), notations.ToString()));
    }

    private static void Field(StringBuilder content, double x, double top, double width, string label, string? value)
    {
        Text(content, x, top, 8, label, false);
        Text(content, x, top + 18, 11, Fit(Value(value), width, 11), false);
    }

    private static void CenteredText(StringBuilder content, double top, int size, string? value, bool bold, double left = 54, double right = 738)
    {
        var textValue = Value(value);
        var x = Math.Max(left, left + ((right - left - TextWidth(textValue, size, bold)) / 2));
        Text(content, x, top, size, textValue, bold);
    }

    private static void Text(StringBuilder content, double x, double top, int size, string value, bool bold)
    {
        var font = bold ? "/F2" : "/F1";
        content.AppendLine($"BT {font} {size} Tf {x:0.##} {PageHeight - top:0.##} Td ({PdfText(value)}) Tj ET");
    }

    private static void WrappedText(StringBuilder content, double x, double top, int size, string value, double width, double lineHeight)
    {
        var currentTop = top;
        foreach (var paragraph in value.Replace("\r", "").Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                if (TextWidth(candidate, size, false) <= width) { line = candidate; continue; }
                if (line.Length > 0) Text(content, x, currentTop, size, line, false);
                currentTop += lineHeight;
                line = word;
                if (currentTop > 500) return;
            }
            if (line.Length > 0) Text(content, x, currentTop, size, line, false);
            currentTop += lineHeight;
            if (currentTop > 500) return;
        }
    }

    private static string Fit(string value, double width, int size)
    {
        if (TextWidth(value, size, false) <= width) return value;
        const string suffix = "...";
        while (value.Length > 1 && TextWidth(value + suffix, size, false) > width) value = value[..^1];
        return value.TrimEnd() + suffix;
    }

    private static double TextWidth(string value, int size, bool bold)
    {
        var units = value.Sum(character => character switch
        {
            ' ' => 0.25,
            'I' or 'i' or 'l' or 'j' or 't' => 0.28,
            'M' or 'W' or 'm' or 'w' => 0.82,
            >= 'A' and <= 'Z' => 0.64,
            _ => 0.48
        });
        return units * size * (bold ? 1.04 : 1);
    }

    private static void Line(StringBuilder content, double x1, double y1, double x2, double y2) =>
        content.AppendLine($"0.35 G 0.6 w {x1:0.##} {PageHeight - y1:0.##} m {x2:0.##} {PageHeight - y2:0.##} l S");

    private static void Rectangle(StringBuilder content, double x, double top, double width, double height) =>
        content.AppendLine($"0.55 G 0.6 w {x:0.##} {PageHeight - top - height:0.##} {width:0.##} {height:0.##} re S");

    private static byte[] BuildPdf(string certificate, string notations)
    {
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, Encoding.ASCII, 1024, leaveOpen: true);
        writer.NewLine = "\n";
        writer.WriteLine("%PDF-1.4");
        var offsets = new List<long> { 0 };
        AddObject(writer, offsets, "<< /Type /Catalog /Pages 2 0 R >>");
        AddObject(writer, offsets, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        var resources = "/Resources << /Font << /F1 7 0 R /F2 8 0 R >> >>";
        AddObject(writer, offsets, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] {resources} /Contents 5 0 R >>");
        AddObject(writer, offsets, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] {resources} /Contents 6 0 R >>");
        AddStream(writer, offsets, certificate);
        AddStream(writer, offsets, notations);
        AddObject(writer, offsets, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>");
        AddObject(writer, offsets, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>");
        writer.Flush();
        var xref = output.Position;
        writer.WriteLine("xref"); writer.WriteLine($"0 {offsets.Count}"); writer.WriteLine("0000000000 65535 f ");
        for (var i = 1; i < offsets.Count; i++) writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer"); writer.WriteLine($"<< /Size {offsets.Count} /Root 1 0 R >>"); writer.WriteLine("startxref"); writer.WriteLine(xref); writer.WriteLine("%%EOF"); writer.Flush();
        return output.ToArray();
    }

    private static void AddStream(StreamWriter writer, List<long> offsets, string stream)
    {
        var body = Encoding.ASCII.GetBytes(stream);
        writer.Flush();
        offsets.Add(writer.BaseStream.Position);
        writer.WriteLine($"{offsets.Count - 1} 0 obj");
        writer.WriteLine($"<< /Length {body.Length} >>");
        writer.WriteLine("stream");
        writer.Flush();
        writer.BaseStream.Write(body);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
    }

    private static void AddObject(StreamWriter writer, List<long> offsets, string value)
    {
        writer.Flush();
        offsets.Add(writer.BaseStream.Position);
        writer.WriteLine($"{offsets.Count - 1} 0 obj"); writer.WriteLine(value); writer.WriteLine("endobj");
    }

    private static string Join(params string?[] values) => string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    private static string FormatDate(DateOnly? value) => value?.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) ?? "—";
    private static string Month(DateOnly? value) => value?.ToString("MMMM", CultureInfo.InvariantCulture) ?? "-";
    private static string OrdinalDay(DateOnly? value) => value?.Day switch
    {
        1 => "First", 2 => "Second", 3 => "Third", 4 => "Fourth", 5 => "Fifth", 6 => "Sixth", 7 => "Seventh",
        8 => "Eighth", 9 => "Ninth", 10 => "Tenth", 11 => "Eleventh", 12 => "Twelfth", 13 => "Thirteenth",
        14 => "Fourteenth", 15 => "Fifteenth", 16 => "Sixteenth", 17 => "Seventeenth", 18 => "Eighteenth",
        19 => "Nineteenth", 20 => "Twentieth", 21 => "Twenty-First", 22 => "Twenty-Second", 23 => "Twenty-Third",
        24 => "Twenty-Fourth", 25 => "Twenty-Fifth", 26 => "Twenty-Sixth", 27 => "Twenty-Seventh",
        28 => "Twenty-Eighth", 29 => "Twenty-Ninth", 30 => "Thirtieth", 31 => "Thirty-First", _ => "-"
    };
    private static string PdfText(string value) => value.Replace("—", "-").Replace("–", "-").Replace("’", "'").Replace("“", "\"").Replace("”", "\"").Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", " ").Replace("\n", " ");
}
