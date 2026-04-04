param(
    [ValidateSet("mirror", "screenshot", "record")]
    [string]$Action = "mirror",
    [string]$DeviceId = "",
    [string]$OutputDir = "BenchmarkResults/device-captures",
    [string]$Tag = "overlay"
)

$ErrorActionPreference = "Stop"

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' not found in PATH."
    }
}

Require-Command adb
Require-Command scrcpy

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$base = Join-Path $OutputDir "$Tag-$stamp"

$adbArgs = @()
$scrcpyArgs = @()
if ($DeviceId -ne "") {
    $adbArgs += "-s"
    $adbArgs += $DeviceId
    $scrcpyArgs += "-s"
    $scrcpyArgs += $DeviceId
}

switch ($Action) {
    "mirror" {
        Write-Host "Starting interactive mirror session..."
        & scrcpy @scrcpyArgs
    }
    "screenshot" {
        $remote = "/sdcard/$Tag-$stamp.png"
        $local = "$base.png"
        Write-Host "Capturing screenshot to $local"
        & adb @adbArgs shell screencap -p $remote
        & adb @adbArgs pull $remote $local
        & adb @adbArgs shell rm $remote
        Write-Host "Saved: $local"
    }
    "record" {
        $local = "$base.mp4"
        Write-Host "Recording screen to $local"
        & scrcpy @scrcpyArgs --record=$local
        Write-Host "Saved: $local"
    }
}
