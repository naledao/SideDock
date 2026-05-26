param(
    [int] $DurationSeconds = 60,
    [string] $HostProject = (Join-Path $PSScriptRoot "..\windows-host\SideDock.Host\SideDock.Host.csproj"),
    [string] $ArtifactRoot = (Join-Path $PSScriptRoot "..\artifacts\validation"),
    [string] $Adb = "adb",
    [string] $VideoSource = "idd-gpu",
    [string] $Resolution = "2k",
    [int] $RefreshRate = 120,
    [int] $BitrateMbps = 64,
    [switch] $SkipAndroidRestart,
    [switch] $SkipHostKill,
    [switch] $DisableDynamicWindow
)

$ErrorActionPreference = "Stop"

function New-ValidationDirectory {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $safeSource = $VideoSource -replace "[^a-zA-Z0-9\-]", "-"
    $safeResolution = $Resolution -replace "[^a-zA-Z0-9\-]", "-"
    $name = "$safeSource-$safeResolution-$($RefreshRate)hz-$stamp"
    $path = Join-Path $ArtifactRoot $name
    New-Item -ItemType Directory -Force $path | Out-Null
    return (Resolve-Path $path).Path
}

function Start-DynamicTestWindow {
    param([int] $LifetimeSeconds)

    if ($DisableDynamicWindow) {
        return $null
    }

    $windowScript = @'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$screens = [System.Windows.Forms.Screen]::AllScreens
$target = $screens | Where-Object { -not $_.Primary } | Sort-Object { $_.Bounds.Width * $_.Bounds.Height } -Descending | Select-Object -First 1
if ($null -eq $target) {
    $target = $screens[0]
}

$bounds = $target.Bounds
$form = New-Object System.Windows.Forms.Form
$form.Text = "SideDock Dynamic Validation"
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.Bounds = $bounds
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::Black
$form.DoubleBuffered = $true
$form.KeyPreview = $true

$start = [DateTime]::UtcNow
$duration = [TimeSpan]::FromSeconds(__DURATION_SECONDS__)
$titleFont = New-Object System.Drawing.Font("Segoe UI", 30, [System.Drawing.FontStyle]::Bold)
$monoFont = New-Object System.Drawing.Font("Consolas", 18, [System.Drawing.FontStyle]::Regular)

$form.Add_KeyDown({
    param($sender, $event)
    if ($event.KeyCode -eq [System.Windows.Forms.Keys]::Escape) {
        $sender.Close()
    }
})

$form.Add_Paint({
    param($sender, $event)
    $elapsed = ([DateTime]::UtcNow - $start).TotalSeconds
    $graphics = $event.Graphics
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $width = [Math]::Max(1, $sender.ClientSize.Width)
    $height = [Math]::Max(1, $sender.ClientSize.Height)
    $graphics.Clear([System.Drawing.Color]::FromArgb(10, 12, 16))

    for ($i = 0; $i -lt 22; $i++) {
        $phase = $elapsed * (1.3 + ($i % 5) * 0.23) + $i * 0.41
        $x = [int](($width - 160) * (0.5 + 0.5 * [Math]::Sin($phase)))
        $y = [int](($height - 120) * (0.5 + 0.5 * [Math]::Cos($phase * 0.73)))
        $size = 36 + (($i * 17) % 96)
        $color = [System.Drawing.Color]::FromArgb(
            210,
            (40 + $i * 37) % 255,
            (120 + $i * 53) % 255,
            (210 + $i * 29) % 255)
        $brush = New-Object System.Drawing.SolidBrush($color)
        $graphics.FillEllipse($brush, $x, $y, $size, $size)
        $brush.Dispose()
    }

    $barBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 255, 255, 255))
    for ($i = 0; $i -lt 16; $i++) {
        $barY = [int](($height / 16) * $i)
        $barW = [int]($width * (0.18 + 0.78 * (0.5 + 0.5 * [Math]::Sin($elapsed * 3.4 + $i))))
        $graphics.FillRectangle($barBrush, 0, $barY, $barW, 5)
    }
    $barBrush.Dispose()

    $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 80, 220, 170))
    $graphics.DrawString("SideDock dynamic validation", $titleFont, $textBrush, 40, 36)
    $graphics.DrawString(("target={0}x{1} elapsed={2:n1}s" -f $width, $height, $elapsed), $monoFont, $accentBrush, 44, 92)
    $graphics.DrawString((Get-Date -Format "HH:mm:ss.fff"), $monoFont, $textBrush, 44, 128)
    $textBrush.Dispose()
    $accentBrush.Dispose()
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 8
$timer.Add_Tick({
    if (([DateTime]::UtcNow - $start) -ge $duration) {
        $timer.Stop()
        $form.Close()
        return
    }

    $form.Invalidate()
})
$form.Add_Shown({ $timer.Start() })
[System.Windows.Forms.Application]::Run($form)
'@

    $windowScript = $windowScript.Replace("__DURATION_SECONDS__", [string][Math]::Max(5, $LifetimeSeconds))
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($windowScript))
    return Start-Process -FilePath "powershell" -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encoded) -PassThru
}

