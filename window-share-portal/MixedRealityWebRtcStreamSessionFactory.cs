using Microsoft.Extensions.Logging;

internal sealed class MixedRealityWebRtcStreamSessionFactory : IWebRtcStreamSessionFactory
{
    public string BackendName => "libwebrtc";

    public IReadOnlyList<WebRtcVideoCodecOption> SupportedVideoCodecOptions =>
    [
        new("auto", "Auto", true, "libwebrtc に任せて最適な codec を選びます。"),
        new("vp8", "VP8", true, "互換性優先です。"),
        new("vp9", "VP9", true, "libwebrtc backend で利用できます。"),
        new("av1", "AV1", false, "選択中の libwebrtc wrapper では未対応です。"),
    ];

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
