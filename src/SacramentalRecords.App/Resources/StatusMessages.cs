using System.Globalization;
using System.Resources;

namespace SacramentalRecords.App.Resources;

/// <summary>Status bar text, maintained in StatusMessages.resx.</summary>
internal static class StatusMessages
{
    private static readonly ResourceManager Manager = new("SacramentalRecords.App.Resources.StatusMessages", typeof(StatusMessages).Assembly);

    public static string OpenPackageToBegin => Get(nameof(OpenPackageToBegin));
    public static string OpenPackageFirst => Get(nameof(OpenPackageFirst));
    public static string SelectPersonFirst => Get(nameof(SelectPersonFirst));
    public static string PackageNotFound => Get(nameof(PackageNotFound));
    public static string Publishing => Get(nameof(Publishing));
    public static string PublishingBeforeClose => Get(nameof(PublishingBeforeClose));
    public static string ClosingSession => Get(nameof(ClosingSession));
    public static string PeopleRefreshed => Get(nameof(PeopleRefreshed));
    public static string PeopleRefreshedUnpublished => Get(nameof(PeopleRefreshedUnpublished));

    public static string RecordsFound(int count) => Format(nameof(RecordsFound), count);
    public static string UsingPackage(string fileName) => Format(nameof(UsingPackage), fileName);
    public static string Published(DateTime time) => Format(nameof(Published), time);
    public static string PublishFailed(string error) => Format(nameof(PublishFailed), error);
    public static string PersonRecordSummary(Guid recordId, int count) => Format(nameof(PersonRecordSummary), recordId, count);
    public static string CertificateSaved(string fileName) => Format(nameof(CertificateSaved), fileName);

    private static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(name), args);
}
