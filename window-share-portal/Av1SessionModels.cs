internal sealed record Av1SessionRequest(long Handle, int? MaxWidth, int FrameRate, string? StreamMode);

internal sealed record Av1SessionStopRequest(string? SessionId);

internal sealed record Av1SessionInfo(
    string SessionId,
    string ProducerName,
    string Backend,
    long Handle,
    int Width,
    int Height,
    int FrameRate,
    string StreamMode,
    int SignalingPort);
