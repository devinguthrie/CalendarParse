<#
.SYNOPSIS
    Meta-agent benchmark loop: proposes targeted C# changes via Claude API,
    applies them, benchmarks, and keeps or reverts based on score delta.

.PARAMETER DryRun
    Show proposed change without applying it, then exit.

.PARAMETER MaxIterations
    Maximum number of benchmark iterations (default: 20).

.PARAMETER TargetScore
    Stop when score reaches or exceeds this value (default: 160).

.PARAMETER Model
    Ollama model for benchmarking (default: qwen2.5vl:7b).

.PARAMETER ImgDir
    Path to the test images directory.

.PARAMETER KnownNames
    Comma-separated list of known employee names.
#>
param(
    [switch]$DryRun,
    [int]$MaxIterations = 20,
    [int]$TargetScore = 245,
    [string]$Model = 'qwen2.5vl:7b',
    [string]$ImgDir = 'CalendarParse\calander-parse-test-imgs',
    [string]$KnownNames = 'Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori,Megan,Raul'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Repo root (script lives at repo root) ────────────────────────────────────
$RepoRoot = $PSScriptRoot
$DefaultBranch = (& git -C $PSScriptRoot symbolic-ref --short HEAD 2>$null) ?? 'master'
$HybridFile  = Join-Path $RepoRoot 'CalendarParse.Cli\Services\HybridCalendarService.cs'
$TriedFile   = Join-Path $RepoRoot 'tried_changes.json'
$ResultsFile = Join-Path $RepoRoot 'loop-results.md'
$ProjectPath = Join-Path $RepoRoot 'CalendarParse.Cli'

# ── 1. Load .env file if present ─────────────────────────────────────────────
$EnvFile = Join-Path $RepoRoot '.env'
if (Test-Path $EnvFile) {
    foreach ($line in (Get-Content $EnvFile -Encoding UTF8)) {
        $line = $line.Trim()
        if ($line -eq '' -or $line.StartsWith('#')) { continue }
        $idx = $line.IndexOf('=')
        if ($idx -lt 1) { continue }
        $key   = $line.Substring(0, $idx).Trim()
        $value = $line.Substring($idx + 1).Trim().Trim('"').Trim("'")
        [System.Environment]::SetEnvironmentVariable($key, $value, 'Process')
    }
    Write-Host "==> Loaded .env from $EnvFile" -ForegroundColor DarkGray
}

# ── 2. Check API key ──────────────────────────────────────────────────────────
if (-not $env:ANTHROPIC_API_KEY) {
    Write-Error 'ANTHROPIC_API_KEY not found. Set it in a .env file or as an environment variable.'
    exit 1
}

# ── 2. Build project once up-front ───────────────────────────────────────────
Write-Host '==> Building project...' -ForegroundColor Cyan
Push-Location $RepoRoot
try {
    & dotnet build $ProjectPath -c Debug --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'Initial build failed. Fix build errors before running the loop.'
        exit 1
    }
} finally {
    Pop-Location
}
Write-Host '==> Build succeeded.' -ForegroundColor Green

# ── 3. Initialize state files if they don't exist ────────────────────────────
if (-not (Test-Path $TriedFile)) {
    Set-Content -Path $TriedFile -Value '[]' -Encoding UTF8
    Write-Host "==> Initialized $TriedFile"
}

if (-not (Test-Path $ResultsFile)) {
    $header = @"
# Loop Results

| Iteration | Score Before → After | Delta | File | Rationale | Timestamp |
|-----------|---------------------|-------|------|-----------|-----------|
"@
    Set-Content -Path $ResultsFile -Value $header -Encoding UTF8
    Write-Host "==> Initialized $ResultsFile"
}

