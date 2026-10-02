using System.Runtime.InteropServices;

namespace WitherChat.Desktop.Platforms;

internal static class WindowsTrayNotification
{
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint NifInfo = 16;
    private const uint NiifInfo = 1;

    public static void Show(IntPtr windowHandle, string title, string message)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return;
        }
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        _ = ExtractIconEx(Environment.ProcessPath ?? string.Empty, 0, large, small, 1);
        var icon = small[0] != IntPtr.Zero ? small[0] : large[0];
        if (icon == IntPtr.Zero)
        {
            return;
        }
        var data = new NotifyIconData
        {
            CbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = windowHandle,
            Id = 0x5743031,
            Flags = NifIcon | NifTip | NifInfo,
            IconHandle = icon,
            Tip = "WitherChat",
            Info = message,
            InfoTitle = title,
            InfoFlags = NiifInfo
        };
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            DestroyExtractedIcons(large[0], small[0]);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            ShellNotifyIcon(NimDelete, ref data);
            DestroyExtractedIcons(large[0], small[0]);
        });
    }

    private static void DestroyExtractedIcons(IntPtr large, IntPtr small)
    {
        if (large != IntPtr.Zero)
        {
            DestroyIcon(large);
        }
        if (small != IntPtr.Zero && small != large)
        {
            DestroyIcon(small);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint CbSize;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] largeIcons, IntPtr[] smallIcons, uint count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
