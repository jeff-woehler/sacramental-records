using SacramentalRecords.Domain;
using SacramentalRecords.Application;

namespace SacramentalRecords.App;

public class MergeResult
{
    public Person Person { get; set; }
    public Guid OtherRecordId { get; set; }
    public bool HasConflicts { get; set; }
    public Dictionary<string, (object? Left, object? Right)> Conflicts { get; set; }
    public List<SacramentRecord> SacramentsToMerge { get; set; }

    public MergeResult(Person person, Guid otherRecordId)
    {
        Person = person;
        OtherRecordId = otherRecordId;
        Conflicts = new Dictionary<string, (object?, object?)>();
        SacramentsToMerge = new List<SacramentRecord>();
        HasConflicts = false;
    }
}

public static class MergeService
{
    public static MergeResult DetectConflicts(Person person1, Person person2)
    {
        var conflicts = new Dictionary<string, (object?, object?)>();

        CheckConflict(nameof(Person.GivenName), person1.GivenName, person2.GivenName, conflicts);
        CheckConflict(nameof(Person.MiddleName), person1.MiddleName, person2.MiddleName, conflicts);
        CheckConflict(nameof(Person.FamilyName), person1.FamilyName, person2.FamilyName, conflicts);
        CheckConflict(nameof(Person.BirthDate), person1.BirthDate, person2.BirthDate, conflicts);
        CheckConflict(nameof(Person.BirthCity), person1.BirthCity, person2.BirthCity, conflicts);
        CheckConflict(nameof(Person.BaptismDate), person1.BaptismDate, person2.BaptismDate, conflicts);
        CheckConflict(nameof(Person.BaptismChurch), person1.BaptismChurch, person2.BaptismChurch, conflicts);
        CheckConflict(nameof(Person.Status), person1.Status, person2.Status, conflicts);
        CheckConflict(nameof(Person.MaidenName), person1.MaidenName, person2.MaidenName, conflicts);
        CheckConflict(nameof(Person.DeceasedDate), person1.DeceasedDate, person2.DeceasedDate, conflicts);
        CheckConflict(nameof(Person.ChurchDbId), person1.ChurchDbId, person2.ChurchDbId, conflicts);
        CheckConflict(nameof(Person.PdsMemberId), person1.PdsMemberId, person2.PdsMemberId, conflicts);
        CheckConflict(nameof(Person.FlocknoteId), person1.FlocknoteId, person2.FlocknoteId, conflicts);

        var mergedPerson = CreateMergedPerson(person1, person2, conflicts);

        return new MergeResult(mergedPerson, person2.RecordId)
        {
            HasConflicts = conflicts.Count > 0,
            Conflicts = conflicts
        };
    }

    private static void CheckConflict<T>(string fieldName, T? left, T? right, Dictionary<string, (object?, object?)> conflicts)
    {
        if (!Equals(left, right))
        {
            conflicts[fieldName] = (left, right);
        }
    }

