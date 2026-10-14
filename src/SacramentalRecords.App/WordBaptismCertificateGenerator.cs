using System.IO;
using System.Runtime.InteropServices;
using SacramentalRecords.Domain;

namespace SacramentalRecords.App;

/// <summary>
/// Creates a certificate from the Word template. Microsoft Word is used for field
/// evaluation and PDF rendering so the template remains the source of the layout.
/// </summary>
internal static class WordBaptismCertificateGenerator
{
        private const string TemplateFileName = "assets\\template.docx";

    internal static string BundledTemplatePath => Path.Combine(AppContext.BaseDirectory, TemplateFileName);

    public static void Write(
        string pdfFileName,
        Person person,
        SacramentRecord baptism,
        IReadOnlyList<SacramentRecord> sacraments)
    {
                var configuredTemplate = MainWindow.FileSettings.GetValueOrDefault(MainWindow.BaptismTemplateSettingKey);
        var templateFileName = string.IsNullOrWhiteSpace(configuredTemplate)
            ? BundledTemplatePath
            : configuredTemplate;
        if (!File.Exists(templateFileName))
        {
            var message = string.IsNullOrWhiteSpace(configuredTemplate)
                ? "The bundled baptism certificate Word template was not found."
                : $"The selected baptism certificate template could not be found:\n{templateFileName}\n\nChoose a new template from the main window.";
            throw new FileNotFoundException(message, templateFileName);
        }

        var wordFileName = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.docx");
        File.Copy(templateFileName, wordFileName, overwrite: true);

        object? word = null;
        object? document = null;
        try
        {
            var wordType = Type.GetTypeFromProgID("Word.Application")
                ?? throw new InvalidOperationException("Microsoft Word is required to create a baptism certificate.");
            word = Activator.CreateInstance(wordType)
                ?? throw new InvalidOperationException("Microsoft Word could not be started.");

            dynamic application = word;
            application.Visible = false;
            application.ScreenUpdating = false;
            application.DisplayAlerts = 0;
            PreferLocalPdfPrinter(application);
            document = application.Documents.Open(wordFileName, ReadOnly: false, AddToRecentFiles: false);
            dynamic openDocument = document;

            var missingDateProperties = SetProperties(openDocument, person, baptism, sacraments);
            UpdateFields(openDocument);
            ClearMissingDateFields(openDocument, missingDateProperties);
            openDocument.Save();

            // ExportAsFixedFormat uses Word's print/PDF engine and preserves the
            // page setup, fonts, rules, and other layout details in the template.
            openDocument.ExportAsFixedFormat(pdfFileName, 17 /* wdExportFormatPDF */);
        }
        finally
        {
            CloseComObject(document, saveChanges: false);
            if (word is not null)
            {
                try { ((dynamic)word).Quit(false); }
                catch (COMException) { }
                finally { if (Marshal.IsComObject(word)) Marshal.ReleaseComObject(word); }
            }
            try { File.Delete(wordFileName); }
            catch (IOException) { }
        }
    }

    private static void PreferLocalPdfPrinter(dynamic application)
    {
        try
        {
            application.WordBasic.FilePrintSetup(Printer: "Microsoft Print to PDF", DoNotSetAsSysDefault: 1);
        }
        catch (COMException)
        {
            // Continue with Word's active printer when the local PDF driver is unavailable.
        }
        catch (ArgumentException)
        {
            // Continue with Word's active printer when the local PDF driver is unavailable.
        }
    }

