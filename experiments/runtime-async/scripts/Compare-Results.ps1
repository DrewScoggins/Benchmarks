<#
.SYNOPSIS
    Aggregates the multi-iteration, three-arm crank results produced by
    Invoke-RuntimeAsyncBenchmarks.ps1 and reports medians with deltas.

.DESCRIPTION
    Reads out\<runId>\<scenario>-<arm>-i<N>.json, groups by (scenario, metric, arm),
    and reports the median across iterations together with the observed spread.

    Two comparisons are printed for each metric:

      all-off -> all-on    the full effect of the feature across runtime, aspnetcore
                           and the app. This is the headline number.
      app-off -> all-on    the app-only effect, holding the framework runtime-async ON.
                           This is what the first round of the experiment measured.

    Median is used rather than mean because crank runs occasionally produce a single
    badly skewed sample (a slow start, a noisy neighbour) that would drag a mean around.
    The Spread column shows (max-min)/median so it is obvious when a delta is smaller
    than the run-to-run noise.

.EXAMPLE
    .\Compare-Results.ps1 -RunId 20260730-142530

.EXAMPLE
    .\Compare-Results.ps1 -Csv results.csv
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot 'out'),

    # Which run folder under OutDir to read. Defaults to the newest.
    [string] $RunId,

    [string[]] $Scenario,

    # Write the flattened per-iteration rows here.
    [string] $Csv,

    # Write the aggregated (median) rows here.
    [string] $SummaryCsv
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ArmOrder = @('all-off', 'rt-on', 'app-off', 'all-on')

$Metrics = @(
    @{ Key = 'http/rps/mean';             Name = 'Requests/sec';        Job = 'load';        Higher = $true;  Format = 'N0' }
    @{ Key = 'http/latency/mean';         Name = 'Mean latency (ms)';   Job = 'load';        Higher = $false; Format = 'N3' }
    @{ Key = 'http/latency/50';           Name = 'Latency p50 (ms)';    Job = 'load';        Higher = $false; Format = 'N3' }
    @{ Key = 'http/latency/99';           Name = 'Latency p99 (ms)';    Job = 'load';        Higher = $false; Format = 'N3' }
    @{ Key = 'http/latency/max';          Name = 'Latency max (ms)';    Job = 'load';        Higher = $false; Format = 'N3' }
    @{ Key = 'http/throughput';           Name = 'Throughput (MB/s)';   Job = 'load';        Higher = $true;  Format = 'N2' }
    @{ Key = 'http/requests/badresponses'; Name = 'Bad responses';      Job = 'load';        Higher = $false; Format = 'N0' }
    @{ Key = 'benchmarks/cpu';            Name = 'App CPU (%)';         Job = 'application'; Higher = $false; Format = 'N1' }
    @{ Key = 'benchmarks/working-set';    Name = 'Working set (MB)';    Job = 'application'; Higher = $false; Format = 'N0' }
    @{ Key = 'benchmarks/private-memory'; Name = 'Private memory (MB)'; Job = 'application'; Higher = $false; Format = 'N0' }
    @{ Key = 'benchmarks/start-time';     Name = 'Start time (ms)';     Job = 'application'; Higher = $false; Format = 'N0' }
    @{ Key = 'benchmarks/published-size'; Name = 'Published size (KB)'; Job = 'application'; Higher = $false; Format = 'N0' }
)

function Get-Median([double[]] $Values) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

function Get-Metric($Result, [string] $JobName, [string] $Key) {
    if (-not $Result.jobResults.jobs.PSObject.Properties.Name.Contains($JobName)) { return $null }
    $job = $Result.jobResults.jobs.$JobName
    if (-not $job.results.PSObject.Properties.Name.Contains($Key)) { return $null }
    return [double] $job.results.$Key
}

function Format-DeltaCell([object] $Delta, [bool] $HigherIsBetter, [double] $NoiseFloor) {
    if ($null -eq $Delta) { return @{ Text = 'n/a'; Color = 'Gray' } }
    $sign = ''
    if ($Delta -ge 0) { $sign = '+' }
    $text = "$sign$([math]::Round($Delta, 2))%"
    # Anything inside the observed run-to-run spread is not a signal.
    if ([math]::Abs($Delta) -le $NoiseFloor) { return @{ Text = "$text (noise)"; Color = 'DarkGray' } }
    $good = $Delta -lt 0
    if ($HigherIsBetter) { $good = $Delta -gt 0 }
    if ($good) { return @{ Text = $text; Color = 'Green' } }
    return @{ Text = $text; Color = 'Red' }
}

