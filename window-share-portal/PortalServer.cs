using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;

internal sealed class PortalServer : IAsyncDisposable
{
    private readonly string[] _args;
    private readonly string _contentRoot;
    private readonly PortalRuntimeState _runtimeState;
    private readonly ClientConnectionTracker _connectionTracker;
    private readonly PortalLogStore _logStore;
    private readonly PortalSessionStore _sessionStore;
    private readonly IWebRtcStreamSessionFactory _webRtcStreamSessionFactory;
    private WebApplication? _app;

    public PortalServer(
        string[] args,
        string contentRoot,
        PortalRuntimeState runtimeState,
        ClientConnectionTracker connectionTracker,
        PortalLogStore logStore,
        PortalSessionStore sessionStore,
        IWebRtcStreamSessionFactory webRtcStreamSessionFactory)
    {
        _args = args;
        _contentRoot = contentRoot;
        _runtimeState = runtimeState;
        _connectionTracker = connectionTracker;
        _logStore = logStore;
        _sessionStore = sessionStore;
        _webRtcStreamSessionFactory = webRtcStreamSessionFactory;
    }

    public bool IsRunning => _app is not null;

    public string WebRtcBackendName => _webRtcStreamSessionFactory.BackendName;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = _args,
            ContentRootPath = _contentRoot,
            WebRootPath = Path.Combine(_contentRoot, "wwwroot"),
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddConsole();
        builder.Logging.AddProvider(new PortalLogLoggerProvider(_logStore));
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        var accessPolicy = _runtimeState.CurrentAccessPolicy;
        builder.WebHost.ConfigureKestrel(options =>
        {
            foreach (var address in accessPolicy.BindAddresses)
            {
                options.Listen(address, accessPolicy.Port);
            }
        });

        builder.Services.AddSingleton(_runtimeState);
        builder.Services.AddSingleton(accessPolicy);
        builder.Services.AddSingleton(_connectionTracker);
        builder.Services.AddSingleton(_logStore);
        builder.Services.AddSingleton(_sessionStore);
        builder.Services.AddSingleton<WindowBroker>();