# ── Helper: run benchmark and parse score + errors ────────────────────────────
function Invoke-Benchmark {
    Push-Location $RepoRoot
    try {
        $output = & dotnet run --project $ProjectPath --no-build -- `
            $ImgDir --model $Model --test --known-names $KnownNames 2>&1
    } finally {
        Pop-Location
    }

    $outputStr = $output -join "`n"

    # Parse score line: "  Overall: N/M shifts matched (X%)"
    $scoreMatch = [regex]::Match($outputStr, 'Overall:\s+(\d+)/(\d+)')
    if (-not $scoreMatch.Success) {
        Write-Warning 'Could not parse score from benchmark output.'
        Write-Host $outputStr
        return @{ Score = 0; Total = 168; Errors = @(); RawOutput = $outputStr }
    }
    $score = [int]$scoreMatch.Groups[1].Value
    $total = [int]$scoreMatch.Groups[2].Value

    # Parse error lines: "      EmployeeName Date: got "X" expected "Y""
    $errors = @()
    foreach ($line in ($output -split "`n")) {
        $m = [regex]::Match($line, '^\s+(\S+)\s+(.+):\s+got\s+"(.+)"\s+expected\s+"(.+)"')
        if ($m.Success) {
            $errors += @{
                Employee = $m.Groups[1].Value
                Date     = $m.Groups[2].Value
                Got      = $m.Groups[3].Value
                Expected = $m.Groups[4].Value
            }
        }
    }

    return @{ Score = $score; Total = $total; Errors = $errors; RawOutput = $outputStr }
}

# ── Helper: build error list string for prompt ───────────────────────────────
function Format-ErrorList($errors) {
    if ($errors.Count -eq 0) { return '(none)' }
    $lines = @()
    foreach ($e in $errors) {
        # Classify error type heuristically
        $got      = $e.Got
        $expected = $e.Expected
        if ($got -eq '' -or $got -eq 'null') {
            $errType = 'WRONG_BLANK'
        } elseif ($expected -eq '' -or $expected -eq 'null') {
            $errType = 'SPURIOUS_VALUE'
        } else {
            $errType = 'WRONG_VALUE'
        }
        $lines += "[$errType] $($e.Employee) $($e.Date): got `"$got`" expected `"$expected`""
    }
    return $lines -join "`n"
}

# ── Helper: read tried_changes.json ──────────────────────────────────────────
function Read-TriedChanges {
    $raw = Get-Content $TriedFile -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
    try {
        return @($raw | ConvertFrom-Json)
    } catch {
        return @()
    }
}

# ── Helper: append entry to tried_changes.json ───────────────────────────────
function Append-TriedChange($entry) {
    $tried = Read-TriedChanges
    # ConvertFrom-Json returns PSCustomObject; we need an array
    if ($tried -isnot [array]) { $tried = @($tried) }
    $tried += $entry
    $tried | ConvertTo-Json -Depth 5 | Set-Content -Path $TriedFile -Encoding UTF8
}

# ── Helper: call Claude API ───────────────────────────────────────────────────
function Invoke-ClaudeApi($systemPrompt, $userPrompt) {
    $body = @{
        model      = 'claude-sonnet-4-6'
        max_tokens = 1024
        system     = $systemPrompt
        messages   = @(
            @{ role = 'user'; content = $userPrompt }
        )
    } | ConvertTo-Json -Depth 10

    $headers = @{
        'x-api-key'         = $env:ANTHROPIC_API_KEY
        'anthropic-version' = '2023-06-01'
        'content-type'      = 'application/json'
    }

    try {
        $response = Invoke-RestMethod `
            -Uri 'https://api.anthropic.com/v1/messages' `
            -Method POST `
            -Headers $headers `
            -Body $body `
            -ContentType 'application/json'
    } catch {
        $errText = "$_" + ($_.ErrorDetails?.Message ?? '')
        if ($errText -match 'credit balance is too low' -or $errText -match 'billing') {
            Write-Host ''
            Write-Host '==> OUT OF CREDITS: Add credits at console.anthropic.com/settings/billing' -ForegroundColor Red
            exit 2
        }
        if ($errText -match 'rate_limit_error' -or $errText -match 'rate limit') {
            Write-Host '  --> Rate limit hit. Waiting 65 seconds...' -ForegroundColor Yellow
            Start-Sleep -Seconds 65
            # Re-throw so the caller's retry loop handles it
        }
        throw
    }

    return $response.content[0].text
}

# ── Helper: escape unescaped double quotes inside JSON string values ──────────
# Walks the JSON character by character; any " inside a string that isn't already
# escaped gets rewritten as \". Handles already-correct JSON without changing it.
function Repair-JsonQuotes([string]$json) {
    $sb        = [System.Text.StringBuilder]::new($json.Length + 64)
    $inString  = $false
    $escaped   = $false
    for ($i = 0; $i -lt $json.Length; $i++) {
        $c = $json[$i]
        if ($escaped)           { [void]$sb.Append($c); $escaped = $false; continue }
        if ($c -eq '\')         { [void]$sb.Append($c); $escaped = $true;  continue }
        if ($c -eq '"') {
            if (-not $inString) { $inString = $true;  [void]$sb.Append($c); continue }
            # Determine if this is a legitimate closing quote by peeking ahead
            $next = $i + 1
            while ($next -lt $json.Length -and $json[$next] -match '[ \t]') { $next++ }
            $nc = if ($next -lt $json.Length) { $json[$next] } else { '}' }
            if ($nc -eq ':' -or $nc -eq ',' -or $nc -eq '}' -or $nc -eq ']') {
                $inString = $false; [void]$sb.Append($c)
            } else {
                [void]$sb.Append('\"')   # embedded quote — escape it
            }
            continue
        }
        [void]$sb.Append($c)
    }
    return $sb.ToString()
}

# ── Helper: parse ProposedChange JSON from Claude response ────────────────────
function Parse-ProposedChange($responseText) {
    # Strip markdown fences if present
    $cleaned = $responseText -replace '(?s)```(?:json)?\s*', '' -replace '```', ''
    $cleaned = $cleaned.Trim()

    # Find first { ... } block
    $start = $cleaned.IndexOf('{')
    $end   = $cleaned.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) {
        throw "No JSON object found in response: $responseText"
    }
    $json = $cleaned.Substring($start, $end - $start + 1)

    # First try: standard parse
    try { return $json | ConvertFrom-Json } catch { }

    # Fallback: repair unescaped embedded quotes then retry
    try {
        $repaired = Repair-JsonQuotes $json
        return $repaired | ConvertFrom-Json
    } catch {
        throw "Failed to parse JSON: $_`nRaw: $json"
    }
}

# ── Helper: count occurrences of a substring in a string ─────────────────────
function Count-Occurrences([string]$haystack, [string]$needle) {
    $count = 0
    $pos   = 0
    while ($true) {
        $idx = $haystack.IndexOf($needle, $pos, [System.StringComparison]::Ordinal)
        if ($idx -lt 0) { break }
        $count++
        $pos = $idx + 1
    }
    return $count
}

# ── Helper: get next tried_change id ─────────────────────────────────────────
function Get-NextTriedId {
    $tried = @(Read-TriedChanges)
    if ($tried.Count -eq 0) { return 1 }
    $maxId = (@($tried) | ForEach-Object { [int]($_.id) } | Measure-Object -Maximum).Maximum
    return $maxId + 1
}

# ── System prompt (embedded) ──────────────────────────────────────────────────
$SystemPrompt = @'
You are a code improvement agent. Your job is to propose a single, targeted change to a C# calendar parsing service to improve its shift extraction accuracy.

You will receive:
- Current accuracy score
- List of failing cells (employee, date, got, expected, error type)
- List of already-tried changes (do NOT repeat these)
- Full content of the target files

You may propose ANY type of code change — prompt strings, numeric thresholds, regex patterns,
algorithm logic, OCR boundary calculations, crop geometry, post-processing heuristics, or
C# control flow. The only constraint is that your "search" string must appear exactly once
in the target file so the replacement is unambiguous.

KNOWN STRUCTURAL ISSUES (highest-priority targets):

1. THU OCT30 X-SWAP (6 errors — Cyndee, Victor, Halle, Kyleigh, Seena, Sarah):
   ComputeDayColBoundsFromOcr() computes column boundaries as midpoints between day-header
   centers. For the Thu Oct30 column the boundary is landing inside the adjacent Wed column,
   causing the strip crop to include Wed shift cells. Investigate the boundary midpoint logic
   and the CropAndStitch() call — the left edge of the Thu strip may need to shift right.

2. CIARA LAST-ROW BLANK (3 errors — Oct29, Oct31, Nov01):
   Ciara is always the last employee row. Her row is physically near the bottom of the image
   and may be cut off by the strip crop height or by the LLM stopping one row early.
   Consider expanding the crop height for the last employee, adding explicit last-row
   instructions to the prompt, or special-casing empIdx == names.Count - 1.

3. TIME-MISREAD (6 errors — Jenny Sun, Victor Tue, Kyleigh Tue, Brittney Thu, Halle Nov28, Tori Nov23):
   The strip prompt does not explicitly tell the model that a time range must include BOTH
   start AND end (e.g. "9:00-5:30", not just "9:00" or "5:30"). Also consider whether
   TrailingHours regex is inadvertently stripping part of the time range.

4. SPURIOUS values (2 errors — Franny Oct28, Seena Oct29):
   The model is returning a non-blank value where the answer is blank or "x".
   Consider tightening the acceptance logic or adding a post-processing filter for
   values that don't match the expected format (time range, x, RTO, PTO).

NEVER propose these changes (known regressions):
- Anti-shift warnings in ExtractColumnAsync (harmful; -8 shifts)
- Anti-shift warning at TOP of rules (-27 shifts)
- Vote reweighting or additional voting passes (-8 to -63 shifts)
- ISO date keys in JSON output (-11 pts)
- CSV or pipe-separated output format (model copies examples)
- CLAHE/grayscale preprocessing (destroys red ink signal)
- Ensemble/secondary model pass (fills blanks with "x")
- Two-shot self-anchoring (-4 shifts)
- More than 5 total voting runs at temperature=0 (-8 pts)
- Numbered-column prompts (-3.4 pts)
- Drift detector threshold=1 (-8 pts)

Return ONLY valid JSON with this exact schema (no markdown, no explanation).
CRITICAL: The "search" and "replace" values often contain C# string literals with double quotes.
You MUST escape every double-quote character inside a JSON string value as \" (backslash-quote).
Failure to escape embedded double quotes produces invalid JSON that cannot be parsed.
{
  "change_type": "prompt_string | threshold | regex | algorithm | ocr_logic | crop_geometry | postprocess",
  "file": "CalendarParse.Cli/Services/HybridCalendarService.cs",
  "search": "<exact string that appears exactly once in the file>",
  "replace": "<replacement string>",
  "targets_error_type": "WRONG_VALUE | WRONG_BLANK | SPURIOUS_VALUE | EXTRA_EMPLOYEE",
  "rationale": "<one sentence>"
}
'@

# ── 4. Run initial benchmark ──────────────────────────────────────────────────
Write-Host '==> Running initial benchmark...' -ForegroundColor Cyan
$benchResult   = Invoke-Benchmark
$currentScore  = $benchResult.Score
$currentTotal  = $benchResult.Total
$currentErrors = $benchResult.Errors

$pct = [math]::Round(($currentScore / $currentTotal) * 100, 1)
Write-Host "==> Initial score: $currentScore/$currentTotal ($pct%)" -ForegroundColor Yellow

if ($currentScore -ge $TargetScore) {
    Write-Host "==> Already at or above target score ($TargetScore). Nothing to do." -ForegroundColor Green
    exit 0
}

# ── 5. Main loop ──────────────────────────────────────────────────────────────
$iteration  = 0
$wins       = 0
$maxRetries = 3

while ($iteration -lt $MaxIterations -and $currentScore -lt $TargetScore) {
    Write-Host ''
    Write-Host "==> Iteration $($iteration + 1)/$MaxIterations  (score=$currentScore/$currentTotal)" -ForegroundColor Cyan

    # ── 5a. Read file contents ────────────────────────────────────────────────
    $hybridContent = [System.IO.File]::ReadAllText($HybridFile)

    # ── 5b. Build meta-agent user prompt ─────────────────────────────────────
    $pctStr    = [math]::Round(($currentScore / $currentTotal) * 100, 1)
    $errorList = Format-ErrorList $currentErrors

    # Cap tried_changes to the last 20 entries to keep token count bounded
    $triedAll  = @(Read-TriedChanges)
    $triedRecent = if ($triedAll.Count -gt 20) { $triedAll[-20..-1] } else { $triedAll }
    $triedList = $triedRecent | ConvertTo-Json -Depth 5

    $UserPrompt = @"
Current accuracy: $currentScore/$currentTotal ($pctStr%)
Baseline: 153/168 (91.1%)
Target: 160/168 (95.2%)

FAILING CELLS:
$errorList

TRIED CHANGES — last $($triedRecent.Count) of $($triedAll.Count) (do not repeat):
$triedList

FULL CONTENT OF CalendarParse.Cli/Services/HybridCalendarService.cs:
---
$hybridContent
---

Propose ONE change targeting the most impactful error type. Return JSON only.
"@

    # ── 5c+d. Call Claude API and parse proposal (with retry) ─────────────────
    $proposal          = $null
    $retryCount        = 0
    $validProposal     = $false
    $malformedProposal = $false   # bad search string — no point retrying

    while (-not $validProposal -and -not $malformedProposal -and $retryCount -lt $maxRetries) {
        try {
            Write-Host '  --> Calling Claude API...'
            $responseText = Invoke-ClaudeApi $SystemPrompt $UserPrompt
            Write-Host "  --> Response received ($($responseText.Length) chars)"

            $proposal = Parse-ProposedChange $responseText

            # ── 5e. Validate: search must appear exactly once ─────────────────
            if (-not $proposal.search -or $proposal.search.Trim() -eq '') {
                throw 'Proposal has empty search string.'
            }

            # Determine target file
            $targetFileRelative = $proposal.file -replace '/', '\'
            $targetFile = Join-Path $RepoRoot $targetFileRelative

            if (-not (Test-Path $targetFile)) {
                throw "Target file not found: $targetFile"
            }

            $fileContent = [System.IO.File]::ReadAllText($targetFile)
            $occurrences = Count-Occurrences $fileContent $proposal.search

            if ($occurrences -ne 1) {
                # Malformed search string — log and bail immediately, no retries.
                # Retrying wastes API calls: Claude would need the corrected file
                # content to produce a valid search string anyway.
                $searchPreview = $proposal.search.Substring(0, [Math]::Min(60, $proposal.search.Length))
                $entry = [ordered]@{
                    id             = Get-NextTriedId
                    change_type    = $proposal.change_type
                    file           = $proposal.file
                    search_preview = $searchPreview
                    rationale      = $proposal.rationale
                    score_before   = $currentScore
                    score_after    = $currentScore
                    outcome        = 'malformed_proposal'
                    timestamp      = (Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ')
                }
                Append-TriedChange $entry
                Write-Warning "  --> Search string appears $occurrences time(s) (must be 1). Logged as malformed_proposal — skipping without retry."
                $malformedProposal = $true
                break
            }

            $validProposal = $true
        } catch {
            $errText = "$_" + ($_.ErrorDetails?.Message ?? '')
            if ($errText -match 'credit balance is too low' -or $errText -match 'billing') {
                Write-Host ''
                Write-Host '==> OUT OF CREDITS: Add credits at console.anthropic.com/settings/billing' -ForegroundColor Red
                exit 2
            }
            Write-Warning "  --> Proposal attempt $($retryCount + 1) failed: $_"
            $retryCount++
            if ($retryCount -ge $maxRetries) {
                Write-Warning '  --> Max retries reached for this iteration. Skipping (not counted against MaxIterations).'
                break
            }
            Write-Host "  --> Retrying proposal request ($retryCount/$maxRetries)..."
        }
    }

    if ($malformedProposal) {
        # Count against MaxIterations so we don't spin forever on a stale file
        $iteration++
        continue
    }

    if (-not $validProposal) {
        Write-Warning '  --> Could not obtain valid proposal after retries. Continuing to next iteration without counting this one.'
        continue  # Do not increment $iteration
    }

    # ── 5f. DryRun: print and exit ────────────────────────────────────────────
    if ($DryRun) {
        Write-Host ''
        Write-Host '==> DRY RUN — proposed change:' -ForegroundColor Yellow
        Write-Host "  change_type : $($proposal.change_type)"
        Write-Host "  file        : $($proposal.file)"
        Write-Host "  targets     : $($proposal.targets_error_type)"
        Write-Host "  rationale   : $($proposal.rationale)"
        Write-Host ''
        Write-Host '  SEARCH:'
        Write-Host $proposal.search
        Write-Host ''
        Write-Host '  REPLACE:'
        Write-Host $proposal.replace
        exit 0
    }

    # ── 5g+h. Git branch setup ────────────────────────────────────────────────
    $branchName = "improvement-loop-$($iteration + 1)"

    Push-Location $RepoRoot
    try {
        # Pre-delete branch if it exists
        $existingBranch = & git branch --list $branchName
        if ($existingBranch) {
            Write-Host "  --> Pre-deleting existing branch: $branchName"
            & git branch -D $branchName 2>&1 | Out-Null
        }

        Write-Host "  --> Creating branch: $branchName"
        & git checkout -b $branchName
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create branch $branchName"
        }
    } catch {
        Pop-Location
        Write-Warning "  --> Git branch error: $_. Skipping iteration."
        $iteration++
        continue
    }

    # ── 5i. Apply change (exact string replacement) ───────────────────────────
    $applyError = $null
    try {
        $targetFileRelative = $proposal.file -replace '/', '\'
        $targetFile = Join-Path $RepoRoot $targetFileRelative
        $originalContent = [System.IO.File]::ReadAllText($targetFile)
        $newContent = $originalContent.Replace($proposal.search, $proposal.replace)
        [System.IO.File]::WriteAllText($targetFile, $newContent, [System.Text.Encoding]::UTF8)
        Write-Host '  --> Change applied to file.'
    } catch {
        $applyError = $_
    }

    if ($applyError) {
        Write-Warning "  --> Failed to apply change: $applyError"
        & git checkout $DefaultBranch
        & git branch -D $branchName 2>&1 | Out-Null
        Pop-Location
        $searchPreview = $proposal.search.Substring(0, [Math]::Min(60, $proposal.search.Length))
        $entry = [ordered]@{
            id             = Get-NextTriedId
            change_type    = $proposal.change_type
            file           = $proposal.file
            search_preview = $searchPreview
            rationale      = $proposal.rationale
            score_before   = $currentScore
            score_after    = $currentScore
            outcome        = 'malformed_proposal'
            timestamp      = (Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ')
        }
        Append-TriedChange $entry
        $iteration++
        continue
    }

    # ── 5j. Build after change ────────────────────────────────────────────────
    Write-Host '  --> Building after change...'
    & dotnet build $ProjectPath -c Debug --nologo
    $buildOk = ($LASTEXITCODE -eq 0)

    if (-not $buildOk) {
        Write-Warning '  --> Build failed after change. Reverting.'
        & git checkout $DefaultBranch
        & git branch -D $branchName 2>&1 | Out-Null
        Pop-Location
        $searchPreview = $proposal.search.Substring(0, [Math]::Min(60, $proposal.search.Length))
        $entry = [ordered]@{
            id             = Get-NextTriedId
            change_type    = $proposal.change_type
            file           = $proposal.file
            search_preview = $searchPreview
            rationale      = $proposal.rationale
            score_before   = $currentScore
            score_after    = $currentScore
            outcome        = 'malformed_proposal'
            timestamp      = (Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ')
        }
        Append-TriedChange $entry
        $iteration++
        continue
    }

    # ── 5k. Run benchmark → new score ────────────────────────────────────────
    Write-Host '  --> Running benchmark with change applied...'
    $newResult = Invoke-Benchmark
    $newScore  = $newResult.Score
    $newPct    = [math]::Round(($newScore / $newResult.Total) * 100, 1)
    $delta     = $newScore - $currentScore
    Write-Host "  --> Score: $currentScore → $newScore ($newPct%)  delta=$delta"

    $timestamp = Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ'
    $searchPreview = $proposal.search.Substring(0, [Math]::Min(60, $proposal.search.Length))

    # ── 5l. Compare and act ───────────────────────────────────────────────────
    if ($newScore -gt $currentScore) {
        # WIN: merge into master
        Write-Host "  --> IMPROVEMENT! Merging $branchName into master." -ForegroundColor Green
        & git checkout $DefaultBranch
        $commitMsg = "loop: $branchName`: $currentScore->$newScore (+$delta) -- $($proposal.rationale)"
        & git merge --no-ff $branchName -m $commitMsg
        & git branch -D $branchName 2>&1 | Out-Null
        Pop-Location

        # Append win row to loop-results.md
        $mdRow = "| $($iteration + 1) | $currentScore/$($newResult.Total) → $newScore/$($newResult.Total) | +$delta | $($proposal.file) | $($proposal.rationale) | $timestamp |"
        Add-Content -Path $ResultsFile -Value $mdRow -Encoding UTF8
        Write-Host "  --> loop-results.md updated." -ForegroundColor Green

        $currentScore  = $newScore
        $currentErrors = $newResult.Errors
        $wins++
    } else {
        # NO IMPROVEMENT: revert by deleting branch (master unchanged)
        Write-Host "  --> No improvement (delta=$delta). Reverting." -ForegroundColor Yellow
        & git checkout $DefaultBranch
        & git branch -D $branchName 2>&1 | Out-Null
        Pop-Location

        # Log to tried_changes.json
        $entry = [ordered]@{
            id             = Get-NextTriedId
            change_type    = $proposal.change_type
            file           = $proposal.file
            search_preview = $searchPreview
            rationale      = $proposal.rationale
            score_before   = $currentScore
            score_after    = $newScore
            outcome        = 'reverted'
            timestamp      = $timestamp
        }
        Append-TriedChange $entry
    }

    $iteration++
}

# ── 6. Exit summary ───────────────────────────────────────────────────────────
Write-Host ''
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host "==> Loop complete." -ForegroundColor Cyan
Write-Host "    Iterations : $iteration"
Write-Host "    Wins       : $wins"
$finalPct = [math]::Round(($currentScore / $currentTotal) * 100, 1)
Write-Host "    Final score: $currentScore/$currentTotal ($finalPct%)" -ForegroundColor Yellow
Write-Host '======================================================' -ForegroundColor Cyan

if ($wins -eq 0 -and $iteration -ge $MaxIterations) {
    $note = "`n> **RANDOM_RESIDUAL**: $MaxIterations iterations yielded zero wins. Errors may be irreducible with prompt-only changes."
    Add-Content -Path $ResultsFile -Value $note -Encoding UTF8
    Write-Host '==> RANDOM_RESIDUAL note written to loop-results.md.' -ForegroundColor Red
}

if ($currentScore -ge $TargetScore) {
    Write-Host "==> Target score of $TargetScore reached!" -ForegroundColor Green
    exit 0
} else {
    exit 0
}
