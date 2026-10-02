using System.Runtime.InteropServices;

namespace WitherChat.Desktop.Platforms;

internal static class WindowsWindowAnimation
{
    private const int GwlStyle = -16;
    private const int GwlWndProc = -4;
    private const int SwMaximize = 3;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint WmSysCommand = 0x0112;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const long ScCommandMask = 0xFFF0;
    private const long ScMinimize = 0xF020;
    private const long ScRestore = 0xF120;
    private const long WsCaption = 0x00C00000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly Dictionary<IntPtr, WindowProcedureRegistration> WindowProcedures = [];

    public static bool EnableSystemMinimizeAnimation(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var style = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
        var requiredStyles = WsCaption |
                             WsSysMenu |
                             WsThickFrame |
                             WsMinimizeBox |
                             WsMaximizeBox;
        if ((style & requiredStyles) != requiredStyles)
        {
            _ = SetWindowLongPtr(
                windowHandle,
                GwlStyle,
                new IntPtr(style | requiredStyles));
            _ = SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove |
                SwpNoSize |
                SwpNoZOrder |
                SwpNoActivate |
                SwpFrameChanged);
        }

        const int transitionsForcedDisabled = 3;
        var transitionsDisabled = 0;
        _ = DwmSetWindowAttribute(
            windowHandle,
            transitionsForcedDisabled,
            ref transitionsDisabled,
            sizeof(int));
        return true;
    }

    public static bool TryMinimize(IntPtr windowHandle) =>
        EnableSystemMinimizeAnimation(windowHandle) &&
        ShowWindowAsync(windowHandle, SwMinimize);

    public static bool TryRestore(IntPtr windowHandle) =>
        EnableSystemMinimizeAnimation(windowHandle) &&
        ShowWindowAsync(windowHandle, SwRestore);

    public static bool TrySetBounds(
        IntPtr windowHandle,
        int x,
        int y,
        int width,
        int height) =>
        OperatingSystem.IsWindows() &&
        windowHandle != IntPtr.Zero &&
        SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            x,
            y,
            width,
            height,
            SwpNoZOrder | SwpNoActivate);

    public static bool TryGetWindowSize(
        IntPtr windowHandle,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (!OperatingSystem.IsWindows() ||
            windowHandle == IntPtr.Zero ||
            !GetWindowRect(windowHandle, out var bounds))
        {
            return false;
        }

        width = Math.Max(1, bounds.Right - bounds.Left);
        height = Math.Max(1, bounds.Bottom - bounds.Top);
        return true;
    }

    public static bool TrySetMaximized(IntPtr windowHandle, bool maximize)
    {
        return OperatingSystem.IsWindows() &&
               windowHandle != IntPtr.Zero &&
               ShowWindowAsync(windowHandle, maximize ? SwMaximize : SwRestore);
    }

    public static bool InstallSystemCommandHook(
        IntPtr windowHandle,
        Action? sessionEndQuery = null,
        Action<bool>? sessionEnd = null)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        lock (WindowProcedures)
        {
            if (WindowProcedures.ContainsKey(windowHandle))
            {
                return true;
            }

            var originalProcedure = GetWindowLongPtr(windowHandle, GwlWndProc);
            if (originalProcedure == IntPtr.Zero)
            {
                return false;
            }

            WindowProcedure? procedure = null;
            procedure = (handle, message, wParam, lParam) =>
            {
                if (ProcessSessionEndMessage(
                        message,
                        wParam,
                        sessionEndQuery,
                        sessionEnd,
                        out var sessionResult))
                {
                    return sessionResult;
                }

                if (message == WmSysCommand)
                {
                    var command = wParam.ToInt64() & ScCommandMask;
                    if (command is ScMinimize or ScRestore)
                    {
                        _ = EnableSystemMinimizeAnimation(handle);
                    }
                }

                return CallWindowProc(originalProcedure, handle, message, wParam, lParam);
            };

            var procedurePointer = Marshal.GetFunctionPointerForDelegate(procedure);
            Marshal.SetLastPInvokeError(0);
            var replacedProcedure = SetWindowLongPtr(windowHandle, GwlWndProc, procedurePointer);
            if (replacedProcedure == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }

            WindowProcedures[windowHandle] = new WindowProcedureRegistration(
                originalProcedure,
                procedurePointer,
                procedure);
            return true;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A Windows session-ending callback must never reject shutdown or escape through an unmanaged window procedure.")]
    internal static bool ProcessSessionEndMessage(
        uint message,
        IntPtr wParam,
        Action? sessionEndQuery,
        Action<bool>? sessionEnd,
        out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message == WmQueryEndSession && sessionEndQuery is not null)
        {
            try
            {
                sessionEndQuery();
            }
            catch (Exception)
            {
                // Never let application cleanup reject a Windows shutdown request.
            }

            result = new IntPtr(1);
            return true;
        }

        if (message == WmEndSession && sessionEnd is not null)
        {
            try
            {
                sessionEnd(wParam != IntPtr.Zero);
            }
            catch (Exception)
            {
                // Native window procedures must not allow managed exceptions to escape.
            }
        }

        return false;
    }

    public static void RemoveSystemCommandHook(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return;
        }

        lock (WindowProcedures)
        {
            if (!WindowProcedures.Remove(windowHandle, out var registration))
            {
                return;
            }

            if (GetWindowLongPtr(windowHandle, GwlWndProc) == registration.ProcedurePointer)
            {
                _ = SetWindowLongPtr(windowHandle, GwlWndProc, registration.OriginalProcedure);
            }

            GC.KeepAlive(registration.Procedure);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    private sealed record WindowProcedureRegistration(
        IntPtr OriginalProcedure,
        IntPtr ProcedurePointer,
        WindowProcedure Procedure);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    [return: MarshalAs(UnmanagedType.SysInt)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProcedure,
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out WindowBounds bounds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);
}
