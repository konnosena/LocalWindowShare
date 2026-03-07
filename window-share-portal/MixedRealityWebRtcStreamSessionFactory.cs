using Microsoft.Extensions.Logging;

internal sealed class MixedRealityWebRtcStreamSessionFactory : IWebRtcStreamSessionFactory
{
    public string BackendName => "libwebrtc";

    public IReadOnlyList<WebRtcVideoCodecOption> SupportedVideoCodecOptions =>
    [
        new("auto", "Auto", true, "15/30fps は VP9、45/60fps と Speed は VP8 を優先します。"),
        new("vp8", "VP8", true, "互換性優先です。"),
        new("vp9", "VP9", true, "高効率です。profile-id=0 を優先して接続します。"),
        new("av1", "AV1", false, "選択中の libwebrtc wrapper では未対応です。"),
    ];

    public WebRtcVideoCodecPreference NormalizeRequestedVideoCodecPreference(WebRtcVideoCodecPreference requestedPreference)
    {
        return WebRtcVideoCodecPreferenceParser.NormalizeForSupportedOptions(requestedPreference, SupportedVideoCodecOptions);
    }

    public IWebRtcStreamSession Create(WebRtcStreamSessionOptions options, IServiceProvider services)
    {
        var broker = services.GetRequiredService<WindowBroker>();
        var logger = services.GetRequiredService<ILogger<MixedRealityWebRtcStreamSession>>();
        return new MixedRealityWebRtcStreamSession(
            broker,
            options.WindowHandle,
            options.MaxWidth,
            options.FrameRate,
            options.StreamMode,
            options.RequestedVideoCodecPreference,
            options.SignalingSocket,
            logger);
    }
}
