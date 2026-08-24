[CmdletBinding()]
param(
    [string]$Base = "$PSScriptRoot",

    # Which run folder under out\ to read. 'full' is the round-2 run.
    [string]$RunId = 'full',

    # Per-iteration rows output.
    [string]$Csv,

    # Aggregated per-percentile stats output.
    [string]$Summary,

    # Optional runs CSV to cross-check p50/p99 against. Skipped when absent.
    [string]$ValidateAgainst
)

$jsonDir = Join-Path $Base "out\$RunId"
if (-not $Csv) { $Csv = Join-Path $Base "out\$RunId-p90-runs.csv" }
if (-not $Summary) { $Summary = Join-Path $Base "out\$RunId-latency-percentile-stats.csv" }
if (-not $ValidateAgainst) {
    foreach ($cand in @("out\$RunId-runs.csv", 'out\full-runs.csv')) {
        $p = Join-Path $Base $cand
        if (Test-Path $p) { $ValidateAgainst = $p; break }
    }
}

function Get-Median {
    param([double[]]$v)
    if (-not $v -or $v.Count -eq 0) { return $null }
    $s = $v | Sort-Object; $n = $s.Count
    if ($n % 2 -eq 1) { return [double]$s[[int](($n - 1) / 2)] }
    return ([double]$s[$n / 2 - 1] + [double]$s[$n / 2]) / 2.0
}

# ---- 1. Read full-precision latency percentiles from the crank result JSON ----
$rows = @()
foreach ($f in Get-ChildItem "$jsonDir\*.json") {
    if ($f.BaseName -notmatch '^(?<s>.+)-(?<a>all-off|rt-on|app-off|all-on)-i(?<i>\d+)$') { continue }
    $scen = $Matches['s']; $armName = $Matches['a']; $iter = [int]$Matches['i']

    $d = Get-Content $f.FullName -Raw | ConvertFrom-Json
    $res = $null
    foreach ($jobName in $d.jobResults.jobs.PSObject.Properties.Name) {
        $r = $d.jobResults.jobs.$jobName.results
        if ($r -and $r.PSObject.Properties.Name -contains 'http/latency/90') { $res = $r; break }
    }
    if (-not $res) { continue }

    function Val($key) {
        $p = $res.PSObject.Properties | Where-Object { $_.Name -eq $key }
        if ($p -and $null -ne $p.Value -and $p.Value -ne '') { return [double]$p.Value }
        return $null
    }

    $rows += [pscustomobject]@{
        Scenario = $scen; Arm = $armName; Iter = $iter
        P50      = Val 'http/latency/50'
        P75      = Val 'http/latency/75'
        P90      = Val 'http/latency/90'
        P95      = Val 'http/latency/95'
        P99      = Val 'http/latency/99'
    }
}
Write-Host "Parsed $($rows.Count) runs from $jsonDir" -ForegroundColor Cyan
$rows | Sort-Object Scenario, Arm, Iter | Export-Csv $Csv -NoTypeInformation

# ---- 2. Validate p50/p99 against the already-published runs csv (exact) ------
if ($ValidateAgainst -and (Test-Path $ValidateAgainst)) {
$existing = Import-Csv $ValidateAgainst
$idx = @{}
foreach ($e in $existing) { $idx["$($e.Scenario)|$($e.Arm)|$($e.Iteration)|$($e.Metric)"] = $e.Value }
$checked = 0; $bad = 0; $maxDiff = 0.0
foreach ($r in $rows) {
    foreach ($p in @(@('Latency p50 (ms)', $r.P50), @('Latency p99 (ms)', $r.P99))) {
        $k = "$($r.Scenario)|$($r.Arm)|$($r.Iter)|$($p[0])"
        if ($idx.ContainsKey($k)) {
            $checked++
            $diff = [math]::Abs([double]$idx[$k] - [double]$p[1])
            if ($diff -gt $maxDiff) { $maxDiff = $diff }
            if ($diff -gt 1e-9) { $bad++ }
        }
    }
}
Write-Host ("Validation: {0} values compared, {1} differing, max diff {2}" -f $checked, $bad, $maxDiff) -ForegroundColor $(if ($bad) { 'Red' } else { 'Green' })
}
else {
    Write-Host "Validation: skipped (no runs csv to cross-check against)" -ForegroundColor Yellow
}

# ---- 3. Per-scenario stats for each percentile -------------------------------
$out = @()
foreach ($metric in 'P50', 'P75', 'P90', 'P99') {
    foreach ($scen in ($rows | Select-Object -ExpandProperty Scenario -Unique | Sort-Object)) {
        $set = $rows | Where-Object { $_.Scenario -eq $scen -and $null -ne $_.$metric }
        if (-not $set) { continue }
        $rec = [ordered]@{ Metric = $metric; Scenario = $scen }
        $med = @{}
        foreach ($a in 'all-off', 'rt-on', 'app-off', 'all-on') {
            $vals = @($set | Where-Object { $_.Arm -eq $a } | Select-Object -ExpandProperty $metric)
            $med[$a] = Get-Median $vals
            $rec["${a}_n"] = $vals.Count
            $rec[$a] = if ($null -ne $med[$a]) { [math]::Round($med[$a], 4) } else { '' }
        }
        $rec['FullPct'] = if ($med['all-off'] -and $med['all-on']) { [math]::Round((($med['all-on'] - $med['all-off']) / $med['all-off']) * 100, 2) } else { '' }
        $rec['AppPct'] = if ($med['app-off'] -and $med['all-on']) { [math]::Round((($med['all-on'] - $med['app-off']) / $med['app-off']) * 100, 2) } else { '' }
    # Layer decomposition: all-off -> rt-on is the runtime alone, rt-on -> app-off is ASP.NET alone.
    $rec['RtPct']  = if ($med['all-off'] -and $med['rt-on'])   { [math]::Round((($med['rt-on']   - $med['all-off']) / $med['all-off']) * 100, 2) } else { '' }
    $rec['AspPct'] = if ($med['rt-on']   -and $med['app-off']) { [math]::Round((($med['app-off'] - $med['rt-on'])   / $med['rt-on'])   * 100, 2) } else { '' }

        $spreads = @()
        foreach ($a in 'all-off', 'rt-on', 'app-off', 'all-on') {
            $vals = @($set | Where-Object { $_.Arm -eq $a } | Select-Object -ExpandProperty $metric)
            if ($vals.Count -ge 2 -and $med[$a]) {
                $spreads += ((($vals | Measure-Object -Maximum).Maximum - ($vals | Measure-Object -Minimum).Minimum) / [math]::Abs($med[$a])) * 100
            }
        }
        $rec['SpreadPct'] = if ($spreads.Count) { [math]::Round(($spreads | Measure-Object -Maximum).Maximum, 2) } else { '' }

        $offV = @($set | Where-Object { $_.Arm -eq 'all-off' } | Select-Object -ExpandProperty $metric)
        $onV = @($set | Where-Object { $_.Arm -eq 'all-on' } | Select-Object -ExpandProperty $metric)
        if ($offV.Count -and $onV.Count) {
            $w = 0; foreach ($o in $offV) { foreach ($n in $onV) { if ($o -lt $n) { $w++ } } }
            $rec['Sep'] = "$w/$($offV.Count * $onV.Count)"
        }
        else { $rec['Sep'] = '' }
        $out += [pscustomobject]$rec
    }
}
$out | Export-Csv $Summary -NoTypeInformation
$out | Where-Object { $_.Metric -eq 'P90' } | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

