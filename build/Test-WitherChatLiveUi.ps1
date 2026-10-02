param(
    [string]$ExecutablePath = '',
    [string]$Channel = 'twitch',
    [int]$WaitSeconds = 20,
    [string]$OutputPath = '',
    [switch]$CaptureOnly,
    [switch]$StressScroll,
    [ValidateRange(1, 100)]
    [int]$StressScrollCycles = 1,
    [switch]$SyntheticStress,
    [switch]$SyntheticFeed,
    [switch]$SyntheticMediaLoading,
    [switch]$OpenCompact,
    [switch]$BrowseHistory,
    [switch]$GracefulExit,
    [switch]$OpenSettings,
    [switch]$OpenThemeDropdown,
    [switch]$OpenLogs,
    [string]$LogChannel = '',
    [int]$LogFileIndex = -1,
    [switch]$OpenChannels,
    [switch]$OpenModeration,
    [switch]$OpenFilters,
    [ValidateRange(-1, 7)]
    [int]$OnboardingStep = -1,
    [ValidateRange(-1, 5000)]
    [int]$OnboardingTransitionCaptureDelayMs = -1,
    [string]$MessageFilter = '',
    [string]$UserFilter = '',
    [string]$ComposerText = '',
    [ValidateSet('Program', 'Chat', 'ChatLogs', 'Overlay', 'Account', 'Donate', 'Advanced')]
    [string]$SettingsSection = 'Program',
    [ValidateSet('Dark', 'Light', 'System')]
    [string]$Theme = 'Dark',
    [ValidateSet('ru', 'en')]
    [string]$Language = 'ru',
    [int]$WindowWidth = 0,
    [int]$WindowHeight = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WitherChatNativeCapture
{
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
'@

$workspace = Split-Path -Parent $PSScriptRoot
$executable = if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    Join-Path $workspace 'src\WitherChat.Desktop\bin\Debug\net8.0\WitherChat.exe'
}
else {
    [System.IO.Path]::GetFullPath($ExecutablePath)
}
if (-not (Test-Path -LiteralPath $executable)) {
    throw "WitherChat executable was not found: $executable"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspace 'artifacts\WitherChat\ui-live-emotes-network-0.5.1A.png'
}

$previousStressValue = $env:WITHERCHAT_UI_STRESS
$previousStressFeedValue = $env:WITHERCHAT_UI_STRESS_FEED
$previousMediaLoadingValue = $env:WITHERCHAT_UI_MEDIA_LOADING
$previousThemeValue = $env:WITHERCHAT_UI_THEME
$previousLanguageValue = $env:WITHERCHAT_UI_LANGUAGE
$previousOpenModerationValue = $env:WITHERCHAT_UI_OPEN_MODERATION
$previousOpenCompactValue = $env:WITHERCHAT_UI_OPEN_COMPACT
$previousBrowseHistoryValue = $env:WITHERCHAT_UI_BROWSE_HISTORY
$previousMutexNameValue = $env:WITHERCHAT_UI_MUTEX_NAME
$previousDataDirectoryValue = $env:WITHERCHAT_UI_DATA_DIRECTORY
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$testDataDirectory = Join-Path $temporaryRoot ('WitherChat-ui-test-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testDataDirectory) | Out-Null

function Remove-TestDataDirectory {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $leaf = [System.IO.Path]::GetFileName($resolved.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar))
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith('WitherChat-ui-test-', [StringComparison]::Ordinal)) {
        throw "Refusing to remove an unexpected UI-test directory: $resolved"
    }

    $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove a non-directory or reparse point: $resolved"
    }

    [System.IO.Directory]::Delete($resolved, $true)
}

