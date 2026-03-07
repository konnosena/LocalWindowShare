using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

internal sealed class WebRtcWindowStreamSession : IAsyncDisposable
{
    private const int MaxSignalMessageBytes = 128 * 1024;
    private static readonly JsonSerializerOptions SignalJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WindowBroker _broker;
    private readonly long _windowHandle;
    private readonly int? _maxWidth;
    private readonly int _frameRate;
    private readonly StreamTuningMode _streamMode;
    private readonly WebRtcVideoCodecPreference _requestedVideoCodecPreference;
    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _socketSendLock = new(1, 1);
    private readonly Lock _signalStateLock = new();
    private readonly VpxVideoEncoder _videoEncoder;
    private readonly RTCPeerConnection _peerConnection;
    private readonly MediaStreamTrack _videoTrack;
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly WindowsGraphicsCaptureSource? _graphicsCaptureSource;
    private readonly Queue<string> _pendingLocalIceCandidatePayloads = new();

    private Task? _captureLoopTask;
    private bool _disposed;
    private bool _localDescriptionSent;
    private VideoCodecsEnum _selectedVideoCodec = VideoCodecsEnum.VP8;
    private int _forceKeyFrameBudget = 3;

    public WebRtcWindowStreamSession(WindowBroker broker, long windowHandle, int? maxWidth, int frameRate, StreamTuningMode streamMode, WebRtcVideoCodecPreference requestedVideoCodecPreference, WebSocket socket, ILogger logger)
    {
        _broker = broker;
        _windowHandle = windowHandle;
        _maxWidth = maxWidth;
        _frameRate = Math.Clamp(frameRate, 15, 60);
        _streamMode = streamMode;
        _requestedVideoCodecPreference = requestedVideoCodecPreference;
        _socket = socket;
        _logger = logger;

        _peerConnection = new RTCPeerConnection(null);
        _videoEncoder = new VpxVideoEncoder();
        var supportedFormats = BuildSupportedFormats();
        if (supportedFormats.Count > 0)
        {
            _selectedVideoCodec = supportedFormats[0].Codec;
        }

        _videoTrack = new MediaStreamTrack(supportedFormats, MediaStreamStatusEnum.SendOnly);
        _peerConnection.addTrack(_videoTrack);
        _peerConnection.OnVideoFormatsNegotiated += formats =>
        {
            if (formats.Count > 0)
            {
                var selectedFormat = formats.First();
                _selectedVideoCodec = selectedFormat.Codec;
                _videoTrack.RestrictCapabilities(selectedFormat);
                _logger.LogInformation("Negotiated video codec {Codec} for HWND {Handle}.", selectedFormat.Codec, _windowHandle);
            }
        };
        _peerConnection.onicecandidate += HandleLocalIceCandidate;
        _peerConnection.onconnectionstatechange += HandleConnectionStateChanged;

        if (WindowsGraphicsCaptureSource.IsSupported)
        {
            try
            {
                _graphicsCaptureSource = new WindowsGraphicsCaptureSource((nint)_windowHandle);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Windows Graphics Capture was unavailable for HWND {Handle}. Falling back to bitmap capture.", _windowHandle);
            }
        }
    }

    private List<VideoFormat> BuildSupportedFormats()
    {
        var formats = _videoEncoder.SupportedFormats
            .Select(format => new VideoFormat(format))
            .ToList();

        if (formats.All(format => format.Codec != VideoCodecsEnum.VP8))
        {
            formats.Add(new VideoFormat(VideoCodecsEnum.VP8, 107, 90000, string.Empty));
        }

        formats = formats
            .Where(format => format.Codec == VideoCodecsEnum.VP8)
            .ToList();

        formats = ReorderFormatsByPreference(formats);
        var requestedCodec = _requestedVideoCodecPreference.ToQueryValue();
        var advertised = string.Join(", ", formats.Select(format => format.Codec.ToString()));
        _logger.LogInformation("Advertising video codecs {Codecs} for HWND {Handle}. Requested={Requested}.", advertised, _windowHandle, requestedCodec);
        if (_requestedVideoCodecPreference is WebRtcVideoCodecPreference.AV1 or WebRtcVideoCodecPreference.VP9)
        {
            _logger.LogWarning("{RequestedCodec} was requested for HWND {Handle}, but this build can only send VP8. Falling back to {Fallback}.", _requestedVideoCodecPreference, _windowHandle, formats.First().Codec);
        }

        return formats;
    }

