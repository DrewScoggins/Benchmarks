[CmdletBinding()]
param(
    [string] $Base    = (Split-Path -Parent $MyInvocation.MyCommand.Path),
    [string] $OutHtml,
    [string] $OutTsv
)

if (-not $OutHtml) { $OutHtml = Join-Path $Base 'teams-results.html' }
if (-not $OutTsv)  { $OutTsv  = Join-Path $Base 'teams-results.tsv'  }

$runs = @(
    @{ Key='x64';   Title='x64 &mdash; aspnet-gold-lin-relay (56 cores, Linux x64)';
       Med="$Base\out\r3-gold-medians.csv";  Lat="$Base\out\r3-gold-latency-stats.csv";  Iters=5 }
    @{ Key='arm64'; Title='Arm64 &mdash; cobalt-cloud-lin-al3-relay (4 cores, Azure Linux 3 Arm64 VM)';
       Med="$Base\out\r3-cloud-medians.csv"; Lat="$Base\out\r3-cloud-latency-stats.csv"; Iters=7 }
    @{ Key='cb200'; Title='Arm64 Cobalt 200 &mdash; cobalt200-lin-al3-relay (128-core host, app confined to 4 cores via cpuSet 0-3)';
       Med="$Base\out\cb200-4c-medians.csv"; Lat="$Base\out\cb200-4c-latency-percentile-stats.csv"; Iters=7 }
)

function Fmt-Num([string]$v, [int]$dec = 0) {
    if ([string]::IsNullOrWhiteSpace($v)) { return '&mdash;' }
    $d = 0.0
    if (-not [double]::TryParse($v, [ref]$d)) { return [System.Web.HttpUtility]::HtmlEncode($v) }
    if ($dec -eq 0) { return ('{0:N0}' -f $d) }
    return ('{0:N2}' -f $d)
}

function Fmt-Pct([string]$v, [switch]$LowerIsBetter) {
    if ([string]::IsNullOrWhiteSpace($v) -or $v -eq 'n/a') { return @{ Text='&mdash;'; Cls='muted' } }
    $d = 0.0
    if (-not [double]::TryParse(($v -replace '[%+]',''), [ref]$d)) { return @{ Text=$v; Cls='' } }
    $txt = ('{0:+0.00;-0.00;0.00}' -f $d) + '%'
    # Classify on the *effect*, not the raw sign: for latency a rise is a regression.
    $effect = if ($LowerIsBetter) { -$d } else { $d }
    $cls = if ($effect -le -5) { 'bad' } elseif ($effect -lt -1) { 'warn' } elseif ($effect -ge 1) { 'good' } else { 'muted' }
    return @{ Text=$txt; Cls=$cls }
}

function Fmt-Sep([string]$v) {
    if ([string]::IsNullOrWhiteSpace($v) -or $v -eq 'n/a') { return @{ Text='&mdash;'; Cls='muted' } }
    if ($v -match 'complete') { return @{ Text=($v -replace 'complete\s*',''); Cls='sep-strong' } }
    return @{ Text=$v; Cls='muted' }
}

function Fmt-Tsv([string]$v, [int]$dec = 0) {
    if ([string]::IsNullOrWhiteSpace($v)) { return '' }
    $d = 0.0
    if (-not [double]::TryParse($v, [ref]$d)) { return $v }
    if ($dec -eq 0) { return ('{0:0}' -f $d) }
    return ('{0:0.00}' -f $d)
}

$sb  = [System.Text.StringBuilder]::new()
$tsv = [System.Text.StringBuilder]::new()