    private static HashSet<string> SetProperties(
        dynamic document,
        Person person,
        SacramentRecord baptism,
        IReadOnlyList<SacramentRecord> sacraments)
    {
        dynamic properties = document.CustomDocumentProperties;
        var fullName = Join(person.GivenName, person.MiddleName, person.FamilyName);
        var missingDateProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        SetTextProperty(properties, "Cert.FullName", fullName);
        SetTextProperty(properties, "Cert.Father", Value(baptism.FatherName));
        SetTextProperty(properties, "Cert.Mother", Value(baptism.MotherName));
        SetTextProperty(properties, "Cert.BirthCity", Value(person.BirthCity));
        SetTextProperty(properties, "Cert.BirthDate-DayOrdinal", OrdinalDay(person.BirthDate));
        SetTextProperty(properties, "Cert.BaptismDate-DayOrdinal", OrdinalDay(baptism.SacramentDate));
        SetTextProperty(properties, "Cert.BaptismPresider", Value(baptism.Minister));
        SetTextProperty(properties, "Cert.BaptismSponsor1", Value(baptism.SponsorOne));
        SetTextProperty(properties, "Cert.BaptismSponsor2", Value(baptism.SponsorTwo));
        SetTextProperty(properties, "Cert.BaptismSponsor2Witness", baptism.SponsorTwoIsChristianWitness ? "(Christian witness)" : string.Empty);
        SetTextProperty(properties, "Cert.BaptismProxy1", Value(baptism.ProxyOne));
        SetTextProperty(properties, "Cert.BaptismProxy2", Value(baptism.ProxyTwo));
        SetTextProperty(properties, "Cert.BaptismSponsorsSentence", SponsorsSentence(baptism));
        SetTextProperty(properties, "Cert.BaptismProxiesSentence", ProxiesSentence(baptism));
        SetTextProperty(properties, "Cert.BaptismVolume", Value(baptism.Volume));
        SetTextProperty(properties, "Cert.BaptismPage", Value(baptism.Page));
        SetDateProperty(properties, "Cert.BirthDate", person.BirthDate);
        SetDateProperty(properties, "Cert.BaptismDate", baptism.SacramentDate);
        var confirmation = FindSacrament(sacraments, "confirmation");
        var marriage = FindSacrament(sacraments, "marriage");
        SetSacramentProperties(properties, "FirstCommunion", FindSacrament(sacraments, "first_communion"), missingDateProperties);
        SetSacramentProperties(properties, "Confirmation", confirmation, missingDateProperties);
        SetTextProperty(properties, "Cert.ConfirmationName", confirmation?.Name?.Trim() ?? string.Empty);
        SetSacramentProperties(properties, "Marriage", marriage, missingDateProperties);
        SetTextProperty(properties, "Cert.MarriageSpouseName", marriage?.Name?.Trim() ?? string.Empty);
        SetSacramentProperties(properties, "HolyOrders", FindSacrament(sacraments, "holy_orders"), missingDateProperties);
        return missingDateProperties;
    }

    private static SacramentRecord? FindSacrament(IEnumerable<SacramentRecord> sacraments, string type) =>
        sacraments.FirstOrDefault(record =>
            string.Equals(EntryTypeCatalog.Canonicalize(record.SacramentType), type, StringComparison.OrdinalIgnoreCase));

    private static void SetSacramentProperties(
        dynamic properties,
        string propertyPrefix,
        SacramentRecord? sacrament,
        ISet<string> missingDateProperties)
    {
        var datePropertyName = $"Cert.{propertyPrefix}Date";
        if (sacrament?.SacramentDate is DateOnly date)
            SetDateProperty(properties, datePropertyName, date);
        else
            missingDateProperties.Add(datePropertyName);
        SetTextProperty(properties, $"Cert.{propertyPrefix}PerformedAt", sacrament?.Place?.Trim() ?? string.Empty);
    }

    private static void SetTextProperty(dynamic properties, string name, string value)
    {
        var wordValue = string.IsNullOrEmpty(value) ? " " : value;
        var property = FindProperty(properties, name);
        if (property is null)
        {
            properties.Add(name, false, 4 /* msoPropertyTypeString */, wordValue);
            return;
        }

        ((dynamic)property).Value = wordValue;
    }

