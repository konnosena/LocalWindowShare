using System.Drawing;
using System.Text.Json;

internal sealed record LoginRequest(string Token);

internal sealed record ClickInputRequest(double XRatio, double YRatio, int? Clicks, string? Button);

internal sealed record PointerInputRequest(string Action, double XRatio, double YRatio, string? Button, int? Clicks, int? WheelDelta);

internal sealed record TextInputRequest(string Text);

internal sealed record KeyInputRequest(string Key);

internal sealed record WebRtcSignalMessage(
    string? Type,
    string? Sdp,
    string? Candidate,
    string? SdpMid,
    int? SdpMLineIndex,
    long? Handle,
    int? MaxWidth);

internal sealed record ClientLogRequest(string? Level, string? Source, string? Message, JsonElement? Context);

internal sealed record LaunchAppRequest(string App);

internal sealed record ResizeWindowRequest(int Width, int Height);

internal sealed record ResizeResult(OperationError? Error, WindowBounds PreviousBounds);

internal sealed record OperationError(int StatusCode, string Message);

internal sealed record WindowSummary(
    long Handle,
    string Title,
    int ProcessId,
    string ProcessName,
    string ClassName,
    bool IsMinimized,
    bool IsForeground,
    WindowBounds Bounds);

internal sealed record WindowFrame(
    WindowSummary Window,
    int Width,
    int Height,
    byte[] ImageBytes,
    string ContentType);

internal readonly record struct WindowBounds(int Left, int Top, int Width, int Height)
{
    public Rectangle ToRectangle() => new(Left, Top, Width, Height);
}