[void]$sb.AppendLine(@'
<!doctype html><html><head><meta charset="utf-8">
<style>
 body{font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#1b1b1b;}
 h2{font-size:16px;margin:18px 0 2px;}
 h3{font-size:13px;margin:14px 0 4px;color:#333;}
 p.sub{margin:0 0 8px;color:#555;font-size:12px;}
 table{border-collapse:collapse;margin:6px 0 14px;}
 th,td{border:1px solid #b8b8b8;padding:4px 9px;text-align:right;white-space:nowrap;}
 th{background:#f0f0f0;font-weight:600;text-align:center;}
 td.s,th.s{text-align:left;font-family:Consolas,monospace;}
 td.num{font-family:Consolas,monospace;}
 .good{color:#0a7d20;font-weight:600;}
 .bad{color:#b30000;font-weight:600;}
 .warn{color:#a35a00;font-weight:600;}
 .muted{color:#666;}
 .sep-strong{color:#0a3d91;font-weight:600;}
 .legend{font-size:11.5px;color:#444;margin:2px 0 20px;}
 .legend b{color:#1b1b1b;}
</style></head><body>
<h2>Runtime-async &mdash; Round 3 results</h2>
<p class="sub">Runtime <code>ca4ed7d</code> (main @ 2026-08-04) + PR #131177 rebased &middot; ASP.NET <code>28dd8a5</code> &middot; medians across iterations &middot; higher Requests/sec is better.</p>
'@)

foreach ($run in $runs) {
    $med = Import-Csv $run.Med
    $lat = Import-Csv $run.Lat

    [void]$sb.AppendLine("<h2>$($run.Title)</h2>")
    [void]$sb.AppendLine("<p class=""sub"">Medians of $($run.Iters) iterations per arm.</p>")
    [void]$tsv.AppendLine(($run.Title -replace '&mdash;','-'))

    # ---- throughput ----
    [void]$sb.AppendLine('<h3>Throughput (Requests/sec)</h3>')
    [void]$sb.AppendLine('<table><tr><th class="s">Scenario</th><th>All&nbsp;off</th><th>Rt&nbsp;on</th><th>App&nbsp;off</th><th>All&nbsp;on</th><th>Full&nbsp;stack&nbsp;&Delta;</th><th>Runtime&nbsp;&Delta;</th><th>ASP.NET&nbsp;&Delta;</th><th>App&nbsp;only&nbsp;&Delta;</th><th>Noise&nbsp;(spread)</th><th>Separation</th></tr>')
    [void]$tsv.AppendLine("Scenario`tAll off`tRt on`tApp off`tAll on`tFull stack %`tRuntime %`tASP.NET %`tApp only %`tSpread %`tSeparation")

    # Scenarios missing an all-on arm are incomplete runs (failed iterations) and are not
    # reportable. orchard is the legitimate exception: it has no all-off arm by design.
    $rows = $med | Where-Object { $_.Metric -eq 'Requests/sec' } | Where-Object {
        $_.Scenario -eq 'orchard' -or -not [string]::IsNullOrWhiteSpace($_.'all-on_median')
    }
    $dropped = ($med | Where-Object { $_.Metric -eq 'Requests/sec' } | Where-Object {
        $_.Scenario -ne 'orchard' -and [string]::IsNullOrWhiteSpace($_.'all-on_median')
    }).Scenario

    foreach ($r in ($rows | Sort-Object { [double]($(if ($_.'all-off_median') { $_.'all-off_median' } else { $_.'all-on_median' })) } -Descending)) {
        $full = Fmt-Pct $r.DeltaAllOffToAllOnPct
        $rt   = Fmt-Pct $r.DeltaRuntimeLayerPct
        $asp  = Fmt-Pct $r.DeltaAspNetLayerPct
        $app  = Fmt-Pct $r.DeltaAppOffToAllOnPct
        $sep  = Fmt-Sep $r.Separation
        [void]$sb.AppendLine(("<tr><td class=""s"">{0}</td><td class=""num"">{1}</td><td class=""num"">{2}</td><td class=""num"">{3}</td><td class=""num"">{4}</td><td class=""{5}"">{6}</td><td class=""{7}"">{8}</td><td class=""{9}"">{10}</td><td class=""{11}"">{12}</td><td class=""num muted"">{13}%</td><td class=""{14}"">{15}</td></tr>" -f `
            $r.Scenario, (Fmt-Num $r.'all-off_median'), (Fmt-Num $r.'rt-on_median'), (Fmt-Num $r.'app-off_median'), (Fmt-Num $r.'all-on_median'),
            $full.Cls, $full.Text, $rt.Cls, $rt.Text, $asp.Cls, $asp.Text, $app.Cls, $app.Text, $r.MaxSpreadPct, $sep.Cls, $sep.Text))
        [void]$tsv.AppendLine(("{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}`t{8}`t{9}`t{10}" -f `
            $r.Scenario, (Fmt-Tsv $r.'all-off_median'), (Fmt-Tsv $r.'rt-on_median'), (Fmt-Tsv $r.'app-off_median'), (Fmt-Tsv $r.'all-on_median'),
            $r.DeltaAllOffToAllOnPct, $r.DeltaRuntimeLayerPct, $r.DeltaAspNetLayerPct, $r.DeltaAppOffToAllOnPct, $r.MaxSpreadPct, $r.Separation))
    }
    [void]$sb.AppendLine('</table>')
    if ($dropped) {
        [void]$sb.AppendLine(("<p class=""sub"">Excluded (runs failed, no complete arm set): <b>{0}</b>.</p>" -f ($dropped -join ', ')))
        [void]$tsv.AppendLine(("Excluded (runs failed): {0}" -f ($dropped -join ', ')))
    }
    [void]$tsv.AppendLine('')

    # ---- latency ----
    [void]$sb.AppendLine('<h3>Latency (ms) &mdash; lower is better</h3>')
    [void]$sb.AppendLine('<table><tr><th class="s">Scenario</th><th>Pct</th><th>All&nbsp;off</th><th>Rt&nbsp;on</th><th>App&nbsp;off</th><th>All&nbsp;on</th><th>Full&nbsp;stack&nbsp;&Delta;</th><th>Runtime&nbsp;&Delta;</th><th>ASP.NET&nbsp;&Delta;</th><th>App&nbsp;only&nbsp;&Delta;</th></tr>')
    [void]$tsv.AppendLine("Scenario`tPercentile`tAll off`tRt on`tApp off`tAll on`tFull stack %`tRuntime %`tASP.NET %`tApp only %")

    foreach ($scen in ($lat | Select-Object -ExpandProperty Scenario -Unique | Sort-Object)) {
        if ($dropped -contains $scen) { continue }
        foreach ($p in @('P50','P90')) {
            $r = $lat | Where-Object { $_.Scenario -eq $scen -and $_.Metric -eq $p } | Select-Object -First 1
            if (-not $r) { continue }
            $full = Fmt-Pct $r.FullPct -LowerIsBetter
            $rt   = Fmt-Pct $r.RtPct -LowerIsBetter
            $asp  = Fmt-Pct $r.AspPct -LowerIsBetter
            $app  = Fmt-Pct $r.AppPct -LowerIsBetter
            [void]$sb.AppendLine(("<tr><td class=""s"">{0}</td><td>{1}</td><td class=""num"">{2}</td><td class=""num"">{3}</td><td class=""num"">{4}</td><td class=""num"">{5}</td><td class=""{6}"">{7}</td><td class=""{8}"">{9}</td><td class=""{10}"">{11}</td><td class=""{12}"">{13}</td></tr>" -f `
                $scen, $p, (Fmt-Num $r.'all-off' 2), (Fmt-Num $r.'rt-on' 2), (Fmt-Num $r.'app-off' 2), (Fmt-Num $r.'all-on' 2),
                $full.Cls, $full.Text, $rt.Cls, $rt.Text, $asp.Cls, $asp.Text, $app.Cls, $app.Text))
            [void]$tsv.AppendLine(("{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}`t{8}`t{9}" -f $scen,$p,(Fmt-Tsv $r.'all-off' 2),(Fmt-Tsv $r.'rt-on' 2),(Fmt-Tsv $r.'app-off' 2),(Fmt-Tsv $r.'all-on' 2),$r.FullPct,$r.RtPct,$r.AspPct,$r.AppPct))
        }
    }
    [void]$sb.AppendLine('</table>')
    [void]$tsv.AppendLine('')
}

[void]$sb.AppendLine(@'
<div class="legend">
<b>All off</b> = runtime + ASP.NET + app all built with runtime-async disabled (baseline).<br>
<b>Rt on</b> = runtime built with runtime-async enabled, ASP.NET still disabled, app not opted in.<br>
<b>App off</b> = runtime + ASP.NET built with runtime-async enabled, benchmark app not opted in.<br>
<b>All on</b> = runtime + ASP.NET + app all runtime-async enabled.<br>
<b>Full stack &Delta;</b> = All off &rarr; All on. The total cost of the feature. Negative = slower for throughput, positive = worse for latency.<br>
<b>Runtime &Delta;</b> = All off &rarr; Rt on. The cost of the <i>runtime</i> layer alone, with ASP.NET still off.<br>
<b>ASP.NET &Delta;</b> = Rt on &rarr; App off. The cost of the <i>ASP.NET</i> layer alone, on top of an already-async runtime.<br>
<b>App only &Delta;</b> = App off &rarr; All on. The cost of an application opting in, on top of an already-async framework.<br>
<b>Noise (spread)</b> = worst (max&minus;min)/median across iterations for that scenario. Treat any delta smaller than this as noise.<br>
<b>Colour</b> is by <i>effect</i>, not by sign: <span class="bad">red</span> = regression &ge;5%, <span class="warn">amber</span> = regression 1&ndash;5%, <span class="good">green</span> = improvement &ge;1%, grey = within &plusmn;1%. In the latency tables a <i>rise</i> is the regression, so the sign-to-colour mapping is inverted there.<br>
<b>Separation</b> = Mann-Whitney pairwise wins, All off vs All on. <span class="sep-strong">25/25</span> or <span class="sep-strong">49/49</span> means every baseline iteration beat every async-on iteration (p&nbsp;&asymp;&nbsp;0.004 or better) &mdash; more reliable than spread on noisy scenarios.<br>
<b>orchard</b> publishes framework-dependent, so it cannot receive the runtime-async-off overlay and has no <i>All off</i> arm.<br>
<b>Environment</b> is pinned per architecture (x64 SDK <code>11.0.100-rc.1.26402.101</code>, Arm64 <code>11.0.100-rc.1.26404.112</code>, load client <code>8.0.423</code>) so all four arms share the toolchain each was originally measured on. Absolute numbers are only comparable <i>within</i> an architecture.<br>
<b>Arm64 database scenarios</b> (<i>fortunes</i>, <i>fortunes_ef</i>) failed on that pod &mdash; its db agent deadlocks; treat any Arm64 db row as unreliable.
</div>
</body></html>
'@)

Add-Type -AssemblyName System.Web -ErrorAction SilentlyContinue
Set-Content -Path $OutHtml -Value $sb.ToString() -Encoding UTF8
Set-Content -Path $OutTsv  -Value $tsv.ToString() -Encoding UTF8

Write-Host "HTML: $OutHtml"
Write-Host "TSV : $OutTsv"
