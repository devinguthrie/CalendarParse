#!/usr/bin/env pwsh
# benchmark.ps1 — runs the preprocessing A/B test matrix
#
# Usage:
#   .\benchmark.ps1
#   .\benchmark.ps1 -Models qwen2.5vl:7b,llama3.2-vision:11b
#   .\benchmark.ps1 -Modes none,clahe,llm
#   .\benchmark.ps1 -Runs 3           # number of runs per cell (averaged)
#   .\benchmark.ps1 -ResizeWidth 1120  # resize images to 1120px before the model (P5)
#   .\benchmark.ps1 -Halves            # also run header+bottom-half composite pass (P4)
#
# Results are printed as a markdown-style table and saved to benchmark-results.txt

param(
    [string[]] $Models = @("qwen2.5vl:7b", "llama3.2-vision:11b", "llava:13b"),
    [string[]] $Modes  = @("none", "current", "clahe", "llm", "denoise"),
    [int]      $Runs   = 1,
    [string]   $ImgDir = "CalendarParse\calander-parse-test-imgs",
    [string]   $OutFile = "benchmark-results.txt",
    # Delete all generated files from the image folder before running (answer.json and source images are preserved)
    [switch]   $Clean,
    # Resize images to this width before sending to the model (0 = no resize). Tests P5 resolution normalisation.
    [int]      $ResizeWidth = 0,
    # Also run extraction on a header+bottom-half composite and merge results. Tests P4 image-halves.
    [switch]   $Halves,
    # Comma-separated list of known employee names passed via --known-names to the CLI.
    [string]   $KnownNames = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

$ProjectDir = Join-Path $PSScriptRoot "CalendarParse.Cli"

# ── helper: run one combination, return the "N/M (X%)" score string ──────────
function Run-Benchmark([string]$model, [string]$mode) {
    $scores = @()
    for ($r = 1; $r -le $Runs; $r++) {
        $label = "$model / $mode" + $(if ($Runs -gt 1) { " (run $r/$Runs)" } else { "" })
        $startTime = Get-Date
        Write-Host "  Running $label ..." -ForegroundColor Cyan
        Write-Host "  Started : $($startTime.ToString('HH:mm:ss'))" -ForegroundColor DarkGray
        Write-Host ""

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $extraArgs = @()
        if ($ResizeWidth -gt 0) { $extraArgs += "--resize"; $extraArgs += $ResizeWidth }
        if ($Halves)            { $extraArgs += "--halves" }
        if ($KnownNames)        { $extraArgs += "--known-names"; $extraArgs += $KnownNames }

        $rawLines = [System.Collections.Generic.List[string]]::new()
        & dotnet run --project $ProjectDir --no-build -- `
            $ImgDir --vision --test --model $model --preprocess $mode @extraArgs 2>&1 | ForEach-Object {
            $line = $_.ToString()
            $rawLines.Add($line)
            Write-Host "    $line" -ForegroundColor DarkGray
        }
        $sw.Stop()
        $elapsed = "$($sw.Elapsed.TotalSeconds.ToString('F0'))s"
        $finishTime = Get-Date
        Write-Host ""

        # Brief cooldown to let Ollama release GPU memory between runs
        Start-Sleep -Seconds 5

        $scoreLine = $rawLines | Select-String "Overall:" | Select-Object -Last 1
        if ($scoreLine) {
            $score = ($scoreLine -replace ".*Overall:\s*", "").Trim()
            $scores += $score
            Write-Host "  Finished: $($finishTime.ToString('HH:mm:ss'))  Score: $score  [total: $elapsed]" -ForegroundColor Green
        } else {
            $scores += "FAILED"
            Write-Host "  Finished: $($finishTime.ToString('HH:mm:ss'))  FAILED  [total: $elapsed]" -ForegroundColor Red
        }
        Write-Host ""
    }
    # Return single score or "avg(r1,r2,...)" when multiple runs
    if ($Runs -eq 1) { return $scores[0] }
    return "avg: " + ($scores -join " | ")
}

# ── Build once up-front ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== CalendarParse Preprocessing Benchmark ===" -ForegroundColor Cyan
Write-Host "  Models : $($Models -join ', ')"
Write-Host "  Modes  : $($Modes  -join ', ')"
Write-Host "  Runs   : $Runs per cell"
if ($ResizeWidth -gt 0) { Write-Host "  Resize : ${ResizeWidth}px" }
if ($Halves)            { Write-Host "  Halves : enabled" }
Write-Host "  Images : $ImgDir"
Write-Host ""

# ── Clean generated files from the image folder before running ─────────────────
if ($Clean) {
    Write-Host "Cleaning generated files from $ImgDir ..." -NoNewline
    $imgFullPath = Join-Path $PSScriptRoot $ImgDir
    # Remove everything that is NOT a source image or answer/csv file
    Get-ChildItem $imgFullPath -File | Where-Object {
        $_.Name -notmatch '^IM \(\d+\)\.(jpg|jpeg|png|answer\.json)$' -and
        $_.Name -ne 'IM (1)-whole-table.csv'
    } | Remove-Item -Force
    # Remove the preprocess-debug subdirectory entirely
    $debugDir = Join-Path $imgFullPath "preprocess-debug"
    if (Test-Path $debugDir) { Remove-Item $debugDir -Recurse -Force }
    Write-Host " done" -ForegroundColor Green
    Write-Host ""
}
Write-Host "Building project..." -NoNewline
$buildOut = dotnet build $ProjectDir -c Debug --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host " FAILED" -ForegroundColor Red
    $buildOut | Write-Host
    exit 1
}
Write-Host " OK" -ForegroundColor Green
Write-Host ""

# ── Run matrix ────────────────────────────────────────────────────────────────
$results = @{}  # key: "$model|$mode" => score string

foreach ($model in $Models) {
    Write-Host "── Model: $model ─────────────────────────────" -ForegroundColor Yellow
    foreach ($mode in $Modes) {
        $key = "$model|$mode"
        $results[$key] = Run-Benchmark $model $mode
    }
    Write-Host ""
}

# ── Print comparison table ────────────────────────────────────────────────────
$colW = 32
$modeW = 10
$scoreW = 28

$header  = ("| {0,-$colW} | {1,-$modeW} | {2,-$scoreW} |" -f "Model", "Preprocess", "Score")
$divider = "|" + ("-" * ($colW + 2)) + "|" + ("-" * ($modeW + 2)) + "|" + ("-" * ($scoreW + 2)) + "|"

$lines = @()
$lines += ""
$lines += "=== PREPROCESSING BENCHMARK RESULTS ==="
$lines += "  $(Get-Date -Format 'yyyy-MM-dd HH:mm')  |  $Runs run(s) per cell"
$lines += ""
$lines += $header
$lines += $divider

foreach ($model in $Models) {
    foreach ($mode in $Modes) {
        $key   = "$model|$mode"
        $score = $results.ContainsKey($key) ? $results[$key] : "—"
        $lines += ("| {0,-$colW} | {1,-$modeW} | {2,-$scoreW} |" -f $model, $mode, $score)
    }
    $lines += $divider
}

$lines | ForEach-Object { Write-Host $_ }
Write-Host ""

# ── Save to file ──────────────────────────────────────────────────────────────
$outPath = Join-Path $PSScriptRoot $OutFile
$lines | Set-Content -Path $outPath -Encoding UTF8
Write-Host "Results saved to: $outPath" -ForegroundColor Green
Write-Host ""
