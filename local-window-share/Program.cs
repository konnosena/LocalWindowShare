internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var contentRoot = ResolveContentRoot(AppContext.BaseDirectory);
        var settingsStore = new PortalSettingsStore();
        var listenPort = ResolveListenPort();
        var settings = settingsStore.Load(Environment.GetEnvironmentVariable("WINDOW_SHARE_PORTAL_TOKEN"), listenPort);
        var runtimeState = new PortalRuntimeState(settings, settingsStore);
        var approvalService = new ClientApprovalService(settingsStore, settings.ClientApprovalRequired, settings.ApprovedClients);
        var connectionTracker = new ClientConnectionTracker();
        using var fileLogWriter = new PortalFileLogWriter(contentRoot);
        var logStore = new PortalLogStore(fileLogWriter: fileLogWriter);
        var sessionStore = new PortalSessionStore();
        var backendSelection = WebRtcBackendSelection.Resolve();
        var webRtcStreamSessionFactory = backendSelection.Factory;
        var server = new PortalServer(args, contentRoot, runtimeState, connectionTracker, logStore, sessionStore, webRtcStreamSessionFactory, approvalService);
        if (backendSelection.UsedFallback)
        {
            logStore.AddWarning("startup", $"Unknown WINDOW_SHARE_PORTAL_WEBRTC_BACKEND value '{backendSelection.ConfiguredValue}'. Falling back to {backendSelection.EffectiveValue}.");
        }

        var backendSource = string.IsNullOrWhiteSpace(backendSelection.ConfiguredValue)
            ? "default"
            : backendSelection.ConfiguredValue;
        logStore.AddInformation("startup", $"Selected WebRTC backend: {webRtcStreamSessionFactory.BackendName} (configured={backendSource}).");
        RegisterGlobalExceptionLogging(logStore);

        try
        {
            var app = new System.Windows.Application();
            app.DispatcherUnhandledException += (_, eventArgs) =>
            {
                logStore.AddError("wpf", $"Unhandled dispatcher exception.{Environment.NewLine}{eventArgs.Exception}");
                eventArgs.Handled = true;
            };

            var window = new MainWindow(runtimeState, connectionTracker, logStore, server, approvalService);
            app.Run(window);
            return 0;
        }
        catch (Exception exception)
        {
            logStore.AddError("fatal", $"Application terminated unexpectedly.{Environment.NewLine}{exception}");
            return 1;
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void RegisterGlobalExceptionLogging(PortalLogStore logStore)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                logStore.AddError("appdomain", $"Unhandled exception. IsTerminating={eventArgs.IsTerminating}.{Environment.NewLine}{exception}");
            }
            else
            {
                logStore.AddError("appdomain", $"Unhandled non-exception object. IsTerminating={eventArgs.IsTerminating}. Value={eventArgs.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            if (WebRtcTaskUtilities.IsExpectedSocketShutdown(eventArgs.Exception))
            {
                logStore.AddInformation("tasks", $"Ignored expected background socket shutdown.{Environment.NewLine}{eventArgs.Exception}");
            }
            else
            {
                logStore.AddError("tasks", $"Unobserved task exception.{Environment.NewLine}{eventArgs.Exception}");
            }

            eventArgs.SetObserved();
        };
    }

    private static int ResolveListenPort()
    {
        var portValue = Environment.GetEnvironmentVariable("WINDOW_SHARE_PORTAL_PORT");
        if (int.TryParse(portValue, out var parsedPort) && parsedPort is > 0 and <= 65535)
        {
            return parsedPort;
        }

        var configuredUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(configuredUrls))
        {
            foreach (var candidate in configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Port is > 0 and <= 65535)
                {
                    return uri.Port;
                }
            }
        }

        return 48331;
    }

    private static string ResolveContentRoot(string baseDirectory)
    {
        var current = new DirectoryInfo(baseDirectory);
        while (current is not null)
        {
            var projectFilePath = Path.Combine(current.FullName, "LocalWindowShare.csproj");
            var webRootPath = Path.Combine(current.FullName, "wwwroot");
            if (File.Exists(projectFilePath) && Directory.Exists(webRootPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return baseDirectory;
    }
}
