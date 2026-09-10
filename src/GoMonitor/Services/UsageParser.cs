using System.Text;
using System.Text.Json;

namespace GoMonitor.Services;

public sealed record UsageTokens(
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens);

/// <summary>
/// Parses usage token fields from OpenAI / Anthropic style JSON payloads,
/// both for complete bodies and incrementally for SSE streams.
/// </summary>
public static class UsageParser
{
    public static UsageTokens? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindUsage(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Searches an element tree depth-first for an object with token usage fields.</summary>
    private static UsageTokens? FindUsage(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var usage = ReadUsageObject(element);
                if (usage is not null)
                {
                    return usage;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        var nested = FindUsage(property.Value);
                        if (nested is not null)
                        {
                            return nested;
                        }
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindUsage(item);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    private static UsageTokens? ReadUsageObject(JsonElement obj)
    {
        long? input = null;
        long? output = null;
        long? cacheRead = null;
        long? cacheWrite = null;
        var found = false;

        foreach (var property in obj.EnumerateObject())
        {
            switch (property.Name)
            {
                case "prompt_tokens":
                case "input_tokens":
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        input = property.Value.GetInt64();
                        found = true;
                    }

                    break;
                case "completion_tokens":
                case "output_tokens":
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        output = property.Value.GetInt64();
                        found = true;
                    }

                    break;
                case "prompt_tokens_details":
                    // OpenAI nests cached tokens here.
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var nested in property.Value.EnumerateObject())
                        {
                            if (nested.Name == "cached_tokens" && nested.Value.ValueKind == JsonValueKind.Number)
                            {
                                cacheRead = nested.Value.GetInt64();
                                found = true;
                            }
                        }
                    }

                    break;
            }

            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Name.Contains("cache", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Name.Contains("read", StringComparison.OrdinalIgnoreCase))
                {
                    cacheRead = property.Value.GetInt64();
                    found = true;
                }
                else if (property.Name.Contains("creation", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("write", StringComparison.OrdinalIgnoreCase))
                {
                    cacheWrite = property.Value.GetInt64();
                    found = true;
                }
            }
        }

        return found ? new UsageTokens(input, output, cacheRead, cacheWrite) : null;
    }
}

/// <summary>
/// Incremental scanner for SSE / chunked streams. Feed raw decoded text chunks;
/// it buffers until a complete "usage" JSON object can be extracted. Merges
/// Anthropic's message_start (input) and message_delta (cumulative output) events.
/// </summary>
public sealed class UsageStreamScanner
{
    private const int MaxBufferChars = 512 * 1024;

    private readonly StringBuilder _buffer = new();
    private long? _input;
    private long? _output;
    private long? _cacheRead;
    private long? _cacheWrite;

    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _buffer.Append(text);
        if (_buffer.Length > MaxBufferChars)
        {
            // Protect against pathological streams; keep the tail where usage may appear.
            _buffer.Remove(0, _buffer.Length - MaxBufferChars);
        }

        ScanBuffer();
    }

    /// <summary>Merged usage across all events seen so far, or null when nothing found.</summary>
    public UsageTokens? BuildResult() =>
        _input is null && _output is null && _cacheRead is null && _cacheWrite is null
            ? null
            : new UsageTokens(_input, _output, _cacheRead, _cacheWrite);

    private void ScanBuffer()
    {
        var text = _buffer.ToString();
        var consumedUpTo = 0;
        var searchFrom = 0;

        while (true)
        {
            var idx = text.IndexOf("\"usage\"", searchFrom, StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }

            var colon = text.IndexOf(':', idx + 7);
            if (colon < 0)
            {
                break;
            }

            var start = -1;
            for (var i = colon + 1; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    start = i;
                    break;
                }

                if (!char.IsWhiteSpace(text[i]))
                {
                    break;
                }
            }

            if (start < 0)
            {
                searchFrom = colon + 1;
                continue;
            }

            var end = FindBalancedEnd(text, start);
            if (end < 0)
            {
                // Incomplete JSON: wait for more data.
                break;
            }

            if (UsageParser.Parse(text[start..(end + 1)]) is { } tokens)
            {
                Merge(tokens);
            }

            consumedUpTo = end + 1;
            searchFrom = consumedUpTo;
        }

        if (consumedUpTo > 0)
        {
            _buffer.Remove(0, consumedUpTo);
        }
    }

    private static int FindBalancedEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private void Merge(UsageTokens tokens)
    {
        // Anthropic: message_start carries input + cache, message_delta carries
        // cumulative output. First-seen input, last-seen output.
        _input ??= tokens.InputTokens;
        if (tokens.OutputTokens is not null)
        {
            _output = tokens.OutputTokens;
        }

        _cacheRead ??= tokens.CacheReadTokens;
        _cacheWrite ??= tokens.CacheWriteTokens;
    }
}
