internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var contentRoot = ResolveContentRoot(AppContext.BaseDirectory);
        var settingsStore = new PortalSettingsStore();
        var listenPort = ResolveListenPort();
        var settings = settingsStore.Load(Environment.GetEnvironmentVariable("WINDOW_SHARE_PORTAL_TOKEN"), listenPort);
        var runtimeState = new PortalRuntimeState(settings, settingsStore);
        var connectionTracker = new ClientConnectionTracker();
        var logStore = new PortalLogStore();
        var sessionStore = new PortalSessionStore();
        var server = new PortalServer(args, contentRoot, runtimeState, connectionTracker, logStore, sessionStore);

        try
        {
            using var form = new PortalControlForm(runtimeState, connectionTracker, logStore, server);
            Application.Run(form);
            return 0;
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
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
            var projectFilePath = Path.Combine(current.FullName, "WindowSharePortal.csproj");
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
