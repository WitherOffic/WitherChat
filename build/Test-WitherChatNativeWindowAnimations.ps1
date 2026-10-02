param(
    [Parameter(Mandatory)]
    [string]$ExecutablePath,
    [switch]$OpenFiltersBeforeCompact,
    [ValidateRange(1, 30)]
    [int]$RepeatedCompactCycles = 6
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WitherChatNativeWindowTest
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hwnd, int command);
}
'@

function Get-NativeRect {
    param([IntPtr]$Handle)

    $rect = [WitherChatNativeWindowTest+RECT]::new()
    if (-not [WitherChatNativeWindowTest]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }

    [pscustomobject]@{
        X = $rect.Left
        Y = $rect.Top
        Width = $rect.Right - $rect.Left
        Height = $rect.Bottom - $rect.Top
    }
}

function Get-AutomationWindow {
    param([int]$ProcessId)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
    $windows |
        Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending |
        Select-Object -First 1
}

function Invoke-AutomationButton {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$AutomationId
    )

    $button = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.AutomationId -eq $AutomationId } |
        Select-Object -First 1
    if ($null -eq $button) {
        throw "Button '$AutomationId' was not found."
    }

    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Get-AnimationSamples {
    param(
        [IntPtr]$Handle,
        [int]$Count = 45,
        [int]$IntervalMilliseconds = 20
    )

    $samples = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $Count; $index++) {
        $samples.Add((Get-NativeRect -Handle $Handle))
        Start-Sleep -Milliseconds $IntervalMilliseconds
    }
    $samples
}

function Assert-SmoothSizeTransition {
    param(
        [object[]]$Samples,
        [bool]$Shrinking
    )

    $distinctSizes = $Samples |
        ForEach-Object { "$($_.Width)x$($_.Height)" } |
        Sort-Object -Unique
    if ($distinctSizes.Count -lt 10) {
        throw "Only $($distinctSizes.Count) distinct animation frames were observed."
    }

    for ($index = 1; $index -lt $Samples.Count; $index++) {
        $widthDelta = $Samples[$index].Width - $Samples[$index - 1].Width
        $heightDelta = $Samples[$index].Height - $Samples[$index - 1].Height
        if ($Shrinking -and ($widthDelta -gt 2 -or $heightDelta -gt 2)) {
            throw "Compact animation oscillated at frame $index."
        }
        if (-not $Shrinking -and ($widthDelta -lt -2 -or $heightDelta -lt -2)) {
            throw "Expand animation oscillated at frame $index."
        }
    }
}

$executable = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Executable was not found: $executable"
}