# ---------------------------------------------------------------------------------------
# Locate the run
# ---------------------------------------------------------------------------------------
if (-not $RunId) {
    $candidates = @(Get-ChildItem -LiteralPath $OutDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Filter '*.json').Count -gt 0 } |
        Sort-Object Name -Descending)
    if ($candidates.Count -eq 0) { throw "No run folders with results found under '$OutDir'." }
    $RunId = $candidates[0].Name
}

$runDir = Join-Path $OutDir $RunId
if (-not (Test-Path -LiteralPath $runDir)) { throw "Run folder '$runDir' does not exist." }

$files = @(Get-ChildItem -LiteralPath $runDir -Filter '*.json' -File)
if ($files.Count -eq 0) { throw "No result JSON files in '$runDir'." }

# ---------------------------------------------------------------------------------------
# Load every result into flat rows
# ---------------------------------------------------------------------------------------
$rows = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($f in $files) {
    if ($f.BaseName -notmatch '^(?<s>.+)-(?<a>all-off|rt-on|app-off|all-on)-i(?<i>\d+)$') {
        Write-Host "SKIP unrecognised file name: $($f.Name)" -ForegroundColor DarkYellow
        continue
    }
    $s = $Matches['s']; $a = $Matches['a']; $i = [int]$Matches['i']
    if ($Scenario -and ($Scenario -notcontains $s)) { continue }

    $result = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
    if ($result.returnCode -ne 0) {
        $failures.Add("$($f.BaseName) returnCode=$($result.returnCode)")
        continue
    }

    $bad = Get-Metric $result 'load' 'http/requests/badresponses'
    if ($null -ne $bad -and $bad -gt 0) {
        $failures.Add("$($f.BaseName) badresponses=$bad")
    }

    foreach ($m in $Metrics) {
        $v = Get-Metric $result $m.Job $m.Key
        if ($null -eq $v) { continue }
        $rows.Add([pscustomobject]@{
            RunId = $RunId; Scenario = $s; Arm = $a; Iteration = $i
            Metric = $m.Name; Key = $m.Key; Value = $v
            HigherIsBetter = $m.Higher; Format = $m.Format
        })
    }
}

if ($rows.Count -eq 0) { throw "No usable results parsed from '$runDir'." }

# ---------------------------------------------------------------------------------------
# Aggregate to medians
# ---------------------------------------------------------------------------------------
$agg = @{}
foreach ($g in $rows | Group-Object Scenario, Key, Arm) {
    $vals = [double[]] @($g.Group | ForEach-Object { $_.Value })
    $median = Get-Median $vals
    $min = ($vals | Measure-Object -Minimum).Minimum
    $max = ($vals | Measure-Object -Maximum).Maximum
    $spread = 0.0
    if ($median -ne 0) { $spread = ($max - $min) / [math]::Abs($median) * 100 }
    $first = $g.Group[0]
    $agg["$($first.Scenario)|$($first.Key)|$($first.Arm)"] = [pscustomobject]@{
        Scenario = $first.Scenario; Key = $first.Key; Arm = $first.Arm
        Metric = $first.Metric; HigherIsBetter = $first.HigherIsBetter; Format = $first.Format
        Median = $median; Min = $min; Max = $max; Spread = $spread; N = $vals.Count
    }
}

$scenarios = @($rows.Scenario | Sort-Object -Unique)
$summary = [System.Collections.Generic.List[object]]::new()

