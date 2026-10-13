using SacramentalRecords.Domain;

namespace SacramentalRecords.Application;

public interface IRegistryStore
{
    Task<IReadOnlyList<Person>> SearchPeopleAsync(string search, CancellationToken cancellationToken);
    Task<Person?> GetPersonAsync(Guid recordId, CancellationToken cancellationToken);
    Task<Person> SavePersonAsync(Person person, DateTimeOffset? expectedUpdatedUtc, CancellationToken cancellationToken);
    Task DeletePersonAsync(Guid recordId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SacramentRecord>> GetSacramentsAsync(Guid personRecordId, CancellationToken cancellationToken);
    Task<SacramentRecord> SaveSacramentAsync(SacramentRecord record, DateTimeOffset? expectedUpdatedUtc, CancellationToken cancellationToken);
    Task DeleteSacramentAsync(Guid recordId, CancellationToken cancellationToken);
}

public sealed class RegistryService(IRegistryStore store)
{
    public Task<IReadOnlyList<Person>> SearchPeopleAsync(string search, CancellationToken cancellationToken = default) =>
        store.SearchPeopleAsync(search.Trim(), cancellationToken);

    public Task<Person?> GetPersonAsync(Guid recordId, CancellationToken cancellationToken = default) =>
        store.GetPersonAsync(recordId, cancellationToken);

    public Task<IReadOnlyList<SacramentRecord>> GetSacramentsAsync(Guid personRecordId, CancellationToken cancellationToken = default) =>
        store.GetSacramentsAsync(personRecordId, cancellationToken);

    public async Task<Person> SavePersonAsync(Person person, DateTimeOffset? expectedUpdatedUtc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(person.GivenName) || string.IsNullOrWhiteSpace(person.FamilyName))
            throw new ValidationException("Given name and family name are required.");
        return await store.SavePersonAsync(person, expectedUpdatedUtc, cancellationToken);
    }

    public Task DeletePersonAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        if (recordId == Guid.Empty) throw new ValidationException("A person record ID is required.");
        return store.DeletePersonAsync(recordId, cancellationToken);
    }

    public async Task<SacramentRecord> SaveSacramentAsync(SacramentRecord record, DateTimeOffset? expectedUpdatedUtc, CancellationToken cancellationToken = default)
    {
        if (record.PersonRecordId == Guid.Empty || string.IsNullOrWhiteSpace(record.SacramentType))
            throw new ValidationException("A person and sacrament type are required.");
        return await store.SaveSacramentAsync(record, expectedUpdatedUtc, cancellationToken);
    }

    public Task DeleteSacramentAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        if (recordId == Guid.Empty) throw new ValidationException("A sacrament record ID is required.");
        return store.DeleteSacramentAsync(recordId, cancellationToken);
    }
}

public sealed class ValidationException(string message) : Exception(message);
public sealed class RecordConflictException(string message) : Exception(message);
