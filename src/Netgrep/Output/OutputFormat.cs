namespace Netgrep.Output;

internal enum OutputFormat
{
    Text,
    Json,
    Ndjson,
}

internal static class OutputFormats
{
    public static OutputFormat Parse(string? value)
    {
        return (value ?? "text").ToLowerInvariant() switch
        {
            "text" => OutputFormat.Text,
            "json" => OutputFormat.Json,
            "ndjson" => OutputFormat.Ndjson,
            _ => throw new ArgumentException($"Unknown format '{value}'. Expected text, json, or ndjson."),
        };
    }
}
