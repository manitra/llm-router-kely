using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouterKely.Core.Statistics;

namespace RouterKely.Statistics;

/// <summary>
/// Durability decorator over the in-memory statistics provider. It is written to only from the
/// background pump, never from an inference request: the request path still updates atomics, and
/// this class adds at most one small file write per dirty UTC day per flush.
/// </summary>
/// <remarks>
/// Layout: one versioned JSON document per UTC day at <c>{directory}/{yyyy-MM-dd}.json</c>, replaced
/// atomically through a temporary file in the same directory. Files past the configured daily
/// retention are pruned on the cold path. On startup <see cref="LoadAsync"/> installs the retained
/// days into the in-memory provider and returns the current UTC day's cost per user, so consumed
/// quota can be seeded and a restart cannot hand a user back the budget they already spent.
/// </remarks>
public sealed class FileStatisticsProvider : IStatisticsProvider
{
    private const int FormatVersion = 1;
    private const string DateFormat = "yyyy-MM-dd";

    private readonly InMemoryStatisticsProvider _inner;
    private readonly ILogger _logger;
    private readonly string _directory;
    private readonly int _dailyRetentionDays;
    private readonly object _gate = new();
    private bool _writeFailing;
    private DateOnly? _lastPrunedDay;

    public FileStatisticsProvider(
        InMemoryStatisticsProvider inner,
        string directory,
        int dailyRetentionDays,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);
        if (dailyRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(dailyRetentionDays));

