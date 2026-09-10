using System.IO;
using System.Text;
using System.Text.Json;
using GoMonitor.Models;

namespace GoMonitor.Services;

/// <summary>
/// Appends proxy request records to daily jsonl files and keeps an in-memory
/// aggregate of the current day. Survives restarts by replaying today's file.
/// </summary>
public sealed class ProxyUsageRecorder
{
    private const int RetentionDays = 90;

    private readonly string _directory;
    private readonly object _lock = new();
    private readonly List<ProxyRequestRecord> _recent = new();
    private readonly Dictionary<string, (int Requests, long In, long Out, long CacheRead)> _byModel = new();

    private int _totalRequests;
    private int _errorRequests;
    private long _inputTokens;
    private long _outputTokens;
    private long _cacheReadTokens;
    private long _cacheWriteTokens;

    public ProxyUsageRecorder(string usageDirectory) => _directory = usageDirectory;

    /// <summary>Rebuilds today's aggregate from disk and prunes stale files.</summary>
    public void LoadToday()
    {
        lock (_lock)
        {
            _recent.Clear();
            _byModel.Clear();
            _totalRequests = 0;
            _errorRequests = 0;
            _inputTokens = 0;
            _outputTokens = 0;
            _cacheReadTokens = 0;
            _cacheWriteTokens = 0;

            try
            {
                Directory.CreateDirectory(_directory);
                PruneOldFiles(DateTime.Now.Date);

                var todayPath = PathFor(DateTime.Now.Date);
                if (!File.Exists(todayPath))
                {
                    return;
                }

                foreach (var line in File.ReadLines(todayPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    ProxyRequestRecord? record;
                    try
                    {
                        record = JsonSerializer.Deserialize<ProxyRequestRecord>(line);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (record is not null)
                    {
                        Aggregate(record);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Stats are best-effort; a failed load starts an empty day.
            }
        }
    }

    public void Record(ProxyRequestRecord record)
    {
        lock (_lock)
        {
            Aggregate(record);
            _recent.Add(record);
            if (_recent.Count > 5)
            {
                _recent.RemoveAt(0);
            }
        }

        try
        {
            var directory = Path.GetDirectoryName(PathFor(record.Timestamp.LocalDateTime.Date));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                PathFor(record.Timestamp.LocalDateTime.Date),
                JsonSerializer.Serialize(record) + Environment.NewLine,
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In-memory aggregate still reflects the request even if the disk write fails.
        }
    }

    public ProxyDailyStats Snapshot()
    {
        lock (_lock)
        {
            var models = _byModel
                .Select(kv => new ProxyModelStat(kv.Key, kv.Value.Requests, kv.Value.In, kv.Value.Out, kv.Value.CacheRead))
                .OrderByDescending(m => m.Requests)
                .ToList();
            return new ProxyDailyStats(
                _totalRequests,
                _errorRequests,
                _inputTokens,
                _outputTokens,
                _cacheReadTokens,
                _cacheWriteTokens,
                models);
        }
    }

    public IReadOnlyList<ProxyRequestRecord> RecentRequests()
    {
        lock (_lock)
        {
            return _recent.ToArray();
        }
    }

    private void Aggregate(ProxyRequestRecord record)
    {
        _totalRequests++;
        if (record.HasError)
        {
            _errorRequests++;
        }

        _inputTokens += record.InputTokens;
        _outputTokens += record.OutputTokens;
        _cacheReadTokens += record.CacheReadTokens;
        _cacheWriteTokens += record.CacheWriteTokens;

        var model = string.IsNullOrEmpty(record.Model) ? "(unknown)" : record.Model;
        var (requests, input, output, cacheRead) = _byModel.TryGetValue(model, out var current)
            ? current
            : (0, 0L, 0L, 0L);
        _byModel[model] = (
            requests + 1,
            input + record.InputTokens,
            output + record.OutputTokens,
            cacheRead + record.CacheReadTokens);
    }

    private string PathFor(DateTime date) =>
        Path.Combine(_directory, $"{date:yyyy-MM-dd}.jsonl");

    private void PruneOldFiles(DateTime today)
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateTime.TryParseExact(name, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date)
                && (today - date).TotalDays > RetentionDays)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Ignore; retried on the next startup.
                }
            }
        }
    }
}
