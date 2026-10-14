using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using SacramentalRecords.Application;
using SacramentalRecords.Domain;

namespace SacramentalRecords.Infrastructure;

/// <summary>
/// Keeps the working SQLite file outside OneDrive. Only the completed ZIP package is synchronized.
/// The lock is held for the lifetime of this store, so two application sessions cannot edit the package.
/// </summary>
public sealed class PackageLockedException : IOException
{
    public string LockPath { get; }

    public PackageLockedException(string lockPath, IOException innerException)
        : base($"The package is already open. Lock file: {lockPath}", innerException) => LockPath = lockPath;
}

public sealed class SqlitePackageStore : IRegistryStore, IAsyncDisposable
{
    private const string DatabaseEntryName = "MemberRecords.sqlite";
    private readonly string _packagePath;
    private readonly string _workingDirectory;
    private readonly string _databasePath;
        private readonly FileStream? _lock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

        public bool HasUnpublishedChanges { get; private set; }
    public bool IsReadOnly { get; }

        public SqlitePackageStore(string packagePath, bool readOnly = false)
    {
        IsReadOnly = readOnly;
        _packagePath = Path.GetFullPath(packagePath);
        var directory = Path.GetDirectoryName(_packagePath) ?? throw new ArgumentException("The package path has no directory.", nameof(packagePath));
        Directory.CreateDirectory(directory);
        if (!File.Exists(_packagePath)) throw new FileNotFoundException("The OneDrive database package was not found.", _packagePath);

                        try
                        {
                            if (!IsReadOnly)
                            {
                                try
                                {
                                    _lock = new FileStream(_packagePath + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None);
                                }
                                catch (IOException ex)
                                {
                                    throw new PackageLockedException(_packagePath + ".lock", ex);
                                }
                            }

                            _workingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SacramentalRecords", "sessions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workingDirectory);
            _databasePath = Path.Combine(_workingDirectory, DatabaseEntryName);
            ExtractPackage();
            InitializeDatabase();
        }
        catch
        {
            try { _lock?.Dispose(); } catch { }
            // Do not remove a lock that this instance failed to acquire.
            try { if (_lock is not null) File.Delete(_packagePath + ".lock"); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<Person>> SearchPeopleAsync(string search, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM People WHERE @search = '' OR (GivenName || ' ' || COALESCE(MiddleName,'') || ' ' || FamilyName || ' ' || COALESCE(PdsMemberId,'') || ' ' || COALESCE(FlocknoteId,'')) LIKE @pattern COLLATE NOCASE ORDER BY FamilyName, GivenName";
            command.Parameters.AddWithValue("@search", search.Trim());
            command.Parameters.AddWithValue("@pattern", $"%{search.Trim()}%");
            var result = new List<Person>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadPerson(reader));
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<Person?> GetPersonAsync(Guid recordId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM People WHERE RecordId=@id COLLATE NOCASE"; command.Parameters.AddWithValue("@id", recordId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadPerson(reader) : null;
        }
        finally { _gate.Release(); }
    }

    public Task<Person> SavePersonAsync(Person person, DateTimeOffset? expected, CancellationToken cancellationToken) => SavePersonCoreAsync(person, expected, cancellationToken);
    public Task<SacramentRecord> SaveSacramentAsync(SacramentRecord record, DateTimeOffset? expected, CancellationToken cancellationToken) => SaveSacramentCoreAsync(record, expected, cancellationToken);

    private async Task<Person> SavePersonCoreAsync(Person person, DateTimeOffset? expected, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open(); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var existing = await ReadVersion(connection, transaction, "People", person.RecordId, cancellationToken);
            CheckVersion(existing, expected);
            var churchId = person.ChurchDbId ?? await NextChurchId(connection, transaction, cancellationToken);
            var saved = person with { ChurchDbId = churchId, UpdatedUtc = DateTimeOffset.UtcNow };
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO People VALUES(@id,@given,@middle,@family,@birth,@church,@city,@baptism,@baptismChurch,@status,@pds,@flock,@updated) ON CONFLICT(RecordId) DO UPDATE SET GivenName=@given,MiddleName=@middle,FamilyName=@family,BirthDate=@birth,ChurchDbId=@church,BirthCity=@city,BaptismDate=@baptism,BaptismChurch=@baptismChurch,Status=@status,PdsMemberId=@pds,FlocknoteId=@flock,UpdatedUtc=@updated";
            AddPersonParameters(command, saved); await command.ExecuteNonQueryAsync(cancellationToken); await AddAudit(connection, transaction, saved.RecordId, "save", "Saved person", cancellationToken); await transaction.CommitAsync(cancellationToken); HasUnpublishedChanges = true; return saved;
        }
        finally { _gate.Release(); }
    }