    private static Person CreateMergedPerson(Person person1, Person person2, Dictionary<string, (object?, object?)> conflicts)
    {
        var merged = new Person(
            person1.RecordId,
            GetConflictResolvedValue(nameof(Person.GivenName), person1.GivenName, person2.GivenName, conflicts) as string ?? person1.GivenName,
            GetConflictResolvedValue(nameof(Person.MiddleName), person1.MiddleName, person2.MiddleName, conflicts) as string,
            GetConflictResolvedValue(nameof(Person.FamilyName), person1.FamilyName, person2.FamilyName, conflicts) as string ?? person1.FamilyName,
            GetConflictResolvedValue(nameof(Person.BirthDate), person1.BirthDate, person2.BirthDate, conflicts) as DateOnly?,
            GetConflictResolvedValue(nameof(Person.ChurchDbId), person1.ChurchDbId, person2.ChurchDbId, conflicts) as string,
            GetConflictResolvedValue(nameof(Person.BirthCity), person1.BirthCity, person2.BirthCity, conflicts) as string,
            GetConflictResolvedValue(nameof(Person.BaptismDate), person1.BaptismDate, person2.BaptismDate, conflicts) as DateOnly?,
            GetConflictResolvedValue(nameof(Person.BaptismChurch), person1.BaptismChurch, person2.BaptismChurch, conflicts) as string,
            GetConflictResolvedValue(nameof(Person.Status), person1.Status, person2.Status, conflicts) as string ?? person1.Status,
            GetConflictResolvedValue(nameof(Person.PdsMemberId), person1.PdsMemberId, person2.PdsMemberId, conflicts) as string,
            GetConflictResolvedValue(nameof(Person.FlocknoteId), person1.FlocknoteId, person2.FlocknoteId, conflicts) as string,
            DateTimeOffset.UtcNow,
            GetConflictResolvedValue(nameof(Person.MaidenName), person1.MaidenName, person2.MaidenName, conflicts) as string,
            GetConflictResolvedValue(nameof(Person.DeceasedDate), person1.DeceasedDate, person2.DeceasedDate, conflicts) as DateOnly?
        );

        return merged;
    }

    private static object? GetConflictResolvedValue(string fieldName, object? left, object? right, Dictionary<string, (object?, object?)> conflicts)
    {
        if (!conflicts.ContainsKey(fieldName))
        {
            return left ?? right;
        }

        // If still in conflicts dict, favor non-null and more recent
        return left ?? right;
    }

    public static Person ResolveMerge(Person person1, Person person2, Dictionary<string, string> choices)
    {
        var values = new Dictionary<string, object?>();

        foreach (var fieldName in choices.Keys)
        {
            var choice = choices[fieldName];
            values[fieldName] = GetValueFromPerson(fieldName, choice == "left" ? person1 : person2);
        }

        var merged = new Person(
            person1.RecordId,
            values.GetValueOrDefault(nameof(Person.GivenName)) as string ?? person1.GivenName,
            values.GetValueOrDefault(nameof(Person.MiddleName)) as string,
            values.GetValueOrDefault(nameof(Person.FamilyName)) as string ?? person1.FamilyName,
            values.GetValueOrDefault(nameof(Person.BirthDate)) as DateOnly?,
            values.GetValueOrDefault(nameof(Person.ChurchDbId)) as string,
            values.GetValueOrDefault(nameof(Person.BirthCity)) as string,
            values.GetValueOrDefault(nameof(Person.BaptismDate)) as DateOnly?,
            values.GetValueOrDefault(nameof(Person.BaptismChurch)) as string,
            values.GetValueOrDefault(nameof(Person.Status)) as string ?? person1.Status,
            values.GetValueOrDefault(nameof(Person.PdsMemberId)) as string,
            values.GetValueOrDefault(nameof(Person.FlocknoteId)) as string,
            DateTimeOffset.UtcNow,
            values.GetValueOrDefault(nameof(Person.MaidenName)) as string,
            values.GetValueOrDefault(nameof(Person.DeceasedDate)) as DateOnly?
        );

        return merged;
    }

    private static object? GetValueFromPerson(string fieldName, Person person)
    {
        return fieldName switch
        {
            nameof(Person.GivenName) => person.GivenName,
            nameof(Person.MiddleName) => person.MiddleName,
            nameof(Person.FamilyName) => person.FamilyName,
            nameof(Person.BirthDate) => person.BirthDate,
            nameof(Person.BirthCity) => person.BirthCity,
            nameof(Person.BaptismDate) => person.BaptismDate,
            nameof(Person.BaptismChurch) => person.BaptismChurch,
            nameof(Person.Status) => person.Status,
            nameof(Person.MaidenName) => person.MaidenName,
            nameof(Person.DeceasedDate) => person.DeceasedDate,
            nameof(Person.ChurchDbId) => person.ChurchDbId,
            nameof(Person.PdsMemberId) => person.PdsMemberId,
            nameof(Person.FlocknoteId) => person.FlocknoteId,
            _ => null
        };
    }
}