$env:WITHERCHAT_UI_MUTEX_NAME = 'WitherChat-ui-test-' + [Guid]::NewGuid().ToString('N')
$env:WITHERCHAT_UI_DATA_DIRECTORY = $testDataDirectory
$env:WITHERCHAT_UI_THEME = $Theme
$env:WITHERCHAT_UI_LANGUAGE = $Language
if ($SyntheticStress) {
    $env:WITHERCHAT_UI_STRESS = '1'
}
if ($SyntheticFeed) {
    $env:WITHERCHAT_UI_STRESS_FEED = '1'
}
if ($SyntheticMediaLoading) {
    $env:WITHERCHAT_UI_MEDIA_LOADING = '1'
}
if ($OpenModeration) {
    $env:WITHERCHAT_UI_OPEN_MODERATION = '1'
}
if ($OpenCompact) {
    $env:WITHERCHAT_UI_OPEN_COMPACT = '1'
}
if ($BrowseHistory) {
    $env:WITHERCHAT_UI_BROWSE_HISTORY = '1'
}

$testSettings = @{
    theme = $Theme
    language = $Language
    closeToTray = $false
    hasCompletedOnboarding = $OnboardingStep -lt 0
}
[System.IO.File]::WriteAllText(
    (Join-Path $testDataDirectory 'settings.json'),
    ($testSettings | ConvertTo-Json))