$process = Start-Process -FilePath $executable -PassThru
try {
    $window = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) {
            throw 'WitherChat exited during startup.'
        }
        $window = Get-AutomationWindow -ProcessId $process.Id
    } while (($null -eq $window -or $window.Current.NativeWindowHandle -eq 0) -and
             [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $window -or $window.Current.NativeWindowHandle -eq 0) {
        throw 'The main WitherChat window was not shown.'
    }

    $handle = [IntPtr]$window.Current.NativeWindowHandle
    $initial = Get-NativeRect -Handle $handle

    if ($OpenFiltersBeforeCompact) {
        Invoke-AutomationButton -Window $window -AutomationId 'FiltersButton'
        Start-Sleep -Milliseconds 250
        $filterInput = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.AutomationId -eq 'MessageSearchInput' } |
            Select-Object -First 1
        if ($null -eq $filterInput -or $filterInput.Current.IsOffscreen) {
            throw 'The filter panel did not open before the compact transition.'
        }
    }

    Invoke-AutomationButton -Window $window -AutomationId 'CompactModeButton'
    $compactSamples = Get-AnimationSamples -Handle $handle
    Assert-SmoothSizeTransition -Samples $compactSamples -Shrinking $true
    $compact = Get-NativeRect -Handle $handle
    if ([Math]::Abs($compact.Width - 360) -gt 2 -or
        [Math]::Abs($compact.Height - 400) -gt 2) {
        throw "Unexpected compact size: $($compact.Width)x$($compact.Height)."
    }

    if ($OpenFiltersBeforeCompact) {
        $window = Get-AutomationWindow -ProcessId $process.Id
        $filterInput = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.AutomationId -eq 'MessageSearchInput' } |
            Select-Object -First 1
        if ($null -ne $filterInput -and -not $filterInput.Current.IsOffscreen) {
            throw 'The filter panel remained visible in compact mode.'
        }
    }

    $screens = [System.Windows.Forms.Screen]::AllScreens
    $targetScreen = if ($screens.Count -gt 1) { $screens[1] } else { $screens[0] }
    $moveX = $targetScreen.WorkingArea.Left + 40
    $moveY = $targetScreen.WorkingArea.Top + 40
    if (-not [WitherChatNativeWindowTest]::SetWindowPos(
            $handle,
            [IntPtr]::Zero,
            $moveX,
            $moveY,
            $compact.Width,
            $compact.Height,
            0x0014)) {
        throw 'Could not move the compact window.'
    }
    Start-Sleep -Milliseconds 100

    $window = Get-AutomationWindow -ProcessId $process.Id
    Invoke-AutomationButton -Window $window -AutomationId 'CompactModeButton'
    $expandSamples = Get-AnimationSamples -Handle $handle
    Assert-SmoothSizeTransition -Samples $expandSamples -Shrinking $false
    $expanded = Get-NativeRect -Handle $handle
    if ([Math]::Abs($expanded.Width - $initial.Width) -gt 3 -or
        [Math]::Abs($expanded.Height - $initial.Height) -gt 3) {
        throw "Expanded size did not return to $($initial.Width)x$($initial.Height)."
    }

    $expectedX = [Math]::Min(
        [Math]::Max(
            [Math]::Round($moveX + ($compact.Width / 2) - ($initial.Width / 2)),
            $targetScreen.WorkingArea.Left),
        $targetScreen.WorkingArea.Right - $initial.Width)
    $expectedY = [Math]::Min(
        [Math]::Max(
            [Math]::Round($moveY + ($compact.Height / 2) - ($initial.Height / 2)),
            $targetScreen.WorkingArea.Top),
        $targetScreen.WorkingArea.Bottom - $initial.Height)
    if ([Math]::Abs($expanded.X - $expectedX) -gt 4 -or
        [Math]::Abs($expanded.Y - $expectedY) -gt 4) {
        throw "Expanded window returned to an old position: $($expanded.X),$($expanded.Y)."
    }

    $stableExpanded = $expanded
    for ($cycle = 1; $cycle -le $RepeatedCompactCycles; $cycle++) {
        $window = Get-AutomationWindow -ProcessId $process.Id
        Invoke-AutomationButton -Window $window -AutomationId 'CompactModeButton'
        Start-Sleep -Milliseconds 1100
        $repeatedCompact = Get-NativeRect -Handle $handle
        if ([Math]::Abs($repeatedCompact.Width - 360) -gt 2 -or
            [Math]::Abs($repeatedCompact.Height - 400) -gt 2) {
            throw "Unexpected compact size in repeated cycle ${cycle}: $($repeatedCompact.Width)x$($repeatedCompact.Height)."
        }

        $window = Get-AutomationWindow -ProcessId $process.Id
        Invoke-AutomationButton -Window $window -AutomationId 'CompactModeButton'
        Start-Sleep -Milliseconds 1100
        $repeatedExpanded = Get-NativeRect -Handle $handle
        if ([Math]::Abs($repeatedExpanded.X - $stableExpanded.X) -gt 4 -or
            [Math]::Abs($repeatedExpanded.Y - $stableExpanded.Y) -gt 4) {
            throw "Window position drifted in repeated cycle ${cycle}: $($repeatedExpanded.X),$($repeatedExpanded.Y) instead of $($stableExpanded.X),$($stableExpanded.Y)."
        }
        if ([Math]::Abs($repeatedExpanded.Width - $stableExpanded.Width) -gt 3 -or
            [Math]::Abs($repeatedExpanded.Height - $stableExpanded.Height) -gt 3) {
            throw "Window size drifted in repeated cycle ${cycle}."
        }
    }

    if (-not [WitherChatNativeWindowTest]::PostMessage(
            $handle,
            0x0112,
            [IntPtr]0xF020,
            [IntPtr]::Zero)) {
        throw 'System minimize command failed.'
    }
    $minimizeDeadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not [WitherChatNativeWindowTest]::IsIconic($handle) -and
           [DateTime]::UtcNow -lt $minimizeDeadline) {
        Start-Sleep -Milliseconds 50
    }
    if (-not [WitherChatNativeWindowTest]::IsIconic($handle)) {
        throw 'The taskbar/system minimize path did not minimize the window.'
    }

    [WitherChatNativeWindowTest]::ShowWindowAsync($handle, 9) | Out-Null
    $restoreDeadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([WitherChatNativeWindowTest]::IsIconic($handle) -and
           [DateTime]::UtcNow -lt $restoreDeadline) {
        Start-Sleep -Milliseconds 50
    }
    if ([WitherChatNativeWindowTest]::IsIconic($handle)) {
        throw 'The system restore path did not restore the window.'
    }

    "Executable=$executable"
    "CompactFrames=$(($compactSamples | ForEach-Object { "$($_.Width)x$($_.Height)" } | Sort-Object -Unique).Count)"
    "ExpandFrames=$(($expandSamples | ForEach-Object { "$($_.Width)x$($_.Height)" } | Sort-Object -Unique).Count)"
    "CompactSize=$($compact.Width)x$($compact.Height)"
    "ExpandedSize=$($expanded.Width)x$($expanded.Height)"
    "MonitorCount=$($screens.Count)"
    "RepeatedCompactCycles=$RepeatedCompactCycles"
    "TransientUiClosed=$OpenFiltersBeforeCompact"
    'SystemMinimizeRestore=True'
    'NativeAnimationTest=Passed'
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
