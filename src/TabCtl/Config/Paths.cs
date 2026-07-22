using System.Security.Cryptography;

namespace TabCtl.Config;

public static class Paths
{
    public const int DefaultPort = 9333;

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tabctl");

    public static string TokenFile => Path.Combine(Dir, "token");
    public static string LogFile => Path.Combine(Dir, "daemon.log");

    /// <summary>
    /// Reads the shared token, creating it on first run.
    ///
    /// The token is required as an <c>X-Cdp-Token</c> header rather than a query parameter
    /// deliberately: a custom header forces a CORS preflight that the daemon never answers,
    /// so a malicious page cannot drive the armed tab by POSTing to localhost.
    /// </summary>
    public static string GetOrCreateToken()
    {
        Directory.CreateDirectory(Dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        if (File.Exists(TokenFile))
        {
            var existing = File.ReadAllText(TokenFile).Trim();
            if (existing.Length > 0) return existing;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(TokenFile, token);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(TokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return token;
    }
}