    private List<VideoFormat> ReorderFormatsByPreference(List<VideoFormat> formats)
    {
        var orderedCodecs = _requestedVideoCodecPreference switch
        {
            _ => new[] { VideoCodecsEnum.VP8 },
        };

        return formats
            .OrderBy(format =>
            {
                var index = Array.IndexOf(orderedCodecs, format.Codec);
                return index >= 0 ? index : orderedCodecs.Length;
            })
            .ThenBy(format => format.FormatID)
            .ToList();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WebRTC session loop started for HWND {Handle}.", _windowHandle);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCts.Token);
        try
        {
            await ReceiveLoopAsync(linkedCts.Token);
        }
        finally
        {
            linkedCts.Cancel();
            _sessionCts.Cancel();

            if (_captureLoopTask is not null)
            {
                try
                {
                    await _captureLoopTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            await DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionCts.Cancel();
        _logger.LogInformation("Disposing WebRTC session for HWND {Handle}.", _windowHandle);

        try
        {
            _peerConnection.Close("session ended");
        }
        catch
        {
        }

        _peerConnection.onicecandidate -= HandleLocalIceCandidate;
        _peerConnection.onconnectionstatechange -= HandleConnectionStateChanged;

        if (_graphicsCaptureSource is not null)
        {
            await _graphicsCaptureSource.DisposeAsync();
        }

        _videoEncoder.Dispose();
        _peerConnection.Dispose();
        _socketSendLock.Dispose();
        _sessionCts.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogInformation("Signaling socket closed by client for HWND {Handle}.", _windowHandle);
                break;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            messageBuffer.Write(buffer, 0, result.Count);
            if (messageBuffer.Length > MaxSignalMessageBytes)
            {
                throw new InvalidOperationException("WebRTC signaling message exceeded the size limit.");
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
            messageBuffer.SetLength(0);
            _logger.LogInformation("Received signaling message for HWND {Handle}: {Payload}", _windowHandle, json);
            await HandleSignalMessageAsync(json, cancellationToken);
        }
    }

    private async Task HandleSignalMessageAsync(string json, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<WebRtcSignalMessage>(json, SignalJsonOptions);
        if (message is null)
        {
            _logger.LogWarning("Ignored empty signaling payload for HWND {Handle}.", _windowHandle);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Candidate))
        {
            _logger.LogInformation("Applying remote ICE candidate for HWND {Handle}. mid={SdpMid}, mline={MLine}", _windowHandle, message.SdpMid, message.SdpMLineIndex);
            _peerConnection.addIceCandidate(ToIceCandidate(message));
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Sdp))
        {
            _logger.LogWarning("Signaling payload had no SDP and no candidate for HWND {Handle}. type={Type}", _windowHandle, message.Type);
            return;
        }

        if (!string.Equals(message.Type, "offer", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Ignoring signaling type {Type} for HWND {Handle}.", message.Type, _windowHandle);
            return;
        }

        _logger.LogInformation("Applying remote offer for HWND {Handle}. SDP length={Length}.", _windowHandle, message.Sdp.Length);
        var remoteDescription = ToSessionDescription(message);
        var setDescriptionResult = _peerConnection.setRemoteDescription(remoteDescription);
        if (setDescriptionResult != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Failed to set remote description: {setDescriptionResult}.");
        }

        var activationError = _broker.ActivateWindow(_windowHandle);
        if (activationError is null)
        {
            _logger.LogInformation("Activated HWND {Handle} for capture.", _windowHandle);
        }
        else
        {
            _logger.LogWarning("Failed to activate HWND {Handle} before capture. {Message}", _windowHandle, activationError.Message);
        }

        _graphicsCaptureSource?.Start();
        var answerDescription = _peerConnection.createAnswer(null);
        _logger.LogInformation("Created local answer for HWND {Handle}. SDP length={Length}.", _windowHandle, answerDescription.sdp?.Length ?? 0);
        await _peerConnection.setLocalDescription(answerDescription);
        _logger.LogInformation("Local answer set for HWND {Handle}.", _windowHandle);
        await SendSignalAsync(new
        {
            type = answerDescription.type.ToString().ToLowerInvariant(),
            sdp = answerDescription.sdp,
        }, cancellationToken);
        _forceKeyFrameBudget = 3;
        FlushQueuedLocalIceCandidates(cancellationToken);
        _captureLoopTask ??= Task.Run(() => CaptureLoopAsync(_sessionCts.Token));
    }

    private void HandleLocalIceCandidate(RTCIceCandidate candidate)
    {
        if (_socket.State != WebSocketState.Open || string.IsNullOrWhiteSpace(candidate?.candidate))
        {
            return;
        }

        var payload = candidate.toJSON();
        lock (_signalStateLock)
        {
            if (!_localDescriptionSent)
            {
                _pendingLocalIceCandidatePayloads.Enqueue(payload);
                _logger.LogInformation("Queued local ICE candidate until answer is sent for HWND {Handle}.", _windowHandle);
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Sending local ICE candidate for HWND {Handle}.", _windowHandle);
                await SendRawTextAsync(payload, _sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send local ICE candidate.");
            }
        });
    }

    private void HandleConnectionStateChanged(RTCPeerConnectionState state)
    {
        _logger.LogDebug("WebRTC peer state changed to {State} for HWND {Handle}.", state, _windowHandle);

        switch (state)
        {
            case RTCPeerConnectionState.connected:
                _forceKeyFrameBudget = 3;
                _captureLoopTask ??= Task.Run(() => CaptureLoopAsync(_sessionCts.Token));
                break;
            case RTCPeerConnectionState.failed:
            case RTCPeerConnectionState.disconnected:
                _peerConnection.Close("ice disconnection");
                break;
            case RTCPeerConnectionState.closed:
                _sessionCts.Cancel();
                break;
        }
    }

    private void FlushQueuedLocalIceCandidates(CancellationToken cancellationToken)
    {
        List<string> pendingPayloads;
        lock (_signalStateLock)
        {
            _localDescriptionSent = true;
            if (_pendingLocalIceCandidatePayloads.Count == 0)
            {
                return;
            }

            pendingPayloads = new List<string>(_pendingLocalIceCandidatePayloads);
            _pendingLocalIceCandidatePayloads.Clear();
        }

        _ = Task.Run(async () =>
        {
            foreach (var payload in pendingPayloads)
            {
                try
                {
                    _logger.LogInformation("Sending queued local ICE candidate for HWND {Handle}.", _windowHandle);
                    await SendRawTextAsync(payload, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to send queued local ICE candidate.");
                }
            }
        }, _sessionCts.Token);
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Capture loop started for HWND {Handle}. Source={Source}.", _windowHandle, _graphicsCaptureSource is null ? "bitmap" : "wgc");
        var frameDurationRtp = (uint)Math.Max(1, Math.Round(90000d / _frameRate));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1, Math.Round(1000d / _frameRate))));
        var firstFrameLogged = false;
        var ticksWithoutFrame = 0;
        var lastFailureReason = "Waiting for the first frame.";

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (_socket.State != WebSocketState.Open)
            {
                continue;
            }

            var connectionState = _peerConnection.connectionState;
            if (connectionState is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
            {
                continue;
            }

            byte[]? encodedFrame = null;
            string encodeMessage = string.Empty;
            if (_graphicsCaptureSource is not null &&
                _graphicsCaptureSource.TryEncodeLatestFrame(_videoEncoder, _selectedVideoCodec, out encodedFrame, out var width, out var height, out encodeMessage))
            {
                MaybeForceKeyFrame();
                _peerConnection.SendVideo(frameDurationRtp, encodedFrame!);
                ticksWithoutFrame = 0;
                lastFailureReason = string.Empty;
                if (!firstFrameLogged)
                {
                    firstFrameLogged = true;
                    _logger.LogInformation("First WGC frame submitted for HWND {Handle}. Codec={Codec}, Size={Width}x{Height}, Bytes={Bytes}.", _windowHandle, _selectedVideoCodec, width, height, encodedFrame!.Length);
                }
                continue;
            }
            else if (_graphicsCaptureSource is not null && !string.IsNullOrWhiteSpace(encodeMessage))
            {
                lastFailureReason = $"WGC: {encodeMessage}";
            }

            if (!_broker.TryCaptureWindowBitmap(_windowHandle, _maxWidth, out var bitmap, out _, out var message, out var statusCode, preferScreenCaptureOnly: true, lowLatencyScaling: _streamMode == StreamTuningMode.LowLatency))
            {
                lastFailureReason = $"Bitmap capture: {statusCode} {message}";
            }
            else
            {
                using (bitmap)
                {
                    if (PushBitmap(bitmap, frameDurationRtp, out var pushMessage))
                    {
                        ticksWithoutFrame = 0;
                        lastFailureReason = string.Empty;
                        if (!firstFrameLogged)
                        {
                            firstFrameLogged = true;
                            _logger.LogInformation("First bitmap frame submitted for HWND {Handle}. Codec={Codec}, Size={Width}x{Height}.", _windowHandle, _selectedVideoCodec, bitmap.Width & ~1, bitmap.Height & ~1);
                        }

                        continue;
                    }

                    lastFailureReason = string.IsNullOrWhiteSpace(pushMessage)
                        ? "Bitmap frame was captured but the encoder returned no payload."
                        : pushMessage;
                }
            }

            ticksWithoutFrame++;
            if (!firstFrameLogged && ticksWithoutFrame % _frameRate == 0)
            {
                _logger.LogWarning("No video frame submitted yet for HWND {Handle}. PeerState={PeerState}. LastReason={Reason}", _windowHandle, _peerConnection.connectionState, lastFailureReason);
            }
        }
    }

