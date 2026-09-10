using System.IO;
using System.Net.Http;
using System.Text.Json;
using GoMonitor.Models;

namespace GoMonitor.Services;

/// <summary>
/// Fetches the upstream model catalog and diffs it against the stored snapshot
/// to surface newly added / removed Go plan models.
/// </summary>
public sealed class ModelCatalogService
{
    private const string ModelsUrl = "https://opencode.ai/zen/go/v1/models";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _snapshotPath;

    public ModelCatalogService(HttpClient http, string snapshotPath)
    {
        _http = http;
        _snapshotPath = snapshotPath;
    }

    public async Task<ModelCatalogDiff?> FetchDiffAsync(CancellationToken cancellationToken = default)
    {
        var models = await FetchAsync(cancellationToken).ConfigureAwait(false);
        if (models is null || models.Count == 0)
        {
            return null;
        }

        var known = LoadSnapshot();
        var freshIds = models.Select(m => m.Id).ToHashSet();

        if (known is null)
        {
            // First run: establish the baseline silently, no diff reported.
            SaveSnapshot(models);
            return null;
        }

        var knownIds = known;
        var diff = ComputeDiff(freshIds, knownIds);

        if (diff.HasChanges)
        {
            SaveSnapshot(models);
        }

        return diff;
    }

    /// <summary>Marks the current upstream catalog as seen without reporting a diff.</summary>
    public async Task AcknowledgeAsync(CancellationToken cancellationToken = default)
    {
        var models = await FetchAsync(cancellationToken).ConfigureAwait(false);
        if (models is not null && models.Count > 0)
        {
            SaveSnapshot(models);
        }
    }

    public static ModelCatalogDiff ComputeDiff(IEnumerable<string> freshIds, IEnumerable<string> knownIds)
    {
        var fresh = freshIds.ToHashSet();
        var known = knownIds.ToHashSet();
        return new ModelCatalogDiff(
            fresh.Except(known).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            known.Except(fresh).OrderBy(id => id, StringComparer.Ordinal).ToList());
    }

    private async Task<List<ModelInfo>?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(ModelsUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<ModelListDto>(json, JsonOptions);
            return dto?.Data?.Where(m => !string.IsNullOrEmpty(m.Id)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    private List<string>? LoadSnapshot()
    {
        if (!File.Exists(_snapshotPath))
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<ModelCatalogSnapshot>(File.ReadAllText(_snapshotPath), JsonOptions);
            return snapshot?.ModelIds?.ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveSnapshot(List<ModelInfo> models)
    {
        try
        {
            var directory = Path.GetDirectoryName(_snapshotPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var snapshot = new ModelCatalogSnapshot(
                DateTimeOffset.Now,
                models.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal).ToList());
            File.WriteAllText(_snapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed snapshot write just means the next fetch reports the same diff.
        }
    }

    private sealed class ModelListDto
    {
        public List<ModelInfo>? Data { get; set; }
    }
}
