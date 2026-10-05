using System.Diagnostics;
using System.Globalization;

namespace WitherChat.Desktop.Services;

internal static class SessionPipeName
{
    internal static string ForCurrentSession(string name)
    {
        if (!OperatingSystem.IsWindows()) return name;
        using var process = Process.GetCurrentProcess();
        return ForSession(name, process.SessionId);
    }

    internal static string ForSession(string name, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        // The single-instance mutex is Local\\ (per Windows session), while
        // named pipes are not. Keep console, RDP and service copies separate.
        return name + "-s" + sessionId.ToString(CultureInfo.InvariantCulture);
    }
}