        _inner = inner;
        _directory = directory;
        _dailyRetentionDays = dailyRetentionDays;
        _logger = logger;
    }

    /// <summary>The directory holding the per-day usage files.</summary>
    public string DirectoryPath => _directory;

    public ValueTask<StatisticsSnapshot> RestoreAsync(CancellationToken cancellationToken) =>
        _inner.RestoreAsync(cancellationToken);

    public async ValueTask WriteAsync(UsageBatch batch, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        if (batch.Entries.Length == 0)
            return;

        DateOnly currentDay = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = new SortedSet<DateOnly>();
        foreach (UsageAggregate entry in batch.Entries)
            days.Add(DateOnly.FromDateTime(entry.PeriodHour.UtcDateTime));

        var panes = new List<(DateOnly Day, UsageRowsSnapshot Rows)>(days.Count);
        foreach (DateOnly day in days)
        {
            UsageRowsSnapshot rows = await _inner
                .QueryUsageRowsAsync(day, day, cancellationToken)
                .ConfigureAwait(false);
            panes.Add((day, rows));
        }

        lock (_gate)
        {
            foreach ((DateOnly day, UsageRowsSnapshot rows) in panes)
                PersistDay(day, rows);

            // Expiry is a day-granular concern, so prune at most once per UTC day instead of on
            // every flush.
            if (_lastPrunedDay != currentDay)
            {
                PruneExpired(currentDay);
                _lastPrunedDay = currentDay;
            }
        }
    }

    public ValueTask<StatisticsSnapshot> QueryAsync(
        DateOnly startDate,
        DateOnly endDate,
        long? userId,
        long? keyId,
        CancellationToken cancellationToken) =>
        _inner.QueryAsync(startDate, endDate, userId, keyId, cancellationToken);

    public ValueTask<UsageRowsSnapshot> QueryUsageRowsAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken) =>
        _inner.QueryUsageRowsAsync(startDate, endDate, cancellationToken);

    /// <summary>
    /// Reads the retained day files, installs them into the in-memory provider, and returns the
    /// current UTC day's cost per user for quota seeding. A missing directory means a first start
    /// and restores nothing; an unreadable or malformed file fails startup with a remedy.
    /// </summary>
    public ValueTask<IReadOnlyDictionary<long, long>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory();

        DateOnly currentDay = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly minimumDay = currentDay.AddDays(-_dailyRetentionDays + 1);

        var rows = new List<UsageRow>();
        foreach (string path in EnumerateStoreFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetExtension(path), ".tmp", StringComparison.OrdinalIgnoreCase) ||
                !TryParseDay(Path.GetFileNameWithoutExtension(path), out DateOnly day))
                continue; // a temporarily named or foreign file is not ours to fail on
            if (day < minimumDay)
                continue; // past retention: it is about to be pruned anyway

            rows.AddRange(ReadDayFile(path, day));
        }

        var snapshot = new UsageRowsSnapshot(rows.ToArray());
        _inner.InstallDailyRows(snapshot);

        var costByUser = new Dictionary<long, long>();
        foreach (UsageRow row in snapshot.Rows)
        {
            if (row.Date == currentDay)
                costByUser[row.UserId] = costByUser.GetValueOrDefault(row.UserId) + row.CostNanoUsd;
        }

        return ValueTask.FromResult<IReadOnlyDictionary<long, long>>(costByUser);
    }

    private void PersistDay(DateOnly day, UsageRowsSnapshot rows)
    {
        try
        {
            WriteDayFile(day, rows);
            if (_writeFailing)
            {
                _writeFailing = false;
                _logger.LogInformation("Usage statistics store '{Directory}' is writable again.", _directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Requests must never stop because a reporting store failed to write. Report the failure
            // once per episode instead of once per flush.
            if (!_writeFailing)
            {
                _writeFailing = true;
                _logger.LogError(
                    exception,
                    "Could not write usage statistics to '{Directory}'. Requests keep being served; usage collected since the last successful write exists only in memory.",
                    _directory);
            }
        }
    }

    private void WriteDayFile(DateOnly day, UsageRowsSnapshot rows)
    {
        string date = day.ToString(DateFormat, CultureInfo.InvariantCulture);
        var document = new UsageFileDocument(
            FormatVersion,
            date,
            rows.Rows
                .Select(row => new UsageFileRow(
                    row.UserId,
                    row.KeyId,
                    row.ModelAlias,
                    row.RequestCount,
                    row.InputTokens,
                    row.CachedInputTokens,
                    row.OutputTokens,
                    row.CostNanoUsd))
                .ToArray());

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            document,
            UsageFileJsonContext.Default.UsageFileDocument);

        string path = Path.Combine(_directory, date + ".json");
        string tempPath = path + ".tmp";
        try
        {
            using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload);
                stream.Flush(true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    private void PruneExpired(DateOnly currentDay)
    {
        DateOnly minimumDay = currentDay.AddDays(-_dailyRetentionDays + 1);
        foreach (string path in EnumerateStoreFiles())
        {
            bool expiredDay = TryParseDay(Path.GetFileNameWithoutExtension(path), out DateOnly day) &&
                day < minimumDay;
            bool abandonedTemp = string.Equals(Path.GetExtension(path), ".tmp", StringComparison.OrdinalIgnoreCase);
            if (!expiredDay && !abandonedTemp)
                continue;

            try
            {
                File.Delete(path);
            }
            catch (IOException) { /* best effort: the next flush retries */ }
            catch (UnauthorizedAccessException) { /* best effort: the next flush retries */ }
        }
    }

    private void EnsureDirectory()
    {
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Usage statistics directory '{_directory}' could not be created: {exception.Message}. "
                + "Check that the path is writable by this process, or clear "
                + "Statistics.PersistenceDirectoryPath to run without persistence.",
                exception);
        }
    }

    private UsageRow[] ReadDayFile(string path, DateOnly day)
    {
        byte[] payload;
        try
        {
            payload = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Corrupt(path, $"it could not be read ({exception.Message})", exception);
        }

        UsageFileDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(payload, UsageFileJsonContext.Default.UsageFileDocument);
        }
        catch (JsonException exception)
        {
            throw Corrupt(path, $"it is not valid JSON ({exception.Message})", exception);
        }

        if (document is null)
            throw Corrupt(path, "it is empty", null);
        if (document.Version != FormatVersion)
            throw Corrupt(path, $"its format version is {document.Version}, expected {FormatVersion}", null);

        string date = day.ToString(DateFormat, CultureInfo.InvariantCulture);
        if (!string.Equals(document.Date, date, StringComparison.Ordinal))
            throw Corrupt(path, $"it records date '{document.Date}' but is named '{date}'", null);
        if (document.Rows is null)
            throw Corrupt(path, "it has no rows member", null);

        var rows = new UsageRow[document.Rows.Length];
        for (int index = 0; index < document.Rows.Length; index++)
        {
            UsageFileRow row = document.Rows[index];
            if (string.IsNullOrWhiteSpace(row.Model) || row.UserId <= 0 || row.KeyId <= 0 ||
                row.Requests < 0 || row.InputTokens < 0 || row.CachedInputTokens < 0 ||
                row.OutputTokens < 0 || row.CostNanoUsd < 0)
            {
                throw Corrupt(path, "it contains an invalid row", null);
            }

            rows[index] = new UsageRow(
                day,
                row.UserId,
                row.KeyId,
                row.Model,
                row.Requests,
                row.InputTokens,
                row.CachedInputTokens,
                row.OutputTokens,
                row.CostNanoUsd);
        }

        return rows;
    }

    private string[] EnumerateStoreFiles()
    {
        try
        {
            return Directory.EnumerateFiles(_directory).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryParseDay(string name, out DateOnly day) =>
        DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static InvalidOperationException Corrupt(string path, string reason, Exception? inner) =>
        new(
            $"Usage statistics file '{path}' is not usable: {reason}. "
            + "Restore it from a backup, delete the file to start that day from zero, "
            + "or delete the whole directory to drop all persisted usage and restart.",
            inner);

}

internal sealed record UsageFileDocument(int Version, string Date, UsageFileRow[] Rows);

internal sealed record UsageFileRow(
    long UserId,
    long KeyId,
    string Model,
    long Requests,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long CostNanoUsd);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UsageFileDocument))]
internal sealed partial class UsageFileJsonContext : JsonSerializerContext;