foreach ($s in $scenarios) {
    $armsPresent = @($ArmOrder | Where-Object { $a = $_; @($rows | Where-Object { $_.Scenario -eq $s -and $_.Arm -eq $a }).Count -gt 0 })

    Write-Host ''
    Write-Host ('=' * 118)
    Write-Host "  $s" -ForegroundColor Cyan
    Write-Host ('=' * 118)

    $iters = @($rows | Where-Object { $_.Scenario -eq $s } | Select-Object -ExpandProperty Iteration -Unique).Count
    Write-Host ("  arms: {0}    iterations: {1}" -f ($armsPresent -join ', '), $iters)
    Write-Host ''

    $header = "  {0,-20}" -f 'Metric'
    foreach ($a in $armsPresent) { $header += " {0,14}" -f $a }
    $header += " {0,18} {1,18} {2,18} {3,18}" -f 'all-off->all-on', 'app-off->all-on', 'rt layer', 'asp layer'
    Write-Host $header
    Write-Host ("  " + ('-' * 116))

    foreach ($m in $Metrics) {
        $cells = @{}
        foreach ($a in $armsPresent) { $cells[$a] = $agg["$s|$($m.Key)|$a"] }
        if (@($cells.Values | Where-Object { $null -ne $_ }).Count -eq 0) { continue }

        $line = "  {0,-20}" -f $m.Name
        $maxSpread = 0.0
        foreach ($a in $armsPresent) {
            $c = $cells[$a]
            if ($null -eq $c) { $line += " {0,14}" -f '-'; continue }
            $line += " {0,14}" -f $c.Median.ToString($m.Format)
            if ($c.Spread -gt $maxSpread) { $maxSpread = $c.Spread }
        }

        function Get-Delta($fromArm, $toArm) {
            $f = $cells[$fromArm]; $t = $cells[$toArm]
            if ($null -eq $f -or $null -eq $t) { return $null }
            if ($f.Median -eq 0) { return $null }
            return ($t.Median - $f.Median) / [math]::Abs($f.Median) * 100
        }

        $dFull = $null; $dApp = $null; $dRt = $null; $dAsp = $null
        if ($armsPresent -contains 'all-off' -and $armsPresent -contains 'all-on') { $dFull = Get-Delta 'all-off' 'all-on' }
        if ($armsPresent -contains 'app-off' -and $armsPresent -contains 'all-on') { $dApp = Get-Delta 'app-off' 'all-on' }
        # Layer decomposition, only meaningful when the mixed rt-on arm is present:
        #   rt layer  = all-off -> rt-on    (runtime built ON, aspnetcore still OFF)
        #   asp layer = rt-on   -> app-off  (aspnetcore additionally built ON)
        if ($armsPresent -contains 'all-off' -and $armsPresent -contains 'rt-on') { $dRt = Get-Delta 'all-off' 'rt-on' }
        if ($armsPresent -contains 'rt-on' -and $armsPresent -contains 'app-off') { $dAsp = Get-Delta 'rt-on' 'app-off' }

        $cFull = Format-DeltaCell $dFull $m.Higher $maxSpread
        $cApp = Format-DeltaCell $dApp $m.Higher $maxSpread
        $cRt = Format-DeltaCell $dRt $m.Higher $maxSpread
        $cAsp = Format-DeltaCell $dAsp $m.Higher $maxSpread

        Write-Host ($line + (" {0,18} {1,18} {2,18} {3,18}" -f $cFull.Text, $cApp.Text, $cRt.Text, $cAsp.Text))

        # Rank-based separation between all-off and all-on. The spread column compares a
        # delta-of-medians against a *range*, which is outlier-sensitive and grows with N,
        # so it can dismiss a real effect on a high-variance scenario (mvc did exactly
        # that: 7.56% spread, yet all 25 pairs separate). Counting how many all-off
        # iterations beat all-on iterations is the Mann-Whitney U statistic; complete
        # separation at 5v5 is an exact one-sided p of 1/252 ~= 0.004.
        $sepText = 'n/a'
        $offVals = @($rows | Where-Object { $_.Scenario -eq $s -and $_.Key -eq $m.Key -and $_.Arm -eq 'all-off' } | ForEach-Object { [double]$_.Value })
        $onVals = @($rows | Where-Object { $_.Scenario -eq $s -and $_.Key -eq $m.Key -and $_.Arm -eq 'all-on' } | ForEach-Object { [double]$_.Value })
        if ($offVals.Count -ge 3 -and $onVals.Count -ge 3) {
            $wins = 0
            foreach ($a in $offVals) { foreach ($b in $onVals) { if ($a -gt $b) { $wins++ } } }
            $tot = $offVals.Count * $onVals.Count
            if ($wins -eq $tot -or $wins -eq 0) { $sepText = "complete $wins/$tot" }
            else { $sepText = "$wins/$tot" }
        }

        $rec = [pscustomobject]@{
            RunId = $RunId; Scenario = $s; Metric = $m.Name; Key = $m.Key
            HigherIsBetter = $m.Higher
            MaxSpreadPct = [math]::Round($maxSpread, 2)
            Separation = $sepText
            DeltaAllOffToAllOnPct = if ($null -ne $dFull) { [math]::Round($dFull, 3) } else { $null }
            DeltaAppOffToAllOnPct = if ($null -ne $dApp) { [math]::Round($dApp, 3) } else { $null }
            DeltaRuntimeLayerPct  = if ($null -ne $dRt) { [math]::Round($dRt, 3) } else { $null }
            DeltaAspNetLayerPct   = if ($null -ne $dAsp) { [math]::Round($dAsp, 3) } else { $null }
        }
        foreach ($a in $ArmOrder) {
            $c = $agg["$s|$($m.Key)|$a"]
            $medianVal = $null; $minVal = $null; $maxVal = $null; $nVal = 0
            if ($null -ne $c) { $medianVal = $c.Median; $minVal = $c.Min; $maxVal = $c.Max; $nVal = $c.N }
            $rec | Add-Member -NotePropertyName "$a`_median" -NotePropertyValue $medianVal
            $rec | Add-Member -NotePropertyName "$a`_min" -NotePropertyValue $minVal
            $rec | Add-Member -NotePropertyName "$a`_max" -NotePropertyValue $maxVal
            $rec | Add-Member -NotePropertyName "$a`_n" -NotePropertyValue $nVal
        }
        $summary.Add($rec)
    }
}

