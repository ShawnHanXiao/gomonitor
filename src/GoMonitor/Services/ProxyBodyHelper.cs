using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoMonitor.Services;

/// <summary>Pure helpers for reading and rewriting proxied request bodies.</summary>
public static class ProxyBodyHelper
{
    public const string ModelPrefix = "proxy-";

    /// <summary>Extracts the "model" property from a JSON request body, if present.</summary>
    public static string? ExtractModel(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String
                    ? model.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rewrites the "model" property of a JSON request body. Returns the original
    /// string when the body is not JSON or does not contain a model property.
    /// </summary>
    public static string RewriteModel(string requestBody, string newModel)
    {
        try
        {
            using var document = JsonDocument.Parse(requestBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("model", out _))
            {
                return requestBody;
            }

            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteWithModelProperty(document.RootElement, writer, newModel);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return requestBody;
        }
    }

    /// <summary>
    /// Strips the "proxy-" alias prefix. Returns the original name when the prefix is absent.
    /// </summary>
    public static (string ActualModel, bool WasRewritten) StripAliasPrefix(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model)
            && model.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (model[ModelPrefix.Length..], true);
        }

        return (model ?? string.Empty, false);
    }

    /// <summary>
    /// Computes a stable conversation hash from the system prompt and first user
    /// message of a chat-completions/messages style body. Returns null when the
    /// body is unusable for session identification.
    /// </summary>
    public static string? ComputeSessionHash(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var systemText = ExtractFirstText(root, "system") ?? string.Empty;
            var firstUserText = ExtractFirstUserText(root);
            if (systemText.Length == 0 && string.IsNullOrEmpty(firstUserText))
            {
                return null;
            }

            var combined = $"{systemText}\u0000{firstUserText}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
            return Convert.ToHexString(hash, 0, 8);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractFirstUserText(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("role", out var role)
                || role.ValueKind != JsonValueKind.String
                || !string.Equals(role.GetString(), "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = ExtractMessageText(message);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ExtractFirstText(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join(
                "\n",
                EnumerateTextParts(value)),
            _ => null,
        };
    }

    private static string? ExtractMessageText(JsonElement message)
    {
        if (message.TryGetProperty("content", out var content))
        {
            return content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Join("\n", EnumerateTextParts(content)),
                _ => null,
            };
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTextParts(JsonElement array)
    {
        foreach (var part in array.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                yield return part.GetString() ?? string.Empty;
            }
            else if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                yield return text.GetString() ?? string.Empty;
            }
        }
    }

    private static void WriteWithModelProperty(JsonElement source, Utf8JsonWriter writer, string newModel)
    {
        writer.WriteStartObject();
        foreach (var property in source.EnumerateObject())
        {
            if (property.Name == "model")
            {
                writer.WritePropertyName("model");
                writer.WriteStringValue(newModel);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }
}
