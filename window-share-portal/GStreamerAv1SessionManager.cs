using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

internal sealed class GStreamerAv1SessionManager : IAsyncDisposable
{
    private const string BackendLabel = "gstreamer-av1";
    private readonly object _sync = new();
    private readonly PortalLogStore _logStore;
    private readonly string? _gstLaunchPath;
    private readonly string? _gstRootPath;
    private ActiveSession? _activeSession;

    public GStreamerAv1SessionManager(PortalLogStore logStore)
    {
        _logStore = logStore;
        _gstLaunchPath = ResolveGstLaunchPath();
        _gstRootPath = _gstLaunchPath is null
            ? null
            : Directory.GetParent(Path.GetDirectoryName(_gstLaunchPath)!)?.FullName;

        if (!IsAvailable)
        {
            _logStore.AddWarning("av1", $"AV1 backend {BackendLabel} is unavailable. {AvailabilityMessage}");
        }
    }

    public string BackendName => BackendLabel;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_gstLaunchPath) && !string.IsNullOrWhiteSpace(_gstRootPath);

    public string AvailabilityMessage => IsAvailable
        ? "Available."
        : "GStreamer (gst-launch-1.0.exe) was not found.";

    public async Task<Av1SessionInfo> StartOrUpdateAsync(
        WindowBroker broker,
        long handle,
        int? maxWidth,
        int frameRate,
        StreamTuningMode streamMode,
        CancellationToken cancellationToken)
    {
        EnsureAvailable();

        if (!broker.TryGetWindow(handle, out var window))
        {
            throw new InvalidOperationException($"Window {handle} was not found.");
        }

        var activationError = broker.ActivateWindow(handle);
        if (activationError is not null)
        {
            _logStore.AddWarning("av1", $"Activation request for HWND {handle} returned: {activationError.Message}");
        }

        var targetSize = CalculateTargetSize(window.Bounds, maxWidth);
        var sessionKey = $"{handle}:{targetSize.Width}:{targetSize.Height}:{frameRate}:{streamMode.ToQueryValue()}";

        lock (_sync)
        {
            if (_activeSession is not null &&
                !_activeSession.Process.HasExited &&
                string.Equals(_activeSession.SessionKey, sessionKey, StringComparison.Ordinal))
            {
                return _activeSession.ToInfo();
            }
        }

        await StopAsync();

        var sessionId = Guid.NewGuid().ToString("N");
        var producerName = $"window-share-av1-{sessionId}";
        var signalingPort = ReserveLoopbackPort();
        var process = StartPipelineProcess(handle, targetSize, frameRate, streamMode, producerName, signalingPort);
        var activeSession = new ActiveSession(
            sessionId,
            producerName,
            sessionKey,
            handle,
            targetSize.Width,
            targetSize.Height,
            frameRate,
            streamMode,
            signalingPort,
            process,
            DateTimeOffset.UtcNow);

        lock (_sync)
        {
            _activeSession = activeSession;
        }

        ObserveProcessLifetime(activeSession);
        await WaitForSignalingPortAsync(activeSession, cancellationToken);
        _logStore.AddInformation(
            "av1",
            $"Started {BackendLabel} session {sessionId} for HWND {handle}. Port={signalingPort}, Size={targetSize.Width}x{targetSize.Height}, Fps={frameRate}, Mode={streamMode.ToQueryValue()}.");
        return activeSession.ToInfo();
    }

    public async Task StopAsync(string? sessionId = null)
    {
        ActiveSession? session;
        lock (_sync)
        {
            if (sessionId is not null &&
                _activeSession is not null &&
                !string.Equals(_activeSession.SessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            session = _activeSession;
            _activeSession = null;
        }

        if (session is null)
        {
            return;
        }

        await StopProcessAsync(session);
    }

    public bool TryGetActiveSession(string sessionId, out Av1SessionInfo sessionInfo)
    {
        lock (_sync)
        {
            if (_activeSession is not null &&
                !_activeSession.Process.HasExited &&
                string.Equals(_activeSession.SessionId, sessionId, StringComparison.Ordinal))
            {
                sessionInfo = _activeSession.ToInfo();
                return true;
            }
        }

        sessionInfo = default!;
        return false;
    }

    public async Task ProxySignalingSocketAsync(string sessionId, WebSocket downstreamSocket, CancellationToken cancellationToken)
    {
        if (!TryGetActiveSession(sessionId, out var sessionInfo))
        {
            if (downstreamSocket.State == WebSocketState.Open)
            {
                await downstreamSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unknown or expired AV1 session.", cancellationToken);
            }

            return;
        }

        using var upstreamSocket = new ClientWebSocket();
        upstreamSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await upstreamSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{sessionInfo.SignalingPort}"), cancellationToken);

        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var downstreamToUpstream = RelayWebSocketAsync(downstreamSocket, upstreamSocket, relayCancellation.Token);
        var upstreamToDownstream = RelayWebSocketAsync(upstreamSocket, downstreamSocket, relayCancellation.Token);

        await Task.WhenAny(downstreamToUpstream, upstreamToDownstream);
        relayCancellation.Cancel();

        await Task.WhenAll(
            SuppressSocketErrorsAsync(downstreamToUpstream),
            SuppressSocketErrorsAsync(upstreamToDownstream));

        await CloseSocketIfNeededAsync(upstreamSocket, "proxy complete", CancellationToken.None);
        await CloseSocketIfNeededAsync(downstreamSocket, "proxy complete", CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private Process StartPipelineProcess(
        long handle,
        (int Width, int Height) targetSize,
        int frameRate,
        StreamTuningMode streamMode,
        string producerName,
        int signalingPort)
    {
        var gstLaunchPath = _gstLaunchPath!;
        var gstRootPath = _gstRootPath!;
        var binDirectory = Path.GetDirectoryName(gstLaunchPath)!;
        var pluginScannerPath = Path.Combine(gstRootPath, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe");
        var pluginPath = Path.Combine(gstRootPath, "lib", "gstreamer-1.0");
        var targetBitrate = CalculateTargetBitrateBps(targetSize.Width, targetSize.Height, frameRate, streamMode);

        var startInfo = new ProcessStartInfo
        {
            FileName = gstLaunchPath,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["PATH"] = $"{binDirectory};{Environment.GetEnvironmentVariable("PATH")}";
        startInfo.Environment["GST_PLUGIN_SCANNER"] = pluginScannerPath;
        startInfo.Environment["GST_PLUGIN_SYSTEM_PATH_1_0"] = pluginPath;
        startInfo.Environment["GST_REGISTRY_FORK"] = "no";
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("d3d11screencapturesrc");
        startInfo.ArgumentList.Add("capture-api=wgc");
        startInfo.ArgumentList.Add($"window-handle={handle}");
        startInfo.ArgumentList.Add("window-capture-mode=client");
        startInfo.ArgumentList.Add("show-cursor=false");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("queue");
        startInfo.ArgumentList.Add("max-size-buffers=2");
        startInfo.ArgumentList.Add("leaky=downstream");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("d3d11convert");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("videoconvert");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("videoscale");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("videorate");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add($"video/x-raw,format=I420,width={targetSize.Width},height={targetSize.Height},framerate={frameRate}/1");
        startInfo.ArgumentList.Add("!");
        startInfo.ArgumentList.Add("webrtcsink");
        startInfo.ArgumentList.Add("run-signalling-server=true");
        startInfo.ArgumentList.Add("signalling-server-host=127.0.0.1");
        startInfo.ArgumentList.Add($"signalling-server-port={signalingPort}");
        startInfo.ArgumentList.Add("video-caps=video/x-av1");
        startInfo.ArgumentList.Add($"start-bitrate={targetBitrate}");
        startInfo.ArgumentList.Add($"max-bitrate={Math.Max(targetBitrate, targetBitrate + targetBitrate / 4)}");
        startInfo.ArgumentList.Add($"meta=meta,name={producerName}");

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                _logStore.AddInformation("gstreamer", eventArgs.Data.Trim());
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                _logStore.AddWarning("gstreamer", eventArgs.Data.Trim());
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start GStreamer AV1 pipeline.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void ObserveProcessLifetime(ActiveSession session)
    {
        _ = session.Process.WaitForExitAsync().ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                var exitCode = -1;
                try
                {
                    exitCode = session.Process.ExitCode;
                }
                catch
                {
                }

                lock (_sync)
                {
                    if (_activeSession?.SessionId == session.SessionId)
                    {
                        _activeSession = null;
                    }
                }

                _logStore.AddWarning(
                    "av1",
                    $"GStreamer AV1 session {session.SessionId} exited. ExitCode={exitCode}, HWND={session.WindowHandle}.");
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForSignalingPortAsync(ActiveSession session, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.Process.HasExited)
            {
                throw new InvalidOperationException($"GStreamer AV1 pipeline exited before signaling server was ready. ExitCode={session.Process.ExitCode}.");
            }

            try
            {
                using var client = new TcpClient();
                using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
                await client.ConnectAsync("127.0.0.1", session.SignalingPort, connectCancellation.Token);
                return;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                await Task.Delay(150, cancellationToken);
            }
        }

        throw new TimeoutException($"Timed out waiting for GStreamer AV1 signaling server on port {session.SignalingPort}.");
    }

    private async Task StopProcessAsync(ActiveSession session)
    {
        try
        {
            if (session.Process.HasExited)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        _logStore.AddInformation("av1", $"Stopping GStreamer AV1 session {session.SessionId} for HWND {session.WindowHandle}.");

        try
        {
            session.Process.Kill(entireProcessTree: true);
            await session.Process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            _logStore.AddWarning("av1", $"Failed to stop GStreamer AV1 session {session.SessionId}: {ex.Message}");
        }
        finally
        {
            session.Process.Dispose();
        }
    }

    private static async Task RelayWebSocketAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested &&
               source.State is WebSocketState.Open or WebSocketState.CloseReceived &&
               destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (destination.State == WebSocketState.Open)
                {
                    await destination.CloseAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        cancellationToken);
                }

                return;
            }

            await destination.SendAsync(
                new ArraySegment<byte>(buffer, 0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                cancellationToken);
        }
    }

    private static async Task SuppressSocketErrorsAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (WebRtcTaskUtilities.IsExpectedSocketShutdown(ex))
        {
        }
    }

    private static async Task CloseSocketIfNeededAsync(WebSocket socket, string reason, CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken);
            }
        }
        catch (Exception ex) when (WebRtcTaskUtilities.IsExpectedSocketShutdown(ex))
        {
        }
    }

    private static (int Width, int Height) CalculateTargetSize(WindowBounds bounds, int? maxWidth)
    {
        var sourceWidth = Math.Max(2, bounds.Width);
        var sourceHeight = Math.Max(2, bounds.Height);
        var width = Math.Max(2, maxWidth.GetValueOrDefault(sourceWidth));
        if (width > sourceWidth)
        {
            width = sourceWidth;
        }

        var height = Math.Max(2, (int)Math.Round(width * (sourceHeight / (double)sourceWidth)));
        return (MakeEven(width), MakeEven(height));
    }

    private static int CalculateTargetBitrateBps(int width, int height, int frameRate, StreamTuningMode mode)
    {
        var rawEstimate = (int)Math.Round(width * height * Math.Max(frameRate, 1) / 6000d);
        var scaled = mode switch
        {
            StreamTuningMode.LowLatency => rawEstimate,
            StreamTuningMode.HighQuality => (int)Math.Round(rawEstimate * 1.35),
            _ => (int)Math.Round(rawEstimate * 1.15),
        };

        var kbps = Math.Clamp(scaled, 1_200, 14_000);
        return kbps * 1000;
    }

    private static int MakeEven(int value)
    {
        var even = Math.Max(2, value);
        return even % 2 == 0 ? even : even - 1;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string? ResolveGstLaunchPath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("WINDOW_SHARE_PORTAL_GST_LAUNCH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gstreamer", "1.0", "msvc_x86_64", "bin", "gst-launch-1.0.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GStreamer", "1.0", "msvc_x86_64", "bin", "gst-launch-1.0.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(AvailabilityMessage);
        }
    }

    private sealed record ActiveSession(
        string SessionId,
        string ProducerName,
        string SessionKey,
        long WindowHandle,
        int Width,
        int Height,
        int FrameRate,
        StreamTuningMode StreamMode,
        int SignalingPort,
        Process Process,
        DateTimeOffset StartedAt)
    {
        public Av1SessionInfo ToInfo() =>
            new(
                SessionId,
                ProducerName,
                BackendLabel,
                WindowHandle,
                Width,
                Height,
                FrameRate,
                StreamMode.ToQueryValue(),
                SignalingPort);
    }
}
