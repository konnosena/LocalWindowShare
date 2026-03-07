using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class PortalSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public PortalSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowSharePortal",
            "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public PortalSettings Load(string? initialToken, int initialPort)
    {
        PortalSettingsDocument? payload = null;
        try
        {
            if (File.Exists(_settingsPath))
            {
                payload = JsonSerializer.Deserialize<PortalSettingsDocument>(File.ReadAllText(_settingsPath));
            }
        }
        catch
        {
        }

        var persistedToken = ReadPersistedToken(payload);
        var token = !string.IsNullOrWhiteSpace(persistedToken)
            ? persistedToken.Trim()
            : !string.IsNullOrWhiteSpace(initialToken)
            ? initialToken.Trim()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var port = payload?.Port is > 0 and <= 65535
            ? payload.Port.Value
            : initialPort;
        var windowPlacement = NormalizeWindowPlacement(payload?.WindowPlacement);
        var loadedFromSettings = !string.IsNullOrWhiteSpace(persistedToken) || payload?.Port is > 0 and <= 65535;

        Save(token, port, windowPlacement);
        return new PortalSettings(
            token,
            port,
            windowPlacement,
            loadedFromSettings
                ? "GUI settings"
                : string.IsNullOrWhiteSpace(initialToken) ? "Generated at first launch" : "Seeded from environment");
    }

    public void Save(string token, int port, PortalWindowPlacement? windowPlacement)
    {
        var normalizedToken = token.Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            throw new InvalidOperationException("Token must not be empty.");
        }

        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }

        var directoryPath = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var temporaryPath = _settingsPath + ".tmp";
        var document = new PortalSettingsDocument
        {
            TokenProtected = ProtectToken(normalizedToken),
            Port = port,
            WindowPlacement = windowPlacement,
        };

        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private static PortalWindowPlacement? NormalizeWindowPlacement(PortalWindowPlacement? placement)
    {
        if (placement is null)
        {
            return null;
        }

        return placement.Value.Width < 160 || placement.Value.Height < 120
            ? null
            : placement;
    }

    private static string? ReadPersistedToken(PortalSettingsDocument? payload)
    {
        if (!string.IsNullOrWhiteSpace(payload?.TokenProtected))
        {
            try
            {
                return UnprotectToken(payload.TokenProtected);
            }
            catch
            {
            }
        }

        return payload?.Token;
    }

    private static string ProtectToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectToken(string tokenProtected)
    {
        var protectedBytes = Convert.FromBase64String(tokenProtected);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class PortalSettingsDocument
    {
        public string? Token { get; init; }

        public string? TokenProtected { get; init; }

        public int? Port { get; init; }

        public PortalWindowPlacement? WindowPlacement { get; init; }
    }
}

internal sealed record PortalSettings(string Token, int Port, PortalWindowPlacement? WindowPlacement, string TokenModeLabel);

internal readonly record struct PortalWindowPlacement(int Left, int Top, int Width, int Height, bool IsMaximized);
