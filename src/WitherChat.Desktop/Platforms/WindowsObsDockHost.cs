using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using WitherChat.Core.Services;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.Views;

namespace WitherChat.Desktop.Platforms;

// WitherChat keeps its own UI thread/process. OBS owns only the native parent container.
internal sealed class WindowsObsDockHost : IDisposable
{
    private readonly Window _window;
    private readonly Action<bool> _configureLayout;
    private readonly Action _updateLayout;
    private readonly bool _preserveVisibility;
    private bool _originalVisible;
    private WindowState _originalState;
    private readonly Action _showStandalone;
    private readonly DispatcherTimer _timer;
    private ObsDockRequest? _owner;
    private nint _handle;
    private nint _originalStyle;
    private nint _originalExStyle;
    private PixelPoint _originalPosition;
    private Size _originalSize;
    private bool _changing;
    private bool _disposed;
    private int _width;
    private int _height;

    internal WindowsObsDockHost(MainWindow window, Action showStandalone)
        : this(window, showStandalone, window.ConfigureObsDockLayout, window.UpdateObsDockLayout) { }

    internal WindowsObsDockHost(DonationAlertsWindow window, Action showStandalone)
        : this(window, showStandalone, window.ConfigureObsDockLayout, window.UpdateObsDockLayout, true) { }

    private WindowsObsDockHost(Window window, Action showStandalone, Action<bool> configureLayout,
        Action updateLayout, bool preserveVisibility = false)
    {
        _window = window;
        _showStandalone = showStandalone;
        _configureLayout = configureLayout;
        _updateLayout = updateLayout;
        _preserveVisibility = preserveVisibility;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
    }

    internal static nint EmbeddedStyle(nint original) => (nint)((long)original & ~0xA1CF0000L | 0x54000000L);

    internal bool IsAttached => _owner.HasValue;
    internal uint? OwnerProcessId => _owner?.ProcessId;
    internal bool Owns(ObsDockRequest request) => _owner is { } owner &&
        owner.ProcessId == request.ProcessId && owner.ParentWindow == request.ParentWindow;

    internal void ShowChatTab() => ShowTab(false);
    internal void ShowDonationsTab() => ShowTab(true);

    private void ShowTab(bool donations)
    {
        if (_owner is { } owner)
            _ = PostMessage(owner.ParentWindow, RegisterWindowMessage(donations ? "WitherChat.ObsDock.ShowDonations.v1" : "WitherChat.ObsDock.ShowChat.v1"), _handle, 0);
    }

    internal static bool IsValidParent(ObsDockRequest request)
    {
        if (!OperatingSystem.IsWindows() || !IsWindow(request.ParentWindow)) return false;
        _ = GetWindowThreadProcessId(request.ParentWindow, out var pid);
        if (pid != request.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.SessionId == Process.GetCurrentProcess().SessionId &&
                string.Equals(process.ProcessName, "obs64", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    internal string Handle(ObsDockRequest request)
    {
        if (_disposed) return "ERROR shutting-down";
        if (!request.Attach)
        {
            if (_owner is not { } owner || owner.ProcessId != request.ProcessId ||
                owner.ParentWindow != request.ParentWindow) return "ERROR not-owner";
            Detach(show: true);
            return "OK detached";
        }

        if (!IsValidParent(request)) return "ERROR invalid-obs-window";
        if (_owner is { } current)
        {
            if (current.ProcessId != request.ProcessId || current.ParentWindow != request.ParentWindow)
                return "ERROR already-attached";
            _window.Show();
            RefreshBounds();
            return "OK " + ((ulong)_handle).ToString(CultureInfo.InvariantCulture);
        }

        var native = _window.TryGetPlatformHandle();
        if (native?.HandleDescriptor != "HWND") return "ERROR native-window-unavailable";
        // SetParent with mismatched awareness can reset the entire child process's DPI mode.
        if (!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(native.Handle),
                GetWindowDpiAwarenessContext(request.ParentWindow))) return "ERROR incompatible-dpi";

        _changing = true;
        try
        {
            _handle = native.Handle;
            _originalStyle = GetWindowLongPtr(_handle, -16);
            _originalExStyle = GetWindowLongPtr(_handle, -20);
            _originalPosition = _window.Position;
            _originalVisible = _window.IsVisible;
            _originalState = _window.WindowState;
            _originalSize = new Size(double.IsFinite(_window.Width) ? _window.Width : 1100,
                double.IsFinite(_window.Height) ? _window.Height : 760);
            _configureLayout(true);
            _window.WindowState = WindowState.Normal;
            _window.Show();
            _ = SetWindowLongPtr(_handle, -16, EmbeddedStyle(_originalStyle));
            _ = SetWindowLongPtr(_handle, -20, (nint)((long)_originalExStyle & ~0x00040109L));
            _ = SetParent(_handle, request.ParentWindow);
            var error = Marshal.GetLastPInvokeError();
            if (error != 0) throw new Win32Exception(error);
            _owner = request;
            _width = _height = 0;
            _ = SetWindowPos(_handle, 0, 0, 0, 0, 0, 0x37); // frame changed, no activate/move/size/z-order
            RefreshBounds();
            _timer.Start();
            return "OK " + ((ulong)_handle).ToString(CultureInfo.InvariantCulture);
        }
        catch (Win32Exception exception)
        {
            RestoreNativeWindow();
            _configureLayout(false);
            _window.WindowState = _originalState;
            AppDiagnostics.Write("OBS dock attach", exception);
            return "ERROR attach-failed";
        }
        finally { _changing = false; }
    }

    private void OnTick(object? sender, EventArgs args)
    {
        if (_changing || _disposed || _owner is not { } owner) return;
        if (!IsWindow(owner.ParentWindow) ||
            GetWindowThreadProcessId(owner.ParentWindow, out var pid) == 0 || pid != owner.ProcessId)
        {
            Detach(show: true);
            return;
        }
        RefreshBounds();
    }

    private void RefreshBounds()
    {
        if (_owner is not { } owner || !GetClientRect(owner.ParentWindow, out var rect)) return;
        var width = Math.Max(1, rect.Right);
        var height = Math.Max(1, rect.Bottom);
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;
        _ = SetWindowPos(_handle, 0, 0, 0, width, height, 0x14); // no z-order/activation
        _updateLayout();
    }

    internal void Detach(bool show)
    {
        if (!IsAttached) return;
        _changing = true;
        try
        {
            _timer.Stop();
            _owner = null;
            RestoreNativeWindow();
            _configureLayout(false);
            _window.Width = Math.Max(_window.MinWidth, _originalSize.Width);
            _window.Height = Math.Max(_window.MinHeight, _originalSize.Height);
            _window.Position = _originalPosition;
            _window.WindowState = _originalState;
            if (show && (!_preserveVisibility || _originalVisible)) _showStandalone();
            else if (show) _window.Hide();
        }
        finally { _changing = false; }
    }

    private void RestoreNativeWindow()
    {
        if (_handle == 0 || !IsWindow(_handle)) return;
        _ = SetParent(_handle, 0);
        _ = SetWindowLongPtr(_handle, -16, (nint)((long)_originalStyle | (_window.IsVisible ? 0x10000000L : 0)));
        _ = SetWindowLongPtr(_handle, -20, _originalExStyle);
        _ = SetWindowPos(_handle, 0, 0, 0, 0, 0, 0x37);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach(show: false);
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
