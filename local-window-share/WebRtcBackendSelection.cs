internal sealed record WebRtcBackendSelectionResult(
    string ConfiguredValue,
    string EffectiveValue,
    bool UsedFallback,
    IWebRtcStreamSessionFactory Factory);

internal static class WebRtcBackendSelection
{
    public static WebRtcBackendSelectionResult Resolve()
    {
        var configuredValue = (Environment.GetEnvironmentVariable("WINDOW_SHARE_PORTAL_WEBRTC_BACKEND") ?? string.Empty).Trim();
        var normalizedValue = configuredValue.ToLowerInvariant();

        return normalizedValue switch
        {
            "" or "libwebrtc" => new WebRtcBackendSelectionResult(
                configuredValue,
                "libwebrtc",
                false,
                new MixedRealityWebRtcStreamSessionFactory()),
            "sipsorcery" => new WebRtcBackendSelectionResult(
                configuredValue,
                "sipsorcery",
                false,
                new SipsorceryWebRtcStreamSessionFactory()),
            _ => new WebRtcBackendSelectionResult(
                configuredValue,
                "libwebrtc",
                true,
                new MixedRealityWebRtcStreamSessionFactory()),
        };
    }
}