function Stop-OldHost {
    if ($SkipHostKill) {
        return
    }

    Get-CimInstance Win32_Process |
        Where-Object {
            $_.CommandLine -like "*SideDock.Host*" -or
            $_.CommandLine -like "*$HostProject*"
        } |
        ForEach-Object {
            try {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
            } catch {
                Write-Warning "Failed to stop old host PID $($_.ProcessId): $($_.Exception.Message)"
            }
        }
}

function Invoke-Adb {
    param([string[]] $AdbArgs)

    & $Adb @AdbArgs
}

function Try-ReadMetric {
    param(
        [string] $Text,
        [string] $Pattern
    )

    $match = [regex]::Match($Text, $Pattern)
    if ($match.Success) {
        for ($index = 1; $index -lt $match.Groups.Count; $index++) {
            if ($match.Groups[$index].Success -and $match.Groups[$index].Value.Length -gt 0) {
                return $match.Groups[$index].Value
            }
        }
    }

    return $null
}

function Try-ReadLastMetric {
    param(
        [string] $Text,
        [string] $Pattern
    )

    $matches = [regex]::Matches($Text, $Pattern)
    if ($matches.Count -gt 0) {
        $match = $matches[$matches.Count - 1]
        for ($index = 1; $index -lt $match.Groups.Count; $index++) {
            if ($match.Groups[$index].Success -and $match.Groups[$index].Value.Length -gt 0) {
                return $match.Groups[$index].Value
            }
        }
    }

    return $null
}

function Try-ReadAdaptiveEvents {
    param([string] $Text)

    $events = @()
    foreach ($match in [regex]::Matches($Text, "adaptive fps (\d+)->(\d+) reason=([a-zA-Z0-9_\-]+)")) {
        $events += [ordered]@{
            fromFps = [int]$match.Groups[1].Value
            toFps = [int]$match.Groups[2].Value
            reason = $match.Groups[3].Value
        }
    }

    return $events
}

function Get-ValidationStatus {
    param(
        [string] $HostText,
        [string] $LogcatText
    )

    if ($HostText -match "decoder unsupported; fallback video mode") {
        if ($HostText -match "rendered=[1-9]\d*") {
            return [ordered]@{
                status = "degraded"
                reason = "android_decoder_unsupported_recovered"
            }
        }

        return [ordered]@{
            status = "blocked"
            reason = "android_decoder_unsupported"
        }
    }

    if ($LogcatText -match "H/W is overloaded|OMX_ErrorInsufficientResources|This session is not supported|InsufficientResources") {
        if ($HostText -match "decoder unsupported; fallback video mode" -and $HostText -match "rendered=[1-9]\d*") {
            return [ordered]@{
                status = "degraded"
                reason = "android_decoder_unsupported_recovered"
            }
        }

        return [ordered]@{
            status = "blocked"
            reason = "android_decoder_unsupported"
        }
    }

    if ($HostText -match "ENCODER_CAPTURE_FAILED|ENCODER_FAILED|GPU_PIPELINE_TRANSIENT") {
        if ($HostText -match "stats generated=\d+") {
            return [ordered]@{
                status = "degraded"
                reason = "host_gpu_pipeline_error"
            }
        }

        return [ordered]@{
            status = "blocked"
            reason = "host_gpu_pipeline_error"
        }
    }

    if ($HostText -match "stats generated=\d+" -and $HostText -match "decodeErrors=[1-9]\d*|reconnects=[1-9]\d*") {
        return [ordered]@{
            status = "degraded"
            reason = "android_video_reconnects"
        }
    }

    if ($HostText -match "stats generated=\d+") {
        return [ordered]@{
            status = "ok"
            reason = "metrics_collected"
        }
    }

    if ($HostText -match "waiting for Idd GPU shared texture ring") {
        return [ordered]@{
            status = "blocked"
            reason = "idd_gpu_ring_unavailable"
        }
    }

    if ($HostText -match "SideDock Virtual Display layout unavailable|SideDock display layout is not available") {
        return [ordered]@{
            status = "blocked"
            reason = "sidedock_display_layout_unavailable"
        }
    }

    if ($HostText -match "\[CONN \d+\]|\[VIDEO \d+\]|control channel|video channel") {
        return [ordered]@{
            status = "blocked"
            reason = "connected_without_metrics"
        }
    }

    if ($LogcatText -match "No activities found to run|Unable to resolve Intent") {
        return [ordered]@{
            status = "blocked"
            reason = "android_app_not_installed_or_not_launchable"
        }
    }

    return [ordered]@{
        status = "blocked"
        reason = "android_not_connected"
    }
}

