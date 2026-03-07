using System.Net.WebSockets;

internal sealed record WebRtcStreamSessionOptions(
    long WindowHandle,
    int? MaxWidth,
    int FrameRate,
    StreamTuningMode StreamMode,
    WebRtcVideoCodecPreference RequestedVideoCodecPreference,
    WebSocket SignalingSocket);

internal interface IWebRtcStreamSession : IAsyncDisposable
{
    Task RunAsync(CancellationToken cancellationToken);
}

internal interface IWebRtcStreamSessionFactory
{
    string BackendName { get; }

    IReadOnlyList<WebRtcVideoCodecOption> SupportedVideoCodecOptions { get; }

    IWebRtcStreamSession Create(WebRtcStreamSessionOptions options, IServiceProvider services);
}
