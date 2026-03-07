using System.Drawing;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.MixedReality.WebRTC;

internal sealed class MixedRealityWebRtcStreamSession : IWebRtcStreamSession
{
    private const int MaxSignalMessageBytes = 128 * 1024;
    private static readonly JsonSerializerOptions SignalJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WindowBroker _broker;
    private readonly StreamTuningMode _streamMode;
    private readonly WebRtcVideoCodecPreference _requestedVideoCodecPreference;
    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _socketSendLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly Queue<string> _pendingLocalIceCandidatePayloads = new();
    private readonly object _signalStateLock = new();
    private readonly object _captureStateLock = new();

    private PeerConnection? _peerConnection;
    private ExternalVideoTrackSource? _videoSource;
    private LocalVideoTrack? _localVideoTrack;
    private bool _disposed;
    private bool _localDescriptionSent;
    private long _currentWindowHandle;
    private int? _currentMaxWidth;
    private int _lastFrameWidth = 2;
    private int _lastFrameHeight = 2;
    private int _captureFailureCount;
    private bool _firstFrameLogged;

    public MixedRealityWebRtcStreamSession(
        WindowBroker broker,
        long windowHandle,
        int? maxWidth,
        int frameRate,
        StreamTuningMode streamMode,
        WebRtcVideoCodecPreference requestedVideoCodecPreference,
        WebSocket socket,
        ILogger logger)
    {
        _broker = broker;
        _currentWindowHandle = windowHandle;
        _currentMaxWidth = maxWidth;
        _streamMode = streamMode;
        _requestedVideoCodecPreference = requestedVideoCodecPreference;
        _socket = socket;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("libwebrtc session loop started for HWND {Handle}.", _currentWindowHandle);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCts.Token);
        try
        {
            await InitializePeerConnectionAsync(linkedCts.Token);
            await ReceiveLoopAsync(linkedCts.Token);
        }
        finally
        {
            linkedCts.Cancel();
            _sessionCts.Cancel();
            await DisposeAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _sessionCts.Cancel();
        _logger.LogInformation("Disposing libwebrtc session for HWND {Handle}.", _currentWindowHandle);

        try
        {
            _peerConnection?.Close();
        }
        catch
        {
        }

        _localVideoTrack?.Dispose();
        _localVideoTrack = null;
        _videoSource?.Dispose();
        _videoSource = null;
        _peerConnection?.Dispose();
        _peerConnection = null;
        _socketSendLock.Dispose();
        _sessionCts.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task InitializePeerConnectionAsync(CancellationToken cancellationToken)
    {
        var peerConnection = new PeerConnection
        {
            Name = $"window-share-{_currentWindowHandle}",
            PreferredVideoCodec = MapPreferredVideoCodec(_requestedVideoCodecPreference),
        };

        peerConnection.LocalSdpReadytoSend += HandleLocalSdpReadyToSend;
        peerConnection.IceCandidateReadytoSend += HandleLocalIceCandidateReadyToSend;
        peerConnection.IceStateChanged += HandleIceStateChanged;
        peerConnection.IceGatheringStateChanged += HandleIceGatheringStateChanged;
        peerConnection.Connected += HandleConnected;
        peerConnection.RenegotiationNeeded += HandleRenegotiationNeeded;

        var configuration = new PeerConnectionConfiguration
        {
            BundlePolicy = BundlePolicy.MaxBundle,
            SdpSemantic = SdpSemantic.UnifiedPlan,
            IceTransportType = IceTransportType.All,
        };

        await peerConnection.InitializeAsync(configuration, cancellationToken);
        var videoSource = ExternalVideoTrackSource.CreateFromArgb32Callback((in FrameRequest request) => HandleArgb32FrameRequest(request));
        var localVideoTrack = LocalVideoTrack.CreateFromSource(videoSource, new LocalVideoTrackInitConfig
        {
            trackName = "window-share",
        });

        _peerConnection = peerConnection;
        _videoSource = videoSource;
        _localVideoTrack = localVideoTrack;

        var codecLabel = string.IsNullOrWhiteSpace(peerConnection.PreferredVideoCodec) ? "auto" : peerConnection.PreferredVideoCodec;
        _logger.LogInformation("Initialized libwebrtc backend for HWND {Handle}. PreferredVideoCodec={Codec}.", _currentWindowHandle, codecLabel);
        if (_requestedVideoCodecPreference == WebRtcVideoCodecPreference.AV1)
        {
            _logger.LogWarning("AV1 was requested for HWND {Handle}, but the selected libwebrtc wrapper does not expose AV1 selection. Falling back to automatic codec selection.", _currentWindowHandle);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
            }
            catch (Exception ex) when (WebRtcTaskUtilities.IsExpectedSocketShutdown(ex))
            {
                _logger.LogInformation("Signaling socket receive loop ended for HWND {Handle}.", _currentWindowHandle);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogInformation("Signaling socket closed by client for HWND {Handle}.", _currentWindowHandle);
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
            _logger.LogInformation("Received signaling message for HWND {Handle}: {Payload}", _currentWindowHandle, json);
            await HandleSignalMessageAsync(json, cancellationToken);
        }
    }

    private async Task HandleSignalMessageAsync(string json, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<WebRtcSignalMessage>(json, SignalJsonOptions);
        if (message is null)
        {
            _logger.LogWarning("Ignored empty signaling payload for HWND {Handle}.", _currentWindowHandle);
            return;
        }

        if (string.Equals(message.Type, "switch-window", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateCaptureTargetAsync(message.Handle, message.MaxWidth, cancellationToken);
            return;
        }

        if (string.Equals(message.Type, "update-stream", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateCaptureTargetAsync(null, message.MaxWidth, cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Candidate))
        {
            _logger.LogInformation("Applying remote ICE candidate for HWND {Handle}. mid={SdpMid}, mline={MLine}", _currentWindowHandle, message.SdpMid, message.SdpMLineIndex);
            _peerConnection?.AddIceCandidate(new IceCandidate
            {
                SdpMid = message.SdpMid ?? "0",
                SdpMlineIndex = message.SdpMLineIndex ?? 0,
                Content = message.Candidate,
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Sdp))
        {
            _logger.LogWarning("Signaling payload had no SDP and no candidate for HWND {Handle}. type={Type}", _currentWindowHandle, message.Type);
            return;
        }

        if (!string.Equals(message.Type, "offer", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Ignoring signaling type {Type} for HWND {Handle}.", message.Type, _currentWindowHandle);
            return;
        }

        _logger.LogInformation("Applying remote offer for HWND {Handle}. SDP length={Length}.", _currentWindowHandle, message.Sdp.Length);
        await _peerConnection!.SetRemoteDescriptionAsync(new SdpMessage
        {
            Type = SdpMessageType.Offer,
            Content = message.Sdp,
        });

        var videoTransceiver = _peerConnection.Transceivers.LastOrDefault(transceiver => transceiver.MediaKind == MediaKind.Video);
        if (videoTransceiver is null)
        {
            _logger.LogWarning("Remote offer for HWND {Handle} did not create a video transceiver. Creating a local fallback transceiver.", _currentWindowHandle);
            videoTransceiver = _peerConnection.AddTransceiver(MediaKind.Video, new TransceiverInitSettings
            {
                Name = "window-share-video",
                InitialDesiredDirection = Transceiver.Direction.SendOnly,
                StreamIDs = new List<string> { "window-share" },
            });
        }

        if (_localVideoTrack is not null)
        {
            videoTransceiver.LocalVideoTrack = _localVideoTrack;
        }
        videoTransceiver.DesiredDirection = Transceiver.Direction.SendOnly;
        _logger.LogInformation(
            "Attached local video track to transceiver for HWND {Handle}. Mline={Mline}, DesiredDirection={Direction}, LocalTrack={HasLocalTrack}.",
            _currentWindowHandle,
            videoTransceiver.MlineIndex,
            videoTransceiver.DesiredDirection,
            videoTransceiver.LocalVideoTrack is not null);

        var activationError = _broker.ActivateWindow(_currentWindowHandle);
        if (activationError is null)
        {
            _logger.LogInformation("Activated HWND {Handle} for capture.", _currentWindowHandle);
        }
        else
        {
            _logger.LogWarning("Failed to activate HWND {Handle} before capture. {Message}", _currentWindowHandle, activationError.Message);
        }

        if (!_peerConnection.CreateAnswer())
        {
            throw new InvalidOperationException("libwebrtc failed to start SDP answer creation.");
        }
    }

    private Task UpdateCaptureTargetAsync(long? requestedHandle, int? requestedMaxWidth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedMaxWidth = requestedMaxWidth is > 0 ? requestedMaxWidth : null;
        var targetHandle = requestedHandle ?? _currentWindowHandle;
        if (!_broker.TryGetWindow(targetHandle, out _))
        {
            _logger.LogWarning("Ignored capture target switch to missing HWND {Handle}.", targetHandle);
            return Task.CompletedTask;
        }

        var handleChanged = false;
        var widthChanged = false;
        lock (_captureStateLock)
        {
            handleChanged = targetHandle != _currentWindowHandle;
            widthChanged = normalizedMaxWidth != _currentMaxWidth;
            if (!handleChanged && !widthChanged)
            {
                return Task.CompletedTask;
            }

            _currentWindowHandle = targetHandle;
            _currentMaxWidth = normalizedMaxWidth;
            if (handleChanged)
            {
                _captureFailureCount = 0;
                _firstFrameLogged = false;
            }
        }

        if (handleChanged)
        {
            var activationError = _broker.ActivateWindow(targetHandle);
            if (activationError is null)
            {
                _logger.LogInformation("Switched capture target to HWND {Handle} without reconnecting.", targetHandle);
            }
            else
            {
                _logger.LogWarning("Switched capture target to HWND {Handle}, but activation failed. {Message}", targetHandle, activationError.Message);
            }
        }

        if (widthChanged)
        {
            _logger.LogInformation("Updated active stream width for HWND {Handle} to {MaxWidth}.", _currentWindowHandle, normalizedMaxWidth?.ToString() ?? "auto");
        }

        return Task.CompletedTask;
    }

    private void HandleArgb32FrameRequest(in FrameRequest request)
    {
        try
        {
            if (!TryCompleteFrameRequestWithCapture(request, out var message) && !string.IsNullOrWhiteSpace(message))
            {
                _captureFailureCount++;
                if (_captureFailureCount == 1 || _captureFailureCount % 30 == 0)
                {
                    _logger.LogWarning("libwebrtc frame capture fallback for HWND {Handle}. Reason={Reason}", _currentWindowHandle, message);
                }
            }
            else
            {
                _captureFailureCount = 0;
            }
        }
        catch (Exception ex)
        {
            _captureFailureCount++;
            if (_captureFailureCount == 1 || _captureFailureCount % 30 == 0)
            {
                _logger.LogWarning(ex, "libwebrtc frame callback failed for HWND {Handle}.", _currentWindowHandle);
            }
            CompleteFrameRequestWithBlackFrame(request, _lastFrameWidth, _lastFrameHeight);
        }
    }

    private bool TryCompleteFrameRequestWithCapture(in FrameRequest request, out string message)
    {
        message = string.Empty;
        long handle;
        int? maxWidth;
        lock (_captureStateLock)
        {
            handle = _currentWindowHandle;
            maxWidth = _currentMaxWidth;
        }

        if (!_broker.TryCaptureWindowBitmap(handle, maxWidth, out var bitmap, out _, out var captureMessage, out var statusCode, preferScreenCaptureOnly: true, lowLatencyScaling: _streamMode == StreamTuningMode.LowLatency))
        {
            message = $"Bitmap capture: {statusCode} {captureMessage}";
            CompleteFrameRequestWithBlackFrame(request, _lastFrameWidth, _lastFrameHeight);
            return false;
        }

        using (bitmap)
        {
            _lastFrameWidth = Math.Max(bitmap.Width, 2);
            _lastFrameHeight = Math.Max(bitmap.Height, 2);
            var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bitmapData = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var frame = new Argb32VideoFrame
                {
                    width = (uint)bitmap.Width,
                    height = (uint)bitmap.Height,
                    data = bitmapData.Scan0,
                    stride = bitmapData.Stride,
                };
                request.Source.CompleteFrameRequest(request.RequestId, request.TimestampMs, in frame);
                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    _logger.LogInformation("First libwebrtc frame submitted for HWND {Handle}. Size={Width}x{Height}.", handle, bitmap.Width, bitmap.Height);
                }

                return true;
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }
    }

    private static void CompleteFrameRequestWithBlackFrame(in FrameRequest request, int width, int height)
    {
        var safeWidth = Math.Max(width, 2);
        var safeHeight = Math.Max(height, 2);
        var buffer = new byte[safeWidth * safeHeight * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var frame = new Argb32VideoFrame
            {
                width = (uint)safeWidth,
                height = (uint)safeHeight,
                data = handle.AddrOfPinnedObject(),
                stride = safeWidth * 4,
            };
            request.Source.CompleteFrameRequest(request.RequestId, request.TimestampMs, in frame);
        }
        finally
        {
            handle.Free();
        }
    }

    private void HandleLocalSdpReadyToSend(SdpMessage message)
    {
        WebRtcTaskUtilities.StartObservedBackgroundTask(async () =>
        {
            try
            {
                await SendSignalAsync(new
                {
                    type = message.Type == SdpMessageType.Answer ? "answer" : "offer",
                    sdp = message.Content,
                }, _sessionCts.Token);
                FlushQueuedLocalIceCandidates(_sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send local SDP for HWND {Handle}.", _currentWindowHandle);
            }
        }, _logger, "sending local SDP");
    }

    private void HandleLocalIceCandidateReadyToSend(IceCandidate candidate)
    {
        var payload = JsonSerializer.Serialize(new
        {
            candidate = candidate.Content,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = candidate.SdpMlineIndex,
        });

        lock (_signalStateLock)
        {
            if (!_localDescriptionSent)
            {
                _pendingLocalIceCandidatePayloads.Enqueue(payload);
                _logger.LogInformation("Queued local ICE candidate until answer is sent for HWND {Handle}.", _currentWindowHandle);
                return;
            }
        }

        WebRtcTaskUtilities.StartObservedBackgroundTask(async () =>
        {
            try
            {
                _logger.LogInformation("Sending local ICE candidate for HWND {Handle}.", _currentWindowHandle);
                await SendRawTextAsync(payload, _sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send local ICE candidate.");
            }
        }, _logger, "sending local ICE candidate");
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

        WebRtcTaskUtilities.StartObservedBackgroundTask(async () =>
        {
            foreach (var payload in pendingPayloads)
            {
                try
                {
                    _logger.LogInformation("Sending queued local ICE candidate for HWND {Handle}.", _currentWindowHandle);
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
        }, _logger, "flushing queued local ICE candidates");
    }

    private void HandleIceStateChanged(IceConnectionState state)
    {
        _logger.LogInformation("libwebrtc ICE state changed to {State} for HWND {Handle}.", state, _currentWindowHandle);
        if (state is IceConnectionState.Failed or IceConnectionState.Disconnected or IceConnectionState.Closed)
        {
            try
            {
                _peerConnection?.Close();
            }
            catch
            {
            }

            if (state is IceConnectionState.Failed or IceConnectionState.Closed)
            {
                _sessionCts.Cancel();
            }
        }
    }

    private void HandleIceGatheringStateChanged(IceGatheringState state)
    {
        _logger.LogDebug("libwebrtc ICE gathering state changed to {State} for HWND {Handle}.", state, _currentWindowHandle);
    }

    private void HandleConnected()
    {
        _logger.LogInformation("libwebrtc peer connected for HWND {Handle}.", _currentWindowHandle);
    }

    private void HandleRenegotiationNeeded()
    {
        _logger.LogInformation("libwebrtc renegotiation requested for HWND {Handle}.", _currentWindowHandle);
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
        try
        {
            await _socketSendLock.WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (WebRtcTaskUtilities.IsExpectedSocketShutdown(ex))
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                try
                {
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                }
                catch (Exception ex) when (WebRtcTaskUtilities.IsExpectedSocketShutdown(ex))
                {
                }
            }
        }
        finally
        {
            _socketSendLock.Release();
        }
    }

    private static string MapPreferredVideoCodec(WebRtcVideoCodecPreference requestedPreference)
    {
        return requestedPreference switch
        {
            WebRtcVideoCodecPreference.VP8 => "VP8",
            WebRtcVideoCodecPreference.VP9 => "VP9",
            _ => string.Empty,
        };
    }
}