$artifactDir = New-ValidationDirectory
$hostLog = Join-Path $artifactDir "host.log"
$hostErr = Join-Path $artifactDir "host.err.log"
$logcatPath = Join-Path $artifactDir "adb-logcat.log"
$summaryPath = Join-Path $artifactDir "summary.json"
$screenshotPath = Join-Path $artifactDir "android-screenshot.png"

Stop-OldHost

Invoke-Adb -AdbArgs @("reverse", "tcp:27183", "tcp:27183") | Out-Null
Invoke-Adb -AdbArgs @("reverse", "tcp:27184", "tcp:27184") | Out-Null

if (-not $SkipAndroidRestart) {
    Invoke-Adb -AdbArgs @("shell", "am", "force-stop", "com.sidedock.client") | Out-Null
}

Invoke-Adb -AdbArgs @("logcat", "-c") | Out-Null
$logcatProcess = Start-Process -FilePath $Adb -ArgumentList @("logcat", "-v", "time") -RedirectStandardOutput $logcatPath -PassThru -WindowStyle Hidden

$hostArgs = @(
    "run",
    "--project",
    (Resolve-Path $HostProject).Path,
    "--",
    "--video-source",
    $VideoSource,
    "--resolution",
    $Resolution,
    "--refresh-rate",
    [string]$RefreshRate,
    "--bitrate",
    "$($BitrateMbps)M"
)

$hostProcess = Start-Process -FilePath "dotnet" -ArgumentList $hostArgs -RedirectStandardOutput $hostLog -RedirectStandardError $hostErr -PassThru -WindowStyle Hidden
$dynamicWindowProcess = $null

try {
    Start-Sleep -Seconds 3
    $dynamicWindowProcess = Start-DynamicTestWindow -LifetimeSeconds ($DurationSeconds + 8)
    if (-not $SkipAndroidRestart) {
        Invoke-Adb -AdbArgs @("shell", "monkey", "-p", "com.sidedock.client", "1") | Out-Null
    }

    Start-Sleep -Seconds $DurationSeconds

    try {
        Invoke-Adb -AdbArgs @("exec-out", "screencap", "-p") > $screenshotPath
    } catch {
        Write-Warning "Failed to capture Android screenshot: $($_.Exception.Message)"
    }
}
finally {
    if ($dynamicWindowProcess -and -not $dynamicWindowProcess.HasExited) {
        Stop-Process -Id $dynamicWindowProcess.Id -Force
        $dynamicWindowProcess.WaitForExit()
    }

    if ($hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -Force
        $hostProcess.WaitForExit()
    }

    if ($logcatProcess -and -not $logcatProcess.HasExited) {
        Stop-Process -Id $logcatProcess.Id -Force
        $logcatProcess.WaitForExit()
    }
}

$hostText = if (Test-Path $hostLog) { Get-Content -Raw -LiteralPath $hostLog } else { "" }
$logcatText = if (Test-Path $logcatPath) { Get-Content -Raw -LiteralPath $logcatPath } else { "" }
$validation = Get-ValidationStatus $hostText $logcatText

