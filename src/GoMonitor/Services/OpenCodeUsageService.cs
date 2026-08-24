using System.Net.Http;
using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using GoMonitor.Models;

namespace GoMonitor.Services;

public sealed class OpenCodeUsageService
{
    private const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<UsageSnapshot> FetchAsync(string? key, CancellationToken cancellationToken = default)
    {
        var fetchedAt = DateTimeOffset.Now;
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return UsageSnapshot.Error(fetchedAt, "No API key configured.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return UsageSnapshot.Error(fetchedAt, $"HTTP {(int)response.StatusCode} from usage endpoint.");
            }

            return Parse(json, fetchedAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return UsageSnapshot.Error(fetchedAt, ex.Message);
        }
    }

    public static UsageSnapshot Parse(string json, DateTimeOffset fetchedAt)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<UsageApiResponse>(json, JsonOptions);
            var usage = dto?.Usage;
            return new UsageSnapshot(
                ToWindow(usage?.Rolling),
                ToWindow(usage?.Weekly),
                ToWindow(usage?.Monthly),
                fetchedAt,
                false,
                null);
        }
        catch (JsonException)
        {
            return UsageSnapshot.Error(fetchedAt, "Malformed usage response.");
        }
    }

    private static UsageWindow ToWindow(UsagePeriodDto? dto)
    {
        if (dto is null)
        {
            return new UsageWindow("missing", null, null);
        }

        DateTimeOffset? resetsAt = null;
        if (DateTimeOffset.TryParse(
                dto.ResetsAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            resetsAt = parsed;
        }

        return new UsageWindow(dto.Status ?? "missing", dto.Percent, resetsAt);
    }

    private sealed class UsageApiResponse
    {
        public UsagePeriodContainer? Usage { get; set; }
    }

    private sealed class UsagePeriodContainer
    {
        public UsagePeriodDto? Rolling { get; set; }
        public UsagePeriodDto? Weekly { get; set; }
        public UsagePeriodDto? Monthly { get; set; }
    }

    private sealed class UsagePeriodDto
    {
        public string? Status { get; set; }
        public double? Percent { get; set; }
        public string? ResetsAt { get; set; }
    }
}
