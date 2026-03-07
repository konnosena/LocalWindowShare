internal static class PortalSecurityLimits
{
    public const string SessionCookieName = "window_share_portal_session";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    public const long MaxClientLogBodyBytes = 8 * 1024;
    public const long MaxTextInputBodyBytes = 8 * 1024;
    public const int MaxClientLogSourceChars = 64;
    public const int MaxClientLogMessageChars = 1024;
    public const int MaxClientLogContextChars = 2048;
    public const int MaxTextInputChars = 2048;
}