$process = $null
try {
    $process = Start-Process -FilePath $executable -PassThru
}
finally {
if ($null -eq $previousStressValue) {
    Remove-Item Env:WITHERCHAT_UI_STRESS -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_STRESS = $previousStressValue
}
if ($null -eq $previousStressFeedValue) {
    Remove-Item Env:WITHERCHAT_UI_STRESS_FEED -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_STRESS_FEED = $previousStressFeedValue
}
if ($null -eq $previousMediaLoadingValue) {
    Remove-Item Env:WITHERCHAT_UI_MEDIA_LOADING -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_MEDIA_LOADING = $previousMediaLoadingValue
}
if ($null -eq $previousThemeValue) {
    Remove-Item Env:WITHERCHAT_UI_THEME -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_THEME = $previousThemeValue
}
if ($null -eq $previousLanguageValue) {
    Remove-Item Env:WITHERCHAT_UI_LANGUAGE -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_LANGUAGE = $previousLanguageValue
}
if ($null -eq $previousOpenModerationValue) {
    Remove-Item Env:WITHERCHAT_UI_OPEN_MODERATION -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_OPEN_MODERATION = $previousOpenModerationValue
}
if ($null -eq $previousOpenCompactValue) {
    Remove-Item Env:WITHERCHAT_UI_OPEN_COMPACT -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_OPEN_COMPACT = $previousOpenCompactValue
}
if ($null -eq $previousBrowseHistoryValue) {
    Remove-Item Env:WITHERCHAT_UI_BROWSE_HISTORY -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_BROWSE_HISTORY = $previousBrowseHistoryValue
}
if ($null -eq $previousMutexNameValue) {
    Remove-Item Env:WITHERCHAT_UI_MUTEX_NAME -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_MUTEX_NAME = $previousMutexNameValue
}
if ($null -eq $previousDataDirectoryValue) {
    Remove-Item Env:WITHERCHAT_UI_DATA_DIRECTORY -ErrorAction SilentlyContinue
}
else {
    $env:WITHERCHAT_UI_DATA_DIRECTORY = $previousDataDirectoryValue
}
if ($null -eq $process) {
    Remove-TestDataDirectory -Path $testDataDirectory
}
}
try {
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id)
    $window = $null
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) {
            throw 'WitherChat exited before the test could access its window.'
        }

        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            $processCondition)
        $window = $windows |
            Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending |
            Select-Object -First 1
    } while (($null -eq $window -or $window.Current.BoundingRectangle.Width -lt 100) -and
             [DateTime]::UtcNow -lt $windowDeadline)

    if ($null -eq $window -or $window.Current.BoundingRectangle.Width -lt 100) {
        throw 'The main WitherChat window was not shown.'
    }

    if ($WindowWidth -gt 0 -and $WindowHeight -gt 0) {
        $resized = [WitherChatNativeCapture]::SetWindowPos(
            [IntPtr]$window.Current.NativeWindowHandle,
            [IntPtr]::Zero,
            0,
            0,
            $WindowWidth,
            $WindowHeight,
            0x0015)
        if (-not $resized) {
            throw 'The main WitherChat window could not be resized for layout validation.'
        }
        Start-Sleep -Milliseconds 500
    }

    function Get-ButtonsWithRetry {
        param([System.Windows.Automation.Condition]$Condition)

        for ($attempt = 0; $attempt -lt 6; $attempt++) {
            try {
                $currentWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                    [System.Windows.Automation.TreeScope]::Children,
                    $processCondition)
                $currentWindow = $currentWindows |
                    Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending |
                    Select-Object -First 1
                if ($null -ne $currentWindow) {
                    $matches = $currentWindow.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        $Condition)
                    if ($matches.Count -gt 0) {
                        return $matches
                    }
                }
            }
            catch [System.Runtime.InteropServices.COMException] {
                if ($attempt -eq 5) {
                    throw
                }
            }

            Start-Sleep -Milliseconds 250
        }

        throw 'The WitherChat buttons could not be read through UI Automation.'
    }

    function Get-ButtonByAutomationIdWithRetry {
        param(
            [System.Windows.Automation.Condition]$Condition,
            [Parameter(Mandatory)][string]$AutomationId
        )

        for ($attempt = 0; $attempt -lt 12; $attempt++) {
            $button = Get-ButtonsWithRetry -Condition $Condition |
                Where-Object { $_.Current.AutomationId -eq $AutomationId } |
                Select-Object -First 1
            if ($null -ne $button) {
                return $button
            }

            Start-Sleep -Milliseconds 250
        }

        return $null
    }

    if (-not $SyntheticStress -and -not $CaptureOnly) {
        $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        $channelSwitcherButton = $buttons |
            Where-Object { $_.Current.AutomationId -eq 'ChannelSwitcherButton' } |
            Select-Object -First 1
        if ($null -eq $channelSwitcherButton) {
            throw 'The channel switcher button was not found.'
        }

        ([System.Windows.Automation.InvokePattern]$channelSwitcherButton.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Milliseconds 300

        $editCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)
        $edits = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
        if ($edits.Count -lt 1) {
            $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
            $addChannelButton = $buttons |
                Where-Object { $_.Current.AutomationId -eq 'AddChannelButton' } |
                Select-Object -First 1
            if ($null -ne $addChannelButton) {
                ([System.Windows.Automation.InvokePattern]$addChannelButton.GetCurrentPattern(
                    [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                Start-Sleep -Milliseconds 250
                $edits = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
            }
            if ($edits.Count -lt 1) {
                throw 'The channel input was not found.'
            }
        }

        $valuePattern = [System.Windows.Automation.ValuePattern]$edits[0].GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        $valuePattern.SetValue($Channel)

        $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        $connectButton = $buttons |
            Where-Object { $_.Current.AutomationId -eq 'ConfirmAddChannelButton' } |
            Select-Object -First 1
        if ($null -eq $connectButton) {
            $connectButton = $buttons |
                Where-Object { $_.Current.AutomationId -eq 'ConnectButton' } |
                Select-Object -First 1
        }
        if ($null -eq $connectButton) {
            throw 'The connect button was not found.'
        }

        $invokePattern = [System.Windows.Automation.InvokePattern]$connectButton.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
        $invokePattern.Invoke()
    }

    Start-Sleep -Seconds $WaitSeconds

    if ($process.HasExited) {
        throw 'WitherChat exited while rendering the live chat.'
    }

    if ($OnboardingStep -ge 0) {
        for ($step = 0; $step -lt $OnboardingStep; $step++) {
            $nextButton = $window.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.AutomationId -eq 'OnboardingNextButton' } |
                Select-Object -First 1
            if ($null -eq $nextButton) {
                throw "The onboarding Next button was not found before step $($step + 2)."
            }
            $nextButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $stepDelay = if (
                $OnboardingTransitionCaptureDelayMs -ge 0 -and
                $step -eq $OnboardingStep - 1) {
                $OnboardingTransitionCaptureDelayMs
            }
            else {
                750
            }
            Start-Sleep -Milliseconds $stepDelay
        }
    }
    else {
        $onboardingSkipButton = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.AutomationId -eq 'OnboardingSkipButton' } |
            Select-Object -First 1
        if ($null -ne $onboardingSkipButton) {
            $onboardingSkipButton.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 500
        }
    }

    if ($OpenCompact -and $OnboardingStep -lt 0 -and -not $OpenSettings) {
        $currentWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            $processCondition)
        $window = $currentWindows |
            Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending |
            Select-Object -First 1
        if ($null -eq $window) {
            throw 'The main WitherChat window was not available before entering compact mode.'
        }

        if ($window.Current.BoundingRectangle.Width -gt 500) {
            $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            $compactModeButton = Get-ButtonByAutomationIdWithRetry `
                -Condition $buttonCondition `
                -AutomationId 'CompactModeButton'
            if ($null -eq $compactModeButton) {
                throw 'The compact mode button was not found.'
            }

            ([System.Windows.Automation.InvokePattern]$compactModeButton.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Start-Sleep -Milliseconds 900
        }
    }

    if ($OpenChannels) {
        $channelsButton = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.AutomationId -eq 'ChannelSwitcherButton' } |
            Select-Object -First 1
        if ($null -eq $channelsButton) {
            throw 'The channel switcher button was not found.'
        }
        $channelsButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 500
    }

    if ($OpenLogs) {
        $logsButton = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.AutomationId -eq 'ChatLogsButton' } |
            Select-Object -First 1
        if ($null -eq $logsButton) {
            throw 'The chat logs button was not found.'
        }
        $logsButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 700

        if (-not [string]::IsNullOrWhiteSpace($LogChannel)) {
            $channelList = $window.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.AutomationId -eq 'LogChannelList' } |
                Select-Object -First 1
            if ($null -eq $channelList) {
                throw 'The log channel list was not found.'
            }
            $channelItem = $channelList.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object {
                    $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
                    $_.Current.Name -eq $LogChannel
                } |
                Select-Object -First 1
            if ($null -eq $channelItem) {
                throw "The requested log channel was not found: $LogChannel"
            }
            $channelItem.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 700
        }

        if ($LogFileIndex -ge 0) {
            $fileList = $window.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.AutomationId -eq 'LogFileList' } |
                Select-Object -First 1
            if ($null -eq $fileList) {
                throw 'The log file list was not found.'
            }
            $fileItems = @($fileList.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object {
                    $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem
                })
            if ($LogFileIndex -ge $fileItems.Count) {
                throw "The requested log file index does not exist: $LogFileIndex"
            }
            $fileItems[$LogFileIndex].GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Seconds 3
            if ($process.HasExited) {
                throw 'WitherChat exited while opening the selected large log.'
            }
        }
    }

    if ($OpenModeration) {
        $moderationButton = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.AutomationId -eq 'ModerationButton' } |
            Select-Object -First 1
        if ($null -ne $moderationButton) {
            $invoke = $moderationButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
            Start-Sleep -Milliseconds 800
        }
    }

    if ($OpenSettings) {
        $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $settingsButton = Get-ButtonByAutomationIdWithRetry `
            -Condition $buttonCondition `
            -AutomationId 'SettingsButton'
        if ($null -eq $settingsButton) {
            throw 'The settings button was not found.'
        }

        ([System.Windows.Automation.InvokePattern]$settingsButton.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Milliseconds 300

        if ($SettingsSection -ne 'Program') {
            $buttons = Get-ButtonsWithRetry -Condition $buttonCondition
            $settingsNavButton = $buttons |
                Where-Object { $_.Current.AutomationId -eq ($SettingsSection + 'SettingsNav') } |
                Select-Object -First 1
            if ($null -eq $settingsNavButton) {
                throw "The $SettingsSection settings navigation button was not found."
            }

            ([System.Windows.Automation.InvokePattern]$settingsNavButton.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Start-Sleep -Milliseconds 300
        }

        if ($OpenThemeDropdown) {
            $comboCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ComboBox)
            $comboBoxes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboCondition)
            $themeComboBox = $comboBoxes |
                Where-Object { $_.Current.AutomationId -eq 'ThemeComboBox' } |
                Select-Object -First 1
            if ($null -eq $themeComboBox) {
                throw 'The theme ComboBox was not found.'
            }

            ([System.Windows.Automation.ExpandCollapsePattern]$themeComboBox.GetCurrentPattern(
                [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
            Start-Sleep -Milliseconds 300
        }
    }

    if ($OpenFilters) {
        $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $buttons = Get-ButtonsWithRetry -Condition $buttonCondition
        $filtersButton = $buttons |
            Where-Object { $_.Current.AutomationId -eq 'FiltersButton' } |
            Select-Object -First 1
        if ($null -eq $filtersButton) {
            throw 'The filters button was not found.'
        }

        ([System.Windows.Automation.InvokePattern]$filtersButton.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Milliseconds 300

        foreach ($filter in @(
            [pscustomobject]@{ Id = 'MessageSearchInput'; Value = $MessageFilter },
            [pscustomobject]@{ Id = 'UserFilterInput'; Value = $UserFilter })) {
            if ([string]::IsNullOrWhiteSpace($filter.Value)) {
                continue
            }

            $editCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit)
            $edits = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
            $filterInput = $edits |
                Where-Object { $_.Current.AutomationId -eq $filter.Id } |
                Select-Object -First 1
            if ($null -eq $filterInput) {
                throw "The $($filter.Id) input was not found."
            }

            $valuePattern = [System.Windows.Automation.ValuePattern]$filterInput.GetCurrentPattern(
                [System.Windows.Automation.ValuePattern]::Pattern)
            $valuePattern.SetValue($filter.Value)
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not [string]::IsNullOrWhiteSpace($ComposerText)) {
        $composer = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.AutomationId -eq 'ComposerInput' } |
            Select-Object -First 1
        if ($null -eq $composer) {
            throw 'The composer input was not found.'
        }
        $composer.SetFocus()
        ([System.Windows.Automation.ValuePattern]$composer.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)).SetValue($ComposerText)
        Start-Sleep -Milliseconds 300
    }

    if ($StressScroll) {
        $listCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::List)
        $messageList = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCondition)
        if ($null -eq $messageList) {
            throw 'The virtualized message list was not found.'
        }

        $scrollPattern = [System.Windows.Automation.ScrollPattern]$messageList.GetCurrentPattern(
            [System.Windows.Automation.ScrollPattern]::Pattern)
        if ($scrollPattern.Current.VerticallyScrollable) {
            1..$StressScrollCycles | ForEach-Object {
                1..12 | ForEach-Object {
                    $scrollPattern.Scroll(
                        [System.Windows.Automation.ScrollAmount]::NoAmount,
                        [System.Windows.Automation.ScrollAmount]::SmallDecrement)
                    Start-Sleep -Milliseconds 35
                }
                1..8 | ForEach-Object {
                    $scrollPattern.Scroll(
                        [System.Windows.Automation.ScrollAmount]::NoAmount,
                        [System.Windows.Automation.ScrollAmount]::SmallIncrement)
                    Start-Sleep -Milliseconds 35
                }
            }
        }
        else {
            $scrollBarCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ScrollBar)
            $scrollBars = $messageList.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $scrollBarCondition)
            $verticalScrollBar = $scrollBars |
                Where-Object { $_.Current.Orientation -eq [System.Windows.Automation.OrientationType]::Vertical } |
                Select-Object -First 1
            if ($null -eq $verticalScrollBar) {
                $listRectangle = $messageList.Current.BoundingRectangle
                $x = [int]($listRectangle.X + ($listRectangle.Width / 2))
                $y = [int]($listRectangle.Y + ($listRectangle.Height / 2))
                $lParam = [IntPtr](([long]($y -band 0xFFFF) -shl 16) -bor ($x -band 0xFFFF))
                1..$StressScrollCycles | ForEach-Object {
                    1..20 | ForEach-Object {
                        $delta = if ($_ -le 12) { 120 } else { -120 }
                        $wParam = [IntPtr]([long](($delta -band 0xFFFF) -shl 16))
                        [WitherChatNativeCapture]::SendMessage(
                            [IntPtr]$window.Current.NativeWindowHandle,
                            0x020A,
                            $wParam,
                            $lParam) | Out-Null
                        Start-Sleep -Milliseconds 35
                    }
                }
            }
            else {
                $rangePattern = [System.Windows.Automation.RangeValuePattern]$verticalScrollBar.GetCurrentPattern(
                    [System.Windows.Automation.RangeValuePattern]::Pattern)
                if ($rangePattern.Current.Maximum -le $rangePattern.Current.Minimum) {
                    throw 'The message list did not contain enough data for a scrolling test.'
                }

                $minimum = $rangePattern.Current.Minimum
                $maximum = $rangePattern.Current.Maximum
                1..$StressScrollCycles | ForEach-Object {
                    1..20 | ForEach-Object {
                        $ratio = ($_ % 10) / 10.0
                        $rangePattern.SetValue($maximum - (($maximum - $minimum) * $ratio))
                        Start-Sleep -Milliseconds 35
                    }
                }
            }
        }

        Start-Sleep -Milliseconds 250
    }

    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition)
    $window = $windows |
        Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height } -Descending |
        Select-Object -First 1
    $rectangle = $window.Current.BoundingRectangle
    $bitmap = [System.Drawing.Bitmap]::new([int]$rectangle.Width, [int]$rectangle.Height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            if ($OpenThemeDropdown) {
                $graphics.CopyFromScreen(
                    [int]$rectangle.X,
                    [int]$rectangle.Y,
                    0,
                    0,
                    $bitmap.Size)
                $captured = $true
            }
            else {
                $deviceContext = $graphics.GetHdc()
                try {
                    $captured = [WitherChatNativeCapture]::PrintWindow(
                        [IntPtr]$window.Current.NativeWindowHandle,
                        $deviceContext,
                        2)
                }
                finally {
                    $graphics.ReleaseHdc($deviceContext)
                }
            }
        }
        finally {
            $graphics.Dispose()
        }

        $directory = Split-Path -Parent $OutputPath
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    "Captured=$captured"
    "Screenshot=$OutputPath"

    if ($GracefulExit) {
        $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        if ($OpenCompact -and -not $OpenSettings) {
            [void][WitherChatNativeCapture]::SendMessage(
                [IntPtr]$window.Current.NativeWindowHandle,
                0x0010,
                [IntPtr]::Zero,
                [IntPtr]::Zero)
            if (-not $process.WaitForExit(10000)) {
                throw 'WitherChat did not finish compact-mode shutdown within 10 seconds.'
            }

            'GracefulExit=True'
            return
        }

        if (-not $OpenSettings) {
            $settingsButton = Get-ButtonByAutomationIdWithRetry `
                -Condition $buttonCondition `
                -AutomationId 'SettingsButton'
            if ($null -eq $settingsButton) {
                throw 'The settings button was not found.'
            }

            ([System.Windows.Automation.InvokePattern]$settingsButton.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Start-Sleep -Milliseconds 300
        }

        $buttons = Get-ButtonsWithRetry -Condition $buttonCondition
        $advancedButton = $buttons |
            Where-Object { $_.Current.AutomationId -eq 'AdvancedSettingsNav' } |
            Select-Object -First 1
        if ($null -eq $advancedButton) {
            throw 'The advanced settings navigation button was not found.'
        }

        ([System.Windows.Automation.InvokePattern]$advancedButton.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Milliseconds 300
        $buttons = Get-ButtonsWithRetry -Condition $buttonCondition
        $exitButton = $buttons |
            Where-Object { $_.Current.AutomationId -eq 'ExitApplicationButton' } |
            Select-Object -First 1
        if ($null -eq $exitButton) {
            throw 'The application exit button was not found.'
        }

        ([System.Windows.Automation.InvokePattern]$exitButton.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        if (-not $process.WaitForExit(10000)) {
            throw 'WitherChat did not finish graceful shutdown within 10 seconds.'
        }

        'GracefulExit=True'
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    Remove-TestDataDirectory -Path $testDataDirectory
}