# ---------------------------------------------------------------------------------------
# Headline table
# ---------------------------------------------------------------------------------------
Write-Host ''
Write-Host ('=' * 118)
Write-Host '  Requests/sec — median of all iterations' -ForegroundColor Cyan
Write-Host ('=' * 118)

$summary | Where-Object { $_.Key -eq 'http/rps/mean' } |
    Select-Object Scenario,
        @{ n = 'all-off'; e = { if ($null -ne $_.'all-off_median') { '{0:N0}' -f $_.'all-off_median' } else { '-' } } },
        @{ n = 'rt-on';   e = { if ($null -ne $_.'rt-on_median')   { '{0:N0}' -f $_.'rt-on_median' }   else { '-' } } },
        @{ n = 'app-off'; e = { if ($null -ne $_.'app-off_median') { '{0:N0}' -f $_.'app-off_median' } else { '-' } } },
        @{ n = 'all-on';  e = { if ($null -ne $_.'all-on_median')  { '{0:N0}' -f $_.'all-on_median' }  else { '-' } } },
        @{ n = 'full %';  e = { if ($null -ne $_.DeltaAllOffToAllOnPct) { '{0:+0.00;-0.00;0.00}' -f $_.DeltaAllOffToAllOnPct } else { 'n/a' } } },
        @{ n = 'rt %';    e = { if ($null -ne $_.DeltaRuntimeLayerPct) { '{0:+0.00;-0.00;0.00}' -f $_.DeltaRuntimeLayerPct } else { 'n/a' } } },
        @{ n = 'asp %';   e = { if ($null -ne $_.DeltaAspNetLayerPct) { '{0:+0.00;-0.00;0.00}' -f $_.DeltaAspNetLayerPct } else { 'n/a' } } },
        @{ n = 'app %';   e = { if ($null -ne $_.DeltaAppOffToAllOnPct) { '{0:+0.00;-0.00;0.00}' -f $_.DeltaAppOffToAllOnPct } else { 'n/a' } } },
        @{ n = 'spread %'; e = { '{0:N2}' -f $_.MaxSpreadPct } },
        @{ n = 'sep'; e = { $_.Separation } } |
    Format-Table -AutoSize | Out-String | Write-Host

Write-Host "  full %  = all-off -> all-on  (runtime + aspnetcore + app)"
Write-Host "  rt %    = all-off -> rt-on   (runtime layer alone; aspnetcore still OFF)"
Write-Host "  asp %   = rt-on   -> app-off (aspnetcore layer alone, on top of an async runtime)"
Write-Host "  app %   = app-off -> all-on  (app only, framework already async)"
Write-Host "  spread %= worst (max-min)/median across iterations for that metric; treat deltas"
Write-Host "            smaller than this as noise."
Write-Host "  sep     = all-off vs all-on pairwise wins (Mann-Whitney U). 'complete' means every"
Write-Host "            all-off iteration beat every all-on iteration; at 5v5 that is p ~= 0.004."
Write-Host "            This is more reliable than spread % on high-variance scenarios."
Write-Host ''

if ($scenarios -contains 'orchard') {
    Write-Host '  NOTE  orchard publishes framework-dependent, so it runs on the agent shared' -ForegroundColor DarkYellow
    Write-Host '        framework and cannot receive the runtime-async-off overlay. It has no' -ForegroundColor DarkYellow
    Write-Host '        all-off arm; only the app-only comparison is available for it.' -ForegroundColor DarkYellow
    Write-Host ''
}

if ($failures.Count -gt 0) {
    Write-Host "  WARNING: $($failures.Count) run(s) were excluded or suspect:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "    $f" -ForegroundColor Red }
    Write-Host ''
}

if ($Csv) {
    $rows | Export-Csv -LiteralPath $Csv -NoTypeInformation
    Write-Host "Wrote per-iteration rows: $Csv"
}
if ($SummaryCsv) {
    $summary | Export-Csv -LiteralPath $SummaryCsv -NoTypeInformation
    Write-Host "Wrote median summary:     $SummaryCsv"
}