$summary = [ordered]@{
    artifactDir = $artifactDir
    durationSeconds = $DurationSeconds
    videoSource = $VideoSource
    resolution = $Resolution
    refreshRate = $RefreshRate
    bitrateMbps = $BitrateMbps
    validationStatus = $validation.status
    validationReason = $validation.reason
    dynamicWindow = -not $DisableDynamicWindow.IsPresent
    displayMode = Try-ReadLastMetric $hostText "mode=(\d+x\d+@\d+)|startup display mode (\d+x\d+@\d+)"
    encoder = Try-ReadLastMetric $hostText "encoder=([a-zA-Z0-9\-_]+)"
    captureFps = Try-ReadLastMetric $hostText "captureFps=([0-9.]+)"
    convertFps = Try-ReadLastMetric $hostText "convertFps=([0-9.]+)"
    avgAcquireMs = Try-ReadLastMetric $hostText "avgAcquire=([0-9.]+)ms|avgCapture=([0-9.]+)ms"
    p95AcquireMs = Try-ReadLastMetric $hostText "p95Acquire=([0-9.]+)ms|p95Capture=([0-9.]+)ms"
    avgConvertMs = Try-ReadLastMetric $hostText "avgConvert=([0-9.]+)ms"
    p95ConvertMs = Try-ReadLastMetric $hostText "p95Convert=([0-9.]+)ms"
    streamFps = Try-ReadLastMetric $hostText "streamFps=([0-9.]+)"
    newFramesSentWindow = Try-ReadLastMetric $hostText "streamFps=[0-9.]+ new=(\d+)"
    repeatFramesSentWindow = Try-ReadLastMetric $hostText "streamFps=[0-9.]+ new=\d+ repeat=(\d+)"
    avgEncodeMs = Try-ReadLastMetric $hostText "avgEncode=([0-9.]+)ms"
    p95EncodeMs = Try-ReadLastMetric $hostText "p95Encode=([0-9.]+)ms"
    p99EncodeMs = Try-ReadLastMetric $hostText "p99Encode=([0-9.]+)ms"
    maxEncodeMs = Try-ReadLastMetric $hostText "maxEncode=([0-9.]+)ms"
    avgSendMs = Try-ReadLastMetric $hostText "avgSend=([0-9.]+)ms"
    p95SendMs = Try-ReadLastMetric $hostText "p95Send=([0-9.]+)ms"
    outputKbps = Try-ReadLastMetric $hostText "kbps=([0-9.]+)"
    lastDecoded = Try-ReadLastMetric $hostText "decoded=(\d+)"
    lastRendered = Try-ReadLastMetric $hostText "rendered=(\d+)"
    renderFps = Try-ReadLastMetric $hostText "render=([0-9.]+)"
    newFrameFps = Try-ReadLastMetric $hostText "fps decode=[0-9.]+ render=[0-9.]+ new=([0-9.]+)"
    repeatFrameFps = Try-ReadLastMetric $hostText "fps decode=[0-9.]+ render=[0-9.]+ new=[0-9.]+ repeat=([0-9.]+)"
    p95AndroidQueueOutputMs = Try-ReadLastMetric $hostText "androidP95 queueOutput=([0-9.]+)ms"
    p95AndroidOutputRenderMs = Try-ReadLastMetric $hostText "androidP95 queueOutput=[0-9.]+ms outputRender=([0-9.]+)ms"
    p95AndroidQueueRenderMs = Try-ReadLastMetric $hostText "androidP95 queueOutput=[0-9.]+ms outputRender=[0-9.]+ms queueRender=([0-9.]+)ms"
    p99AndroidQueueOutputMs = Try-ReadLastMetric $hostText "androidP99 queueOutput=([0-9.]+)ms"
    p99AndroidOutputRenderMs = Try-ReadLastMetric $hostText "androidP99 queueOutput=[0-9.]+ms outputRender=([0-9.]+)ms"
    p99AndroidQueueRenderMs = Try-ReadLastMetric $hostText "androidP99 queueOutput=[0-9.]+ms outputRender=[0-9.]+ms queueRender=([0-9.]+)ms"
    lastFrameKind = Try-ReadLastMetric $hostText "kind=([a-zA-Z0-9_\-]+)"
    lastSourceAgeMs = Try-ReadLastMetric $hostText "sourceAge=([0-9.]+)ms"
    decodeErrors = Try-ReadLastMetric $hostText "decodeErrors=(\d+)"
    videoReconnects = Try-ReadLastMetric $hostText "reconnects=(\d+)"
    adaptiveEvents = @(Try-ReadAdaptiveEvents $hostText)
    androidDecodeErrors = Try-ReadLastMetric $logcatText "decodeErrors[=: ]+(\d+)"
    androidReconnects = Try-ReadLastMetric $logcatText "videoReconnects[=: ]+(\d+)"
    files = [ordered]@{
        hostLog = $hostLog
        hostErr = $hostErr
        adbLogcat = $logcatPath
        screenshot = $screenshotPath
    }
}

$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host "2K@120 validation written to $artifactDir"
Write-Host "Summary: $summaryPath"

