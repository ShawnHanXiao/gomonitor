using System.IO;
using System.Text.Json;

namespace GoMonitor.Services;

public sealed class AuthKeyProvider
{
    private readonly string _authJsonPath;

    public AuthKeyProvider(string authJsonPath) => _authJsonPath = authJsonPath;

    public string? Resolve(string? overrideKey) =>
        !string.IsNullOrWhiteSpace(overrideKey) ? overrideKey.Trim() : ReadFromAuthJson();

    public string? ReadFromAuthJson()
    {
        if (!File.Exists(_authJsonPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_authJsonPath));
            if (document.RootElement.TryGetProperty("opencode-go", out var entry)
                && entry.TryGetProperty("key", out var key))
            {
                return key.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
