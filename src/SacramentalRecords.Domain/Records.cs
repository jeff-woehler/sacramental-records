namespace SacramentalRecords.Domain;

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