    private bool PushBitmap(Bitmap bitmap, uint frameDurationRtp, out string message)
    {
        message = string.Empty;
        var width = bitmap.Width & ~1;
        var height = bitmap.Height & ~1;
        if (width < 2 || height < 2)
        {
            message = "The captured bitmap was too small after even-dimension alignment.";
            return false;
        }

        var bounds = new Rectangle(0, 0, width, height);
        var bitmapData = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixelBuffer = VideoFrameBuffer.CopyToContiguousBgraBuffer(bitmapData.Scan0, width, height, bitmapData.Stride);
            MaybeForceKeyFrame();

            byte[]? encodedFrame;
            try
            {
                encodedFrame = _videoEncoder.EncodeVideo(width, height, pixelBuffer, VideoPixelFormatsEnum.Bgra, _selectedVideoCodec);
            }
            catch (Exception ex)
            {
                message = $"The bitmap frame encoder failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            if (encodedFrame is { Length: > 0 })
            {
                _peerConnection.SendVideo(frameDurationRtp, encodedFrame);
                return true;
            }

            message = "The video encoder returned an empty frame.";
            return false;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private void MaybeForceKeyFrame()
    {
        if (_forceKeyFrameBudget <= 0)
        {
            return;
        }

        _videoEncoder.ForceKeyFrame();
        _forceKeyFrameBudget--;
    }

    private async Task SendSignalAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await SendRawTextAsync(json, cancellationToken);
    }

    private async Task SendRawTextAsync(string payload, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socketSendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        finally
        {
            _socketSendLock.Release();
        }
    }

    private static RTCSessionDescriptionInit ToSessionDescription(WebRtcSignalMessage message)
    {
        return new RTCSessionDescriptionInit
        {
            type = string.Equals(message.Type, "answer", StringComparison.OrdinalIgnoreCase)
                ? RTCSdpType.answer
                : RTCSdpType.offer,
            sdp = message.Sdp ?? string.Empty,
        };
    }

    private static RTCIceCandidateInit ToIceCandidate(WebRtcSignalMessage message)
    {
        return new RTCIceCandidateInit
        {
            candidate = message.Candidate,
            sdpMid = message.SdpMid,
            sdpMLineIndex = message.SdpMLineIndex.HasValue ? (ushort)message.SdpMLineIndex.Value : (ushort)0,
        };
    }
}
