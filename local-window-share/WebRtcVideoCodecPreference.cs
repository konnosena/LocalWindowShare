internal enum WebRtcVideoCodecPreference
{
    Auto,
    VP8,
    VP9,
}

internal sealed record WebRtcVideoCodecOption(string Value, string Label, bool Available, string Hint);

internal static class WebRtcVideoCodecPreferenceParser
{
    public static WebRtcVideoCodecPreference Parse(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "vp8" => WebRtcVideoCodecPreference.VP8,
            "vp9" => WebRtcVideoCodecPreference.VP9,
            _ => WebRtcVideoCodecPreference.Auto,
        };
    }

    public static string ToQueryValue(this WebRtcVideoCodecPreference value)
    {
        return value switch
        {
            WebRtcVideoCodecPreference.VP8 => "vp8",
            WebRtcVideoCodecPreference.VP9 => "vp9",
            _ => "auto",
        };
    }

    public static WebRtcVideoCodecPreference NormalizeForSupportedOptions(
        WebRtcVideoCodecPreference requestedPreference,
        IReadOnlyList<WebRtcVideoCodecOption> options,
        WebRtcVideoCodecPreference fallback = WebRtcVideoCodecPreference.Auto)
    {
        if (requestedPreference == WebRtcVideoCodecPreference.Auto)
        {
            return WebRtcVideoCodecPreference.Auto;
        }

        var requestedValue = requestedPreference.ToQueryValue();
        var supported = options.Any(option =>
            option.Available &&
            string.Equals(option.Value, requestedValue, StringComparison.OrdinalIgnoreCase));

        return supported ? requestedPreference : fallback;
    }

    public static IReadOnlyList<WebRtcVideoCodecOption> GetUiOptions()
    {
        return
        [
            new("auto", "Auto", true, "15/30fps は VP9、45/60fps と Speed は VP8 を優先します。"),
            new("vp8", "VP8", true, "互換性優先です。"),
            new("vp9", "VP9", true, "高効率です。profile-id=0 を優先して接続します。"),
        ];
    }
}