    private static object? FindProperty(dynamic properties, string name)
    {
        try
        {
            return properties[name];
        }
        catch (COMException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void SetDateProperty(dynamic properties, string name, DateOnly? value)
    {
        var date = value?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        var property = FindProperty(properties, name);
        if (property is null)
        {
            properties.Add(name, false, 5 /* msoPropertyTypeDate */, date);
            return;
        }

        ((dynamic)property).Value = date;
    }

    private static void UpdateFields(dynamic document)
    {
        foreach (dynamic storyRange in document.StoryRanges)
        {
            dynamic range = storyRange;
            do
            {
                range.Fields.Update();
                range = range.NextStoryRange;
            }
            while (range is not null);
        }
    }

    private static void ClearMissingDateFields(dynamic document, IReadOnlySet<string> missingDateProperties)
    {
        if (missingDateProperties.Count == 0) return;

        foreach (dynamic storyRange in document.StoryRanges)
        {
            dynamic range = storyRange;
            do
            {
                foreach (dynamic field in range.Fields)
                {
                    var code = (string)field.Code.Text;
                    if (missingDateProperties.Any(name => code.Contains(name, StringComparison.OrdinalIgnoreCase)))
                        field.Result.Text = string.Empty;
                }
                range = range.NextStoryRange;
            }
            while (range is not null);
        }
    }

    private static void CloseComObject(object? value, bool saveChanges)
    {
        if (value is null) return;
        try
        {
            dynamic comObject = value;
            comObject.Close(saveChanges);
        }
        catch (COMException)
        {
            // Word may already have closed the document during an automation error.
        }
        finally
        {
            if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }

    private static string Join(params string?[] values) => string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    // Word DOCPROPERTY fields cannot hold an empty string, so blank sentences use a single space.
    private static string SponsorsSentence(SacramentRecord r)
    {
        var one = r.SponsorOne?.Trim();
        var two = r.SponsorTwo?.Trim();
        var hasOne = !string.IsNullOrEmpty(one);
        var hasTwo = !string.IsNullOrEmpty(two);
        return (hasOne, hasTwo, r.SponsorTwoIsChristianWitness) switch
        {
            (true, true, false) => $"The Sponsors being {one} and {two}",
            (true, true, true) => $"The Sponsor being {one}, with {two} as Christian Witness",
            (true, false, _) => $"The Sponsor being {one}",
            (false, true, false) => $"The Sponsor being {two}",
            (false, true, true) => $"With {two} as Christian Witness",
            _ => " "
        };
    }

    private static string ProxiesSentence(SacramentRecord r)
    {
        var parts = new[] { (Proxy: r.ProxyOne, For: r.SponsorOne), (Proxy: r.ProxyTwo, For: r.SponsorTwo) }
            .Where(p => !string.IsNullOrWhiteSpace(p.Proxy))
            .Select(p => string.IsNullOrWhiteSpace(p.For) ? p.Proxy!.Trim() : $"{p.Proxy!.Trim()} for {p.For.Trim()}")
            .ToList();
        return parts.Count == 0 ? " " : $"Kindly represented by proxy: {string.Join(" and ", parts)}";
    }

    private static string OrdinalDay(DateOnly? value) => value?.Day switch
    {
        1 => "First", 2 => "Second", 3 => "Third", 4 => "Fourth", 5 => "Fifth", 6 => "Sixth", 7 => "Seventh",
        8 => "Eighth", 9 => "Ninth", 10 => "Tenth", 11 => "Eleventh", 12 => "Twelfth", 13 => "Thirteenth",
        14 => "Fourteenth", 15 => "Fifteenth", 16 => "Sixteenth", 17 => "Seventeenth", 18 => "Eighteenth",
        19 => "Nineteenth", 20 => "Twentieth", 21 => "Twenty-First", 22 => "Twenty-Second", 23 => "Twenty-Third",
        24 => "Twenty-Fourth", 25 => "Twenty-Fifth", 26 => "Twenty-Sixth", 27 => "Twenty-Seventh",
        28 => "Twenty-Eighth", 29 => "Twenty-Ninth", 30 => "Thirtieth", 31 => "Thirty-First", _ => "-"
    };
}
