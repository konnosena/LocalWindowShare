using Microsoft.Extensions.Logging;

internal sealed class SipsorceryWebRtcStreamSessionFactory : IWebRtcStreamSessionFactory
{
    public string BackendName => "sipsorcery";

    public IReadOnlyList<WebRtcVideoCodecOption> SupportedVideoCodecOptions =>
    [
        new("auto", "Auto", true, "利用可能な codec の中から最適なものを使います。"),
        new("vp8", "VP8", true, "互換性優先です。"),
        new("vp9", "VP9", false, "現在の送信ライブラリでは未対応です。"),
        new("av1", "AV1", false, "現在のサーバービルドでは未対応です。"),
    ];

    public IWebRtcStreamSession Create(WebRtcStreamSessionOptions options, IServiceProvider services)
    {
        var broker = services.GetRequiredService<WindowBroker>();
        var logger = services.GetRequiredService<ILogger<WebRtcWindowStreamSession>>();
        return new WebRtcWindowStreamSession(
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
