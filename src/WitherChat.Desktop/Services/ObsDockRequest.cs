using System.Globalization;

namespace WitherChat.Desktop.Services;

internal readonly record struct ObsDockRequest(bool Attach, uint ProcessId, nint ParentWindow, bool Donations = false)
{
    internal static bool TryParse(string? command, out ObsDockRequest request)
    {
        request = default;
        if (command is null || command.Length > 128) return false;
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] is not ("ATTACH" or "ATTACH_DONATIONS" or "DETACH") ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == 0 ||
            !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var handle) ||
            handle == 0 || handle > (ulong)nint.MaxValue) return false;
        request = new ObsDockRequest(parts[0] != "DETACH", pid, (nint)handle, parts[0] == "ATTACH_DONATIONS");
        return true;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{(Attach ? Donations ? "ATTACH_DONATIONS" : "ATTACH" : "DETACH")} {ProcessId} {(ulong)ParentWindow}");

    internal static bool TryParseArguments(string[] args, out ObsDockRequest request) =>
        args.Length == 3 && args[0] == "--obs-dock"
            ? TryParse($"ATTACH {args[1]} {args[2]}", out request)
            : Fail(out request);

    private static bool Fail(out ObsDockRequest request)
    {
        request = default;
        return false;
    }
}
