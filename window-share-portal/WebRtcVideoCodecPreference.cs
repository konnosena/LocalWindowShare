internal enum WebRtcVideoCodecPreference
{
    Auto,
    VP8,
    VP9,
    AV1,
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
            "av1" => WebRtcVideoCodecPreference.AV1,
            _ => WebRtcVideoCodecPreference.Auto,
        };
    }

    public static string ToQueryValue(this WebRtcVideoCodecPreference value)
    {
        return value switch
        {
            WebRtcVideoCodecPreference.VP8 => "vp8",
            WebRtcVideoCodecPreference.VP9 => "vp9",
            WebRtcVideoCodecPreference.AV1 => "av1",
            _ => "auto",
        };
    }

    public static IReadOnlyList<WebRtcVideoCodecOption> GetUiOptions()
    {
        return
        [
            new("auto", "Auto", true, "利用可能な codec の中から最適なものを使います。"),
            new("vp8", "VP8", true, "互換性優先です。"),
            new("vp9", "VP9", false, "現在の送信ライブラリでは未対応です。"),
            new("av1", "AV1", false, "現在のサーバービルドでは未対応です。"),
        ];
    }
}