        var app = builder.Build();
        ConfigurePipeline(app, accessPolicy);
        await app.StartAsync(cancellationToken);
        _app = app;
        _logStore.AddInformation("server", $"Server started on {string.Join(", ", _runtimeState.ListenUrls)}");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync(cancellationToken);
        }
        finally
        {
            await _app.DisposeAsync();
            _app = null;
            _logStore.AddInformation("server", "Server stopped.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    public async Task DisconnectClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var target = _connectionTracker.TryCreateDisconnectTarget(clientId) ??
            throw new InvalidOperationException("Selected client was not found.");

        foreach (var sessionId in target.SessionIds)
        {
            _sessionStore.Remove(sessionId);
        }

        await target.DisconnectAsync(cancellationToken);
        _logStore.AddInformation(
            "server",
            $"Disconnected client {target.EnvironmentLabel} ({target.RemoteAddress}). Sessions={target.SessionIds.Count}, realtime={target.RealtimeConnectionCount}.");
    }

    private void ConfigurePipeline(WebApplication app, NetworkAccessPolicy accessPolicy)
    {
        app.Use(async (context, next) =>
        {
            if (!accessPolicy.IsAllowed(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = "Access denied. This server only accepts loopback, local-network, and VPN-network clients.",
                    });
                }
                else
                {
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync("Access denied. This server only accepts loopback, local-network, and VPN-network clients.");
                }

                _connectionTracker.TrackRequest(context);
                return;
            }

            await next();
            _connectionTracker.TrackRequest(context);
        });

        app.Use(async (context, next) =>
        {
            if (IsRequestBodyTooLarge(context, out var message))
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(new { message });
                return;
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseWebSockets();

        app.MapGet("/", () => Results.Redirect("/index.html"));

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api", out var remaining))
            {
                await next();
                return;
            }

            if (remaining.Equals("/login", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (IsAuthorized(context))
            {
                await next();
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });

        app.MapPost("/api/login", (LoginRequest request, HttpContext context, HttpResponse response) =>
        {
            if (!FixedTimeEquals(request.Token, _runtimeState.Token))
            {
                return Results.Unauthorized();
            }

            var sessionId = _sessionStore.CreateSession(_runtimeState.Token);
            response.Cookies.Append(PortalSecurityLimits.SessionCookieName, sessionId, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                MaxAge = PortalSecurityLimits.SessionLifetime,
                Path = "/",
            });

            return Results.Ok(new
            {
                ok = true,
                expiresInMinutes = (int)PortalSecurityLimits.SessionLifetime.TotalMinutes,
            });
        });

        app.MapPost("/api/logout", (HttpContext context, HttpResponse response) =>
        {
            if (context.Request.Cookies.TryGetValue(PortalSecurityLimits.SessionCookieName, out var sessionId))
            {
                _sessionStore.Remove(sessionId);
            }

            response.Cookies.Delete(PortalSecurityLimits.SessionCookieName);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/client-log", (ClientLogRequest request, HttpContext context, PortalLogStore logStore) =>
        {
            if (TryValidateClientLogRequest(context, request, out var validationMessage))
            {
                return Results.Json(new { message = validationMessage }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var level = ParseClientLogLevel(request.Level);
            var source = string.IsNullOrWhiteSpace(request.Source) ? "browser" : request.Source.Trim();
            var message = string.IsNullOrWhiteSpace(request.Message) ? "(empty client log)" : request.Message.Trim();
            if (request.Context is JsonElement requestContext && requestContext.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                message = $"{message} | context={requestContext.GetRawText()}";
            }

            logStore.Add(level, source, message);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/server-info", () => Results.Ok(new
        {
            machineName = Environment.MachineName,
            listenUrl = _runtimeState.ListenUrl,
            listenUrls = _runtimeState.ListenUrls,
            allowedAddresses = _runtimeState.AllowedAddressLabels,
            allowedNetworks = _runtimeState.AllowedNetworkLabels,
            videoCodecOptions = BuildVideoCodecOptions(),
            tokenModeLabel = _runtimeState.TokenModeLabel,
            hasCustomToken = !string.Equals(_runtimeState.TokenModeLabel, "Generated at first launch", StringComparison.Ordinal),
            limitations = new[]
            {
                "Minimized windows often cannot be captured. Restore them first.",
                "Input injection can be blocked by Windows when the target app runs elevated or as a protected UI.",
                "Some GPU-accelerated windows may return incomplete frames through PrintWindow.",
            },
            webRtcBackend = _webRtcStreamSessionFactory.BackendName,
        }));

        app.MapGet("/api/windows", (WindowBroker broker) => Results.Ok(new
        {
            windows = broker.ListWindows(),
        }));

        app.MapGet("/api/windows/{handle:long}", (long handle, WindowBroker broker) =>
        {
            return broker.TryGetWindow(handle, out var window)
                ? Results.Ok(window)
                : Results.NotFound(new { message = "Window not found." });
        });

        app.MapGet("/api/windows/{handle:long}/frame", (long handle, int? maxWidth, int? quality, string? format, HttpContext context, WindowBroker broker) =>
        {
            if (!broker.TryCaptureWindow(handle, maxWidth, quality, format, out var frame, out var errorMessage, out var statusCode))
            {
                return Results.Json(new { message = errorMessage }, statusCode: statusCode);
            }

            context.Response.Headers.CacheControl = "no-store, no-cache";
            context.Response.Headers.Append("X-Window-Handle", frame.Window.Handle.ToString());
            context.Response.Headers.Append("X-Window-Width", frame.Width.ToString());
            context.Response.Headers.Append("X-Window-Height", frame.Height.ToString());
            return Results.File(frame.ImageBytes, frame.ContentType);
        });

        app.MapPost("/api/windows/{handle:long}/resize", (long handle, ResizeWindowRequest request, WindowBroker broker) =>
        {
            if (request.Width < 200 || request.Height < 200 || request.Width > 7680 || request.Height > 4320)
            {
                return Results.Json(new { message = "Width and height must be between 200 and 7680/4320." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = broker.ResizeWindow(handle, request.Width, request.Height);
            if (result.Error is not null)
            {
                return Results.Json(new { message = result.Error.Message }, statusCode: result.Error.StatusCode);
            }

            return Results.Ok(new
            {
                ok = true,
                previousBounds = new
                {
                    left = result.PreviousBounds.Left,
                    top = result.PreviousBounds.Top,
                    width = result.PreviousBounds.Width,
                    height = result.PreviousBounds.Height,
                },
                appliedBounds = new
                {
                    left = result.AppliedBounds.Left,
                    top = result.AppliedBounds.Top,
                    width = result.AppliedBounds.Width,
                    height = result.AppliedBounds.Height,
                },
            });
        });

        app.MapPost("/api/windows/{handle:long}/activate", (long handle, WindowBroker broker) =>
        {
            var error = broker.ActivateWindow(handle);
            return error is null
                ? Results.Ok(new { ok = true })
                : Results.Json(new { message = error.Message }, statusCode: error.StatusCode);
        });

        app.MapPost("/api/windows/{handle:long}/input/click", (long handle, ClickInputRequest request, WindowBroker broker) =>
        {
            var error = broker.ClickWindow(handle, request);
            return error is null
                ? Results.Ok(new { ok = true })
                : Results.Json(new { message = error.Message }, statusCode: error.StatusCode);
        });

        app.MapPost("/api/windows/{handle:long}/input/pointer", (long handle, PointerInputRequest request, WindowBroker broker) =>
        {
            var error = broker.HandlePointerInput(handle, request);
            return error is null
                ? Results.Ok(new { ok = true })
                : Results.Json(new { message = error.Message }, statusCode: error.StatusCode);
        });

        app.MapPost("/api/windows/{handle:long}/input/text", (long handle, TextInputRequest request, WindowBroker broker) =>
        {
            var error = broker.TypeText(handle, request);
            return error is null
                ? Results.Ok(new { ok = true })
                : Results.Json(new { message = error.Message }, statusCode: error.StatusCode);
        });

        app.MapPost("/api/windows/{handle:long}/input/key", (long handle, KeyInputRequest request, WindowBroker broker) =>
        {
            var error = broker.SendKey(handle, request);
            return error is null
                ? Results.Ok(new { ok = true })
                : Results.Json(new { message = error.Message }, statusCode: error.StatusCode);
        });

        app.MapPost("/api/launch", (LaunchAppRequest request, PortalLogStore logStore) =>
        {
            var allowed = new Dictionary<string, (string FileName, string? Arguments)>(StringComparer.OrdinalIgnoreCase)
            {
                ["explorer"] = ("explorer.exe", null),
                ["cmd"] = ("cmd.exe", null),
            };

            if (!allowed.TryGetValue(request.App ?? "", out var entry))
            {
                return Results.Json(new { message = $"Unknown app: {request.App}" }, statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = entry.FileName,
                    Arguments = entry.Arguments ?? "",
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(startInfo);
                logStore.AddInformation("launch", $"Launched {entry.FileName}.");
                return Results.Ok(new { ok = true, app = request.App });
            }
            catch (Exception ex)
            {
                logStore.AddInformation("launch", $"Failed to launch {entry.FileName}: {ex.Message}");
                return Results.Json(new { message = $"Failed to launch {entry.FileName}: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.Map("/ws/webrtc", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket upgrade required.");
                return;
            }

            if (!IsAuthorized(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var handle = ParseQueryInt64(context.Request.Query["handle"]);
            if (handle is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Missing or invalid handle.");
                return;
            }

            var frameRate = ParseQueryInt32(context.Request.Query["frameRate"]) ?? 30;
            var maxWidth = ParseQueryInt32(context.Request.Query["maxWidth"]);
            var streamMode = StreamTuningModeParser.Parse(context.Request.Query["mode"]);
            var requestedVideoCodecPreference = WebRtcVideoCodecPreferenceParser.Parse(context.Request.Query["codec"]);
            var videoCodecPreference = _webRtcStreamSessionFactory.NormalizeRequestedVideoCodecPreference(requestedVideoCodecPreference);
            if (maxWidth is <= 0)
            {
                maxWidth = null;
            }

            if (videoCodecPreference != requestedVideoCodecPreference)
            {
                _logStore.AddWarning(
                    "webrtc",
                    $"Requested codec {requestedVideoCodecPreference.ToQueryValue()} is not supported by backend {_webRtcStreamSessionFactory.BackendName}. Falling back to {videoCodecPreference.ToQueryValue()}.");
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var realtimeRegistration = _connectionTracker.RegisterRealtimeConnection(context, async disconnectCancellationToken =>
            {
                if (socket.State == System.Net.WebSockets.WebSocketState.Open ||
                    socket.State == System.Net.WebSockets.WebSocketState.CloseReceived)
                {
                    try
                    {
                        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "disconnected by app", disconnectCancellationToken);
                        return;
                    }
                    catch
                    {
                    }
                }

                if (socket.State != System.Net.WebSockets.WebSocketState.Closed &&
                    socket.State != System.Net.WebSockets.WebSocketState.Aborted)
                {
                    socket.Abort();
                }
            });
            var logger = context.RequestServices.GetRequiredService<ILogger<WebRtcWindowStreamSession>>();
            _logStore.AddInformation("webrtc", $"Accepted signaling socket for HWND {handle.Value}, {frameRate} fps, maxWidth={maxWidth?.ToString() ?? "auto"}, mode={streamMode.ToQueryValue()}, codec={videoCodecPreference.ToQueryValue()}, backend={_webRtcStreamSessionFactory.BackendName}.");
            var sessionOptions = new WebRtcStreamSessionOptions(handle.Value, maxWidth, frameRate, streamMode, videoCodecPreference, socket);
            await using var session = _webRtcStreamSessionFactory.Create(sessionOptions, context.RequestServices);

            try
            {
                await session.RunAsync(context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WebRTC session failed for HWND {Handle}.", handle.Value);
                if (socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    try
                    {
                        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.InternalServerError, "session failed", CancellationToken.None);
                    }
                    catch
                    {
                    }
                }
            }
        });

    }

    private IReadOnlyList<WebRtcVideoCodecOption> BuildVideoCodecOptions()
    {
        return _webRtcStreamSessionFactory.SupportedVideoCodecOptions;
    }

    private bool IsAuthorized(HttpContext context)
    {
        context.Request.Cookies.TryGetValue(PortalSecurityLimits.SessionCookieName, out var sessionId);
        return _sessionStore.TryValidateAndTouch(sessionId, _runtimeState.Token);
    }

    private static bool FixedTimeEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }

    private static long? ParseQueryInt64(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static int? ParseQueryInt32(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static LogLevel ParseClientLogLevel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "warn" => LogLevel.Warning,
            "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ => LogLevel.Information,
        };
    }

    private static bool TryValidateClientLogRequest(HttpContext context, ClientLogRequest request, out string message)
    {
        if (context.Request.ContentLength is > PortalSecurityLimits.MaxClientLogBodyBytes)
        {
            message = $"Client log payloads must be {PortalSecurityLimits.MaxClientLogBodyBytes} bytes or smaller.";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.Source) && request.Source.Trim().Length > PortalSecurityLimits.MaxClientLogSourceChars)
        {
            message = $"Client log source must be {PortalSecurityLimits.MaxClientLogSourceChars} characters or shorter.";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.Message) && request.Message.Trim().Length > PortalSecurityLimits.MaxClientLogMessageChars)
        {
            message = $"Client log message must be {PortalSecurityLimits.MaxClientLogMessageChars} characters or shorter.";
            return true;
        }

        if (request.Context is JsonElement element &&
            element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined &&
            element.GetRawText().Length > PortalSecurityLimits.MaxClientLogContextChars)
        {
            message = $"Client log context must be {PortalSecurityLimits.MaxClientLogContextChars} characters or shorter.";
            return true;
        }

        message = string.Empty;
        return false;
    }

    private static bool IsRequestBodyTooLarge(HttpContext context, out string message)
    {
        if (context.Request.ContentLength is null)
        {
            message = string.Empty;
            return false;
        }

        if (context.Request.Path.Equals("/api/client-log", StringComparison.OrdinalIgnoreCase) &&
            context.Request.ContentLength.Value > PortalSecurityLimits.MaxClientLogBodyBytes)
        {
            message = $"Client log payloads must be {PortalSecurityLimits.MaxClientLogBodyBytes} bytes or smaller.";
            return true;
        }

        if (context.Request.Path.StartsWithSegments("/api/windows", out var remaining) &&
            remaining.Value?.EndsWith("/input/text", StringComparison.OrdinalIgnoreCase) == true &&
            context.Request.ContentLength.Value > PortalSecurityLimits.MaxTextInputBodyBytes)
        {
            message = $"Text input payloads must be {PortalSecurityLimits.MaxTextInputBodyBytes} bytes or smaller.";
            return true;
        }

        message = string.Empty;
        return false;
    }
}
