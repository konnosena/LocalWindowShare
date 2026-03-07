internal enum StreamTuningMode
{
    LowLatency,
    Balanced,
    HighQuality,
}

internal static class StreamTuningModeParser
{
    public static StreamTuningMode Parse(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "low-latency" or "latency" or "speed" => StreamTuningMode.LowLatency,
            "high-quality" or "quality" => StreamTuningMode.HighQuality,
            _ => StreamTuningMode.Balanced,
        };
    }

    public static string ToQueryValue(this StreamTuningMode mode)
    {
        return mode switch
        {
            StreamTuningMode.LowLatency => "low-latency",
            StreamTuningMode.HighQuality => "high-quality",
            _ => "balanced",
        };
    }
}