    private async Task<SacramentRecord> SaveSacramentCoreAsync(SacramentRecord record, DateTimeOffset? expected, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open(); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            CheckVersion(await ReadVersion(connection, transaction, "Sacraments", record.RecordId, cancellationToken), expected);
            var saved = record with { UpdatedUtc = DateTimeOffset.UtcNow };
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO Sacraments (RecordId,PersonRecordId,SacramentType,SacramentDate,Name,Place,Minister,FatherName,MotherName,SponsorOne,SponsorTwo,SponsorTwoIsChristianWitness,ProxyOne,ProxyTwo,Volume,Page,Item,Details,CanonicalNotations,UpdatedUtc) VALUES(@id,@person,@type,@date,@name,@place,@minister,@father,@mother,@sponsor1,@sponsor2,@witness2,@proxy1,@proxy2,@volume,@page,@item,@details,@canonical,@updated) ON CONFLICT(RecordId) DO UPDATE SET PersonRecordId=@person,SacramentType=@type,SacramentDate=@date,Name=@name,Place=@place,Minister=@minister,FatherName=@father,MotherName=@mother,SponsorOne=@sponsor1,SponsorTwo=@sponsor2,SponsorTwoIsChristianWitness=@witness2,ProxyOne=@proxy1,ProxyTwo=@proxy2,Volume=@volume,Page=@page,Item=@item,Details=@details,CanonicalNotations=@canonical,UpdatedUtc=@updated";
            AddSacramentParameters(command, saved); await command.ExecuteNonQueryAsync(cancellationToken); await AddAudit(connection, transaction, saved.RecordId, "save", "Saved entry", cancellationToken); await transaction.CommitAsync(cancellationToken); HasUnpublishedChanges = true; return saved;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SacramentRecord>> GetSacramentsAsync(Guid personRecordId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open(); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM Sacraments WHERE PersonRecordId=@id COLLATE NOCASE ORDER BY SacramentDate"; command.Parameters.AddWithValue("@id", personRecordId.ToString());
            var result = new List<SacramentRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSacrament(reader)); return result;
        }
        finally { _gate.Release(); }
    }

    public Task DeletePersonAsync(Guid recordId, CancellationToken cancellationToken) => DeleteAsync("People", recordId, "Deleted person and associated entries", cancellationToken);
    public Task DeleteSacramentAsync(Guid recordId, CancellationToken cancellationToken) => DeleteAsync("Sacraments", recordId, "Deleted entry", cancellationToken);

    private async Task DeleteAsync(string table, Guid id, string summary, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await using var connection = Open(); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken); await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"DELETE FROM {table} WHERE RecordId=@id COLLATE NOCASE"; command.Parameters.AddWithValue("@id", id.ToString()); await command.ExecuteNonQueryAsync(cancellationToken); await AddAudit(connection, transaction, id, "delete", summary, cancellationToken); await transaction.CommitAsync(cancellationToken); HasUnpublishedChanges = true; }
        finally { _gate.Release(); }
    }

        /// <summary>
    /// Removes a lock only when no process currently has it open. This makes it safe
    /// for a user to recover from a crash without deleting an active session's lock.
    /// </summary>
    public static bool TryRemoveStaleLock(string packagePath)
    {
        var lockPath = Path.GetFullPath(packagePath) + ".lock";
        if (!File.Exists(lockPath)) return true;

        try
        {
            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(lockPath);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task PublishPackageAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CommitPackageAsync();
            HasUnpublishedChanges = false;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        await _gate.WaitAsync();
        try { }
        finally
        {
            _gate.Release();
            _lock?.Dispose();
            if (_lock is not null) try { File.Delete(_packagePath + ".lock"); } catch { }
            try { Directory.Delete(_workingDirectory, true); } catch { }
            _gate.Dispose();
        }
    }

    private void ExtractPackage()
    {
        using var archive = ZipFile.OpenRead(_packagePath);
        var entry = archive.GetEntry(DatabaseEntryName) ?? throw new InvalidDataException($"The package must contain {DatabaseEntryName}.");
        entry.ExtractToFile(_databasePath);
    }

    private void InitializeDatabase()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS People (RecordId TEXT PRIMARY KEY, GivenName TEXT NOT NULL, MiddleName TEXT, FamilyName TEXT NOT NULL, BirthDate TEXT, ChurchDbId TEXT, BirthCity TEXT, BaptismDate TEXT, BaptismChurch TEXT, Status TEXT NOT NULL, PdsMemberId TEXT, FlocknoteId TEXT, UpdatedUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Sacraments (RecordId TEXT PRIMARY KEY, PersonRecordId TEXT NOT NULL REFERENCES People(RecordId) ON DELETE CASCADE, SacramentType TEXT NOT NULL, SacramentDate TEXT, Name TEXT, Place TEXT, Minister TEXT, FatherName TEXT, MotherName TEXT, SponsorOne TEXT, SponsorTwo TEXT, Volume TEXT, Page TEXT, Item TEXT, Details TEXT, CanonicalNotations TEXT, UpdatedUtc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS AuditEvents (EventId TEXT PRIMARY KEY, RecordId TEXT NOT NULL, User TEXT NOT NULL, Operation TEXT NOT NULL, OccurredUtc TEXT NOT NULL, Summary TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS IX_People_Name ON People(FamilyName, GivenName); CREATE INDEX IF NOT EXISTS IX_Sacraments_Person ON Sacraments(PersonRecordId);";
        command.ExecuteNonQuery();
        AddColumnIfMissing(connection, "Sacraments", "SponsorTwoIsChristianWitness", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "Sacraments", "ProxyOne", "TEXT");
        AddColumnIfMissing(connection, "Sacraments", "ProxyTwo", "TEXT");
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=@column";
        check.Parameters.AddWithValue("@column", column);
        if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        // Do not pool connections for the working database. Microsoft.Data.Sqlite can
        // retain pooled native connections after an individual connection is disposed,
        // which leaves the SQLite file open while PublishPackageAsync tries to archive it.
        var connection = new SqliteConnection($"Data Source={_databasePath};Cache=Private;Pooling=False");
        connection.Open();
        return connection;
    }
    private static string? S(SqliteDataReader r, string name) => r[name] is DBNull ? null : r[name]?.ToString();
    private static DateOnly? D(SqliteDataReader r, string name) => DateOnly.TryParse(S(r, name), out var d) ? d : null;
    private static DateTimeOffset T(SqliteDataReader r, string name) => DateTimeOffset.TryParse(S(r, name), out var d) ? d : DateTimeOffset.MinValue;
    private static Person ReadPerson(SqliteDataReader r) => new(Guid.Parse(S(r,"RecordId")!), S(r,"GivenName")!, S(r,"MiddleName"), S(r,"FamilyName")!, D(r,"BirthDate"), S(r,"ChurchDbId"), S(r,"BirthCity"), D(r,"BaptismDate"), S(r,"BaptismChurch"), S(r,"Status")!, S(r,"PdsMemberId"), S(r,"FlocknoteId"), T(r,"UpdatedUtc"));
    private static SacramentRecord ReadSacrament(SqliteDataReader r) => new(Guid.Parse(S(r,"RecordId")!), Guid.Parse(S(r,"PersonRecordId")!), S(r,"SacramentType")!, D(r,"SacramentDate"), S(r,"Name"), S(r,"Place"), S(r,"Minister"), S(r,"FatherName"), S(r,"MotherName"), S(r,"SponsorOne"), S(r,"SponsorTwo"), S(r,"SponsorTwoIsChristianWitness") == "1", S(r,"ProxyOne"), S(r,"ProxyTwo"), S(r,"Volume"), S(r,"Page"), S(r,"Item"), S(r,"Details"), S(r,"CanonicalNotations"), T(r,"UpdatedUtc"));
    private static string? Iso(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? Iso(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture);

    private static void AddPersonParameters(SqliteCommand c, Person p) { Add(c,"@id",p.RecordId); Add(c,"@given",p.GivenName); Add(c,"@middle",p.MiddleName); Add(c,"@family",p.FamilyName); Add(c,"@birth",Iso(p.BirthDate)); Add(c,"@church",p.ChurchDbId); Add(c,"@city",p.BirthCity); Add(c,"@baptism",Iso(p.BaptismDate)); Add(c,"@baptismChurch",p.BaptismChurch); Add(c,"@status",p.Status); Add(c,"@pds",p.PdsMemberId); Add(c,"@flock",p.FlocknoteId); Add(c,"@updated",Iso(p.UpdatedUtc)); }
    private static void AddSacramentParameters(SqliteCommand c, SacramentRecord r) { Add(c,"@id",r.RecordId); Add(c,"@person",r.PersonRecordId); Add(c,"@type",r.SacramentType); Add(c,"@date",Iso(r.SacramentDate)); Add(c,"@name",r.Name); Add(c,"@place",r.Place); Add(c,"@minister",r.Minister); Add(c,"@father",r.FatherName); Add(c,"@mother",r.MotherName); Add(c,"@sponsor1",r.SponsorOne); Add(c,"@sponsor2",r.SponsorTwo); Add(c,"@witness2",r.SponsorTwoIsChristianWitness ? 1 : 0); Add(c,"@proxy1",r.ProxyOne); Add(c,"@proxy2",r.ProxyTwo); Add(c,"@volume",r.Volume); Add(c,"@page",r.Page); Add(c,"@item",r.Item); Add(c,"@details",r.Details); Add(c,"@canonical",r.CanonicalNotations); Add(c,"@updated",Iso(r.UpdatedUtc)); }
    private static void Add(SqliteCommand c, string name, object? value) => c.Parameters.AddWithValue(name, value switch { Guid id => id.ToString(), _ => value ?? DBNull.Value });
    private static async Task<string?> ReadVersion(SqliteConnection c, SqliteTransaction t, string table, Guid id, CancellationToken token) { await using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=$"SELECT UpdatedUtc FROM {table} WHERE RecordId=@id COLLATE NOCASE"; cmd.Parameters.AddWithValue("@id",id.ToString()); return (await cmd.ExecuteScalarAsync(token))?.ToString(); }
    private static void CheckVersion(string? actual, DateTimeOffset? expected) { if (expected is not null && actual is not null && DateTimeOffset.Parse(actual) != expected) throw new RecordConflictException("The record changed in another session. Refresh before saving."); }
    private static async Task<string> NextChurchId(SqliteConnection c, SqliteTransaction t, CancellationToken token) { await using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="SELECT COALESCE(MAX(CAST(ChurchDbId AS INTEGER)),0)+1 FROM People"; return (Convert.ToInt64(await cmd.ExecuteScalarAsync(token), CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture); }
    private static async Task AddAudit(SqliteConnection c, SqliteTransaction t, Guid id, string operation, string summary, CancellationToken token) { await using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="INSERT INTO AuditEvents VALUES(@event,@record,@user,@operation,@occurred,@summary)"; Add(cmd,"@event",Guid.NewGuid()); Add(cmd,"@record",id); Add(cmd,"@user",Environment.UserName); Add(cmd,"@operation",operation); Add(cmd,"@occurred",Iso(DateTimeOffset.UtcNow)); Add(cmd,"@summary",summary); await cmd.ExecuteNonQueryAsync(token); }

    private Task CommitPackageAsync()
    {
        var directory = Path.GetDirectoryName(_packagePath)!; var temp = Path.Combine(directory, $".{Path.GetFileName(_packagePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create)) archive.CreateEntryFromFile(_databasePath, DatabaseEntryName, CompressionLevel.Optimal);
            using (var verify = ZipFile.OpenRead(temp)) { if (verify.GetEntry(DatabaseEntryName) is null) throw new InvalidDataException("The new database package failed verification."); }
            if (File.Exists(_packagePath)) File.Copy(_packagePath, $"{_packagePath}-{DateTime.Now:yyyy-MM-dd-HHmmss-fff}-{Guid.NewGuid():N}.bak", false);
            File.Move(temp, _packagePath, true);
            RetainBackups();
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        return Task.CompletedTask;
    }

    private void RetainBackups()
    {
        var directory = Path.GetDirectoryName(_packagePath)!;
        var backupPattern = $"{Path.GetFileName(_packagePath)}-*.bak";
        var backups = Directory.EnumerateFiles(directory, backupPattern)
            .Select(path => new
            {
                Path = path,
                Timestamp = GetBackupTimestamp(path)
            })
            .OrderByDescending(backup => backup.Timestamp)
            .ToList();

        var keep = backups.Take(5).ToList();
        if (keep.Count == 5 && keep.All(backup => backup.Timestamp.Date == DateTime.Now.Date))
        {
            var priorDayBackup = backups.Skip(5)
                .FirstOrDefault(backup => backup.Timestamp.Date < DateTime.Now.Date);
            if (priorDayBackup is not null) keep.Add(priorDayBackup);
        }

        var keepPaths = keep.Select(backup => backup.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var backup in backups.Where(backup => !keepPaths.Contains(backup.Path)))
        {
            try { File.Delete(backup.Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private DateTime GetBackupTimestamp(string path)
    {
        var name = Path.GetFileName(path);
        var packagePrefix = Path.GetFileName(_packagePath);
        var marker = $"{packagePrefix}-";
        if (name.StartsWith(marker, StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            var timestampText = name[marker.Length..^4].Split('-')[0..5];
            if (DateTime.TryParseExact(
                    string.Join('-', timestampText),
                    "yyyy-MM-dd-HHmmss-fff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
            {
                return timestamp;
            }
        }

        return File.GetLastWriteTime(path);
    }
}
