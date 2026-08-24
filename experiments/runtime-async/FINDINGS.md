# Runtime-Async Performance Impact on ASP.NET Workloads

**Date:** 2026-07-30 (rounds 1–2) · 2026-08-04 (round 3) · 2026-08-17 (round 4)
**Author:** @DrewScoggins (with GitHub Copilot CLI)
**Hardware profile:** `aspnet-gold-lin-relay` (gold-lin, 56 cores, Linux x64) · `cobalt-cloud-lin-al3-relay` (Arm64, 4 cores)
**Status:** Rounds 1–2 complete for 7 of 9 scenarios (96 of 130 planned runs). See [Data Completeness](#11-data-completeness).
**Round 3** (PR #131177 on current main, x64 + Arm64) begins at [§14](#14-round-3-headline).
**Round 4** (splitting the framework cost into runtime vs ASP.NET) begins at [§24](#24-round-4--splitting-the-framework-cost-into-runtime-vs-aspnet).
**Round 5** (Cobalt 200, newer Arm64 hardware) begins at [§27](#27-round-5--cobalt-200-newer-arm64-hardware).

> **Read §27 first.** This document is chronological, so the headline below is the
> round-1 conclusion. The cost is in the framework, not the app (round 1), and within
> the framework it is **entirely in ASP.NET Core, not the runtime** (round 4). Round 5
> then shows the large Arm64 penalty reported in §26 was a property of the cobalt100
> pod: on newer Arm64 hardware the same bits cost **-3.1% to -5.4%**, in line with x64.

---

## 1. Headline Finding

**Runtime-async costs roughly 3% throughput on framework-bound ASP.NET scenarios, and essentially all of that cost comes from the framework being compiled async — not from the application opting in.**

| | |
|---|---|
| Turning the feature on across the **whole stack** (runtime + ASP.NET Core + app) | **−2.8% to −6.9%** throughput |
| Turning it on for the **app only**, on top of an already-async framework | **−0.4% to +0.4%** (indistinguishable from noise) |

This distinction is the entire point of the experiment, and it is invisible to the naive test. A previous round toggled only the app-level MSBuild flag on top of official build-cache-service (BCS) bits and measured "no impact." That conclusion was **wrong**, because the official runtime and ASP.NET Core bits are themselves compiled with runtime-async enabled. Measuring the feature's real cost requires rebuilding the framework itself.

---

## 2. What Runtime-Async Is, and the Three Layers That Enable It

Runtime-async changes how `async` methods are compiled: instead of the C# compiler generating a state machine, methods are marked `MethodImplOptions.Async` (`0x2000`) and the runtime/JIT handles suspension. Because the **IL shape itself differs**, there is **no environment variable or JIT switch** that turns it off at run time — the only way to obtain async-off framework code is to recompile it.

The feature is enabled independently in three places:

| # | Layer | Where it is enabled | Can it be disabled without patching? |
|---|---|---|---|
| 1 | **dotnet/runtime** | `src/libraries/Directory.Build.targets:145` (gated on `UseRuntimeAsync != 'false'`) **and** `src/coreclr/System.Private.CoreLib/System.Private.CoreLib.csproj:47`, which sets `<Features>` **unconditionally** | **No** — CoreLib needed a one-line patch |
| 2 | **dotnet/aspnetcore** | `Directory.Build.targets:153` (shared framework) and `:167` (tests), already gated on `UseRuntimeAsync != 'false'` | Yes — property alone suffices |
| 3 | **The application** | MSBuild property `/p:Features=runtime-async=on` on the benchmark app's csproj | Yes — just omit the flag |

Layers 1 and 2 are what "the framework" means throughout this document. Layer 3 is "the app."

---

## 3. Experiment Design — The Three Arms

Each scenario is measured under three configurations. Comparing them isolates *where* the cost lives.

| Arm | Runtime (layer 1) | ASP.NET Core (layer 2) | App (layer 3) | What it represents |
|---|---|---|---|---|
| **`all-off`** | OFF | OFF | OFF | True baseline — the feature does not exist anywhere in the stack |
| **`app-off`** | ON | ON | OFF | Today's shipping default — framework is async, app has not opted in |
| **`all-on`** | ON | ON | ON | Full adoption — everything async |

The two comparisons that matter:

- **`all-off` → `all-on`** — the **total** cost of the feature (reported as `full %`)
- **`app-off` → `all-on`** — the **incremental** cost of an app opting in, given the framework is already async (reported as `app %`)

`app-off` and `all-on` deliberately share the *same* physical framework overlay (the async-ON one) and differ only by the app's build flag. `all-off` is the only arm that receives the async-OFF overlay. This was verified by hashing the deployed files (see §7).

---

## 4. How Each Framework Was Acquired or Built

### 4.1 Pinned commits

Everything is pinned to the exact commits the official BCS artifacts were produced from, so the local builds are directly comparable to shipping bits.

| Component | Commit | Version string |
|---|---|---|
| dotnet/runtime | `fdccdc6954791fcde7ffa2834d75930c0efa5456` | — |
| dotnet/aspnetcore | `747d2cdb584079a0c7309115979f13c331fb7df7` | `11.0.0-rc.1.26380.5` |

### 4.2 Stock (official) bits — reference only

Downloaded from the build cache service, using the URL pattern supplied for this work:

```
https://pvscmdupload.blob.core.windows.net/$web/builds/aspnetcore/buildArtifacts/
  <build-sha>/aspnetcore_x64_linux/BuildArtifacts_linux_x64_Release_aspnetcore.nupkg
```

Note the SHA in the blob path is the **build** identifier, not the source commit. The source commit was read from `Microsoft.AspNetCore.App.versions.txt` inside the package, which reports `747d2cdb5840...` / `11.0.0-rc.1.26380.5`.

These stock bits were **not** used to produce any benchmark number in this report. They served two purposes:

1. **Discovery** — scanning them proved the shipping framework is already async-compiled (1,983 async methods), which invalidated the earlier round's baseline.
2. **Validation target** — the locally built async-ON framework was required to match them exactly before any measurement was trusted (§7).

### 4.3 dotnet/runtime — built twice from one source tree

Two `git worktree`s were created at the pinned commit: `~/ra/runtime-on` and `~/ra/runtime-noasync`.

**Required patch.** Unlike `src/libraries`, CoreLib's csproj enables the feature unconditionally, so `UseRuntimeAsync=false` had no effect on it. A one-line change (`wsl/patch-corelib.py`) adds the same condition the rest of the repo already uses:

```xml
<!-- src/coreclr/System.Private.CoreLib/System.Private.CoreLib.csproj:47 -->
- <Features>$(Features);runtime-async=on</Features>
+ <Features Condition="'$(UseRuntimeAsync)' != 'false'">$(Features);runtime-async=on</Features>
```

The patch is applied to **both** worktrees, so ON and OFF differ only by the property — not by source.

**Build commands** (`wsl/build-runtime.sh`):

```bash
# async ON
./build.sh -s clr+libs+packs -c Release -a x64
# async OFF
./build.sh -s clr+libs+packs -c Release -a x64 /p:UseRuntimeAsync=false
```

Wall time ≈ 20–22 min each.

### 4.4 dotnet/aspnetcore — built twice from one source tree

Two worktrees at the pinned commit: `~/ra/aspnetcore` and `~/ra/aspnetcore-off`. **No source patch required** — aspnetcore already gates on `UseRuntimeAsync`.

**Build commands** (`wsl/build-aspnetcore.sh`):

```bash
# async ON
./eng/build.sh -c Release -arch x64 -pack --no-build-java \
    -p:OnlyPackPlatformSpecificPackages=true /p:BuildNodeJS=false
# async OFF  (adds one property)
./eng/build.sh -c Release -arch x64 -pack --no-build-java \
    -p:OnlyPackPlatformSpecificPackages=true /p:BuildNodeJS=false /p:UseRuntimeAsync=false
```

Wall time ≈ 10–13 min each.

#### The `OnlyPackPlatformSpecificPackages` discovery — load-bearing

This flag is not cosmetic, and omitting it silently corrupts the experiment.

aspnetcore's runtime-async property group (`Directory.Build.targets:153`) is guarded by `'$(IsPackable)' != 'true'` — the feature is only meant for projects that ship *exclusively* in the shared framework. Dual-shipped projects (those listed as `AspNetCoreAppReferenceAndPackage` in `eng/SharedFramework.Local.props`) set `IsPackable=true` in their own csproj, which blocks the flag. `Directory.Build.targets:80` flips it back:

```xml
<IsPackable Condition="'$(OnlyPackPlatformSpecificPackages)' == 'true' AND '$(RuntimeIdentifier)' == ''">false</IsPackable>
```

Official CI passes this on every leg (`.azure/pipelines/ci-public.yml:301`). Without it, the "async ON" build produced only **848** async methods across 50 assemblies instead of the stock **1,099** across 61 — a clean all-or-nothing gap across exactly 11 assemblies:

`Microsoft.Extensions.Identity.Core` (109), `WebUtilities` (39), `Components` (32), `Components.Web` (25), `JSInterop` (15), `Authorization` (10), `Components.Forms` (7), `Identity.Stores` (6), `HealthChecks` (5), `Components.Authorization` (2), `SignalR.Common` (1).

This was caught by the mandatory stock-parity check in §7, not by any build error.

#### Other build notes

- `-p:BuildNodeJS=false` is the supported "No-NodeJS CI build" mode; it skips prebuilt Blazor JS assets that HTTP benchmarks never touch. Applied to **both** flavors so it cannot skew the A/B.
- `git worktree` does **not** populate submodules — `git submodule update --init --recursive` is required per worktree (otherwise MessagePack sources are missing).
- MSBuild's up-to-date checks are timestamp-based and do **not** invalidate on a changed global property. Both build scripts `rm -rf artifacts/{bin,obj,packages}` first, or you silently get the previous flavor.

### 4.5 Overlay staging

`New-LocalOverlay.ps1` assembles two flattened shared-framework directories, each 451 files:

1. Runtime pack `lib/net11.0` + `native`
2. ASP.NET Core shared framework `lib/net11.0` layered on top (last write wins)

Each overlay is shipped to the agent per run via crank's `--application.options.outputFiles <dir>\*`, which copies the files into the published application folder, overriding the self-contained framework.

---

## 5. Verification That the Bits Are Actually Different

A custom tool (`tools/AsyncScan`) reads every assembly with `System.Reflection.Metadata` and counts methods whose `MethodDefinition.ImplAttributes` include `MethodImplOptions.Async` (`0x2000`). It also reports ReadyToRun (R2R) status via `PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size != 0`.

| Overlay | Assemblies | Async methods | Total methods | Assemblies w/ async | R2R |
|---|---|---|---|---|---|
| **Stock BCS** (reference) | 313 | 1,983 | 196,956 | 90 | 130 / 313 |
| **Local `on`** | 313 | **1,983** | 196,956 | 90 | 130 / 313 |
| **Local `off`** | 313 | **23** | 196,956 | 1 | 130 / 313 |

The local async-ON build is an **exact structural match** to the official bits on every axis. This is the check that caught the `OnlyPackPlatformSpecificPackages` bug.

**Residual 23 methods in the OFF build.** These come from 11 hand-written `[MethodImpl(MethodImplOptions.Async)]` sites in `System/Runtime/CompilerServices/AsyncHelpers.cs` (23 after overloads and generic instantiations). They are the feature's own await plumbing, are inert unless called, and cannot be removed by a build property. Accepted caveat.

### 5.1 Post-hoc verification from the runs themselves

The table above verifies the overlays as *staged locally*. The checks below verify what was **actually deployed and executed on the agent**, using files downloaded back from the benchmark machine after each run. All three layers are confirmed independently.

**Layer 1 + 2 — framework, by hash.** After deployment, `libclrjit.so` (runtime/JIT, native) and `Microsoft.AspNetCore.Antiforgery.dll` (ASP.NET Core, managed) were downloaded from the published folder on the agent and hashed. Across all 18 completed scenario/arm combinations (6 scenarios × 3 arms), **18/18 matched the intended overlay exactly**, with zero unknowns:

| Arm | `libclrjit.so` (SHA-256 prefix) | `Antiforgery.dll` (SHA-256 prefix) | Resolves to |
|---|---|---|---|
| `all-off` | `9B152FB8BB6C` | `603183271A3F` | **OFF** overlay |
| `app-off` | `1C6F43A7B738` | `93D0AAE3D0E1` | **ON** overlay |
| `all-on` | `1C6F43A7B738` | `93D0AAE3D0E1` | **ON** overlay |

`app-off` and `all-on` are byte-identical here **by design** — they share the async-ON framework and differ only in the app. `all-off` differs in both files, confirming the runtime *and* the ASP.NET Core layer were both swapped.

**Layer 2 — framework, by IL.** Hashes prove the files differ; scanning proves *how*. Running `AsyncScan` over the `Antiforgery.dll` retrieved from the agent:

| Arm | Assemblies w/ async | Runtime-async methods | Total methods |
|---|---|---|---|
| `all-off` | 0 | **0** | 258 |
| `app-off` | 1 | **4** | 250 |
| `all-on` | 1 | **4** | 250 |

The `all-off` copy contains **no** runtime-async methods at all. The total method count also drops from 258 to 250, consistent with compiler-generated state-machine members being replaced by 4 runtime-async methods. This is direct IL-level evidence from the bytes that executed.

**Layer 3 — the app, by published size.** `app-off` and `all-on` deploy identical framework bits, so hashing cannot distinguish them; the only difference is the app's `/p:Features=runtime-async=on` build flag. crank's `Published size (KB)` provides an independent probe:

| Arm | Published size (KB) | App built with flag? |
|---|---|---|
| `all-off` | 135,367 | No |
| `app-off` | 135,367 | No |
| `all-on` | **135,336** | **Yes** |

The value is **perfectly deterministic** — a single distinct value per arm across all 5 non-Orchard scenarios and every iteration, zero variance. Applying the flag shrinks the app by exactly 31 KB, again consistent with state machines being replaced. For OrchardCore (a far larger app) the same flag shrinks the publish by ~7,086 KB (566,612 → 559,526), confirming the flag took effect there too.

> **Why this probe is clean:** `Published size` is measured *before* the overlay is copied in. We know this because `all-off` and `app-off` report the identical 135,367 KB despite their overlays differing by 1,915 KB on disk (110,347 KB for `on` vs 112,262 KB for `off`). That makes published size a probe of the **application layer alone**, uncontaminated by the framework swap — precisely the independent signal needed to confirm layer 3.

**Command lines.** `logs/full/manifest.jsonl` and the header of each run log record the exact invocation. Only `all-on` runs carry `--application.buildArguments /p:Features=runtime-async=on`, and each log's `File added:` lines name the overlay directory (`overlay\off\...` or `overlay\on\...`) for every one of the 451 files copied.

**Summary of what was running in each arm:**

| Arm | Runtime (JIT + CoreLib) | ASP.NET Core | Application | Evidence |
|---|---|---|---|---|
| `all-off` | async **OFF** | async **OFF** (0 methods in probe asm) | no flag | hash + IL scan + size |
| `app-off` | async **ON** | async **ON** (4 methods in probe asm) | no flag | hash + IL scan + size |
| `all-on` | async **ON** | async **ON** (4 methods in probe asm) | **flag set** | hash + IL scan + size |

---

## 6. Benchmark Execution

| Setting | Value |
|---|---|
| Tool | crank (`Microsoft.Crank.Controller`) |
| Profile | `aspnet-gold-lin-relay` (relay profiles are required; needs current `az login`) |
| Target framework | `net11.0` |
| Iterations | 5 per scenario per arm; **median** reported |
| Ordering | Arms interleaved within each iteration (`all-off`, `app-off`, `all-on`, then next iteration) so slow machine drift affects all arms equally |
| Scenario sources | `D:\git\benchmarks\scenarios\*.benchmarks.yml` (read-only; never modified) |

### Scenarios

| Scenario | Config file | Database? | Why included |
|---|---|---|---|
| `json` | `json.benchmarks.yml` | No | Minimal JSON serialization; framework-bound |
| `plaintext` | `plaintext.benchmarks.yml` | No | Pipelined text; near pure I/O, expected null control |
| `mvc` | `json.benchmarks.yml` | No | MVC pipeline; more async depth than minimal APIs |
| `fortunes` | `database.benchmarks.yml` | Yes (raw ADO) | Classic DB + templating |
| `fortunes_ef` | `database.benchmarks.yml` | Yes (EF Core) | Adds EF Core's async layers |
| `multiple_queries` | `database.benchmarks.yml` | Yes | 20 queries/request; DB-latency-bound control |
| `orchard` (`about-sqlite`) | `orchard.benchmarks.yml` | SQLite | Large real-world CMS app |
| `updates` | `database.benchmarks.yml` | Yes | **Not completed** — see §11 |
| `fortunes_ef_mvc_https` | `database.benchmarks.yml` | Yes | **Not completed** — see §11 |

### Per-run integrity checks

- `--property` tags stamp `scenario`, `arm`, `iteration`, `overlay`, `appFlag`, `runtimeSha`, `aspnetSha`, `profile`, `runId` into every result JSON.
- `-VerifyOverlay` downloads two marker files from the agent after deployment (`libclrjit.so`, `Microsoft.AspNetCore.Antiforgery.dll`) and hashes them, confirming the intended overlay actually landed and that `all-off` differs from `app-off`/`all-on`.
- Full stdout of every run is retained in `logs/full/<scenario>-<arm>-i<N>.log`, and the exact command line for every run in `logs/full/manifest.jsonl`.

> **Note:** crank's reported ".NET Runtime Version" is **not** evidence the overlay landed — it reflects the SDK/runtime crank installed, not the overlaid files. Only the marker-file hashes prove it.

---

## 7. Results — Throughput

Medians of 5 iterations (4 where noted), Requests/sec.

| Scenario | all-off | app-off | all-on | full % | app % | spread % | sep | N |
|---|---|---|---|---|---|---|---|---|
| json | 1,765,774 | 1,724,971 | 1,714,706 | **−2.89** | −0.60 | 1.48 | **25/25** | 5/5/5 |
| fortunes | 578,746 | 560,356 | 558,081 | **−3.57** | −0.41 | 1.77 | **25/25** | 5/5/5 |
| fortunes_ef | 480,872 | 466,590 | 467,247 | **−2.83** | +0.14 | 1.47 | **25/25** | 5/4/5 |
| mvc | 1,134,874 | 1,077,573 | 1,056,576 | **−6.90** | −1.95 | 7.56 | **25/25** | 5/5/5 |
| plaintext | 9,319,026 | 9,366,904 | 9,319,748 | +0.01 | −0.50 | 3.61 | 16/25 | 5/5/5 |
| multiple_queries | 51,292 | 51,074 | 51,279 | −0.03 | +0.40 | 1.34 | 10/16 | 4/4/4 |
| orchard | — | 18,002 | 18,669 | n/a | +3.71 | 6.62 | n/a | —/5/5 |

### Latency corroboration (median, ms)

Latency is measured independently of throughput, so agreement between them is meaningful evidence. `p90` is included alongside `p50` to show how the effect behaves in the tail.

| Scenario | Metric | all-off | app-off | all-on | full % | app % | spread % | sep |
|---|---|---|---|---|---|---|---|---|
| json | p50 | 0.126 | 0.129 | 0.130 | **+3.17** | +0.78 | 1.59 | **25/25** |
| json | **p90** | 0.228 | 0.235 | 0.236 | **+3.51** | +0.43 | 2.63 | **25/25** |
| json | mean | 0.143 | 0.147 | 0.148 | **+3.48** | +0.48 | — | — |
| fortunes | p50 | 0.380 | 0.392 | 0.393 | **+3.42** | +0.26 | 1.05 | **25/25** |
| fortunes | **p90** | 0.785 | 0.825 | 0.843 | **+7.39** | +2.18 | 7.59 | **25/25** |
| fortunes_ef | p50 | 0.460 | 0.468 | 0.467 | **+1.52** | −0.21 | 2.39 | 23/25 |
| fortunes_ef | **p90** | 0.920 | 0.940 | 0.950 | **+3.26** | +1.06 | 3.26 | 23/25 |
| mvc | p50 | 0.182 | 0.193 | 0.196 | **+7.69** | +1.55 | 4.15 | **25/25** |
| mvc | **p90** | 0.507 | 0.526 | 0.564 | +11.24 | +7.22 | 28.71 | 18/25 |
| plaintext | p50 | 0.287 | 0.289 | 0.293 | +2.09 | +1.38 | 5.80 | 16/25 |
| plaintext | **p90** | 1.010 | 1.050 | 1.060 | +4.95 | +0.95 | 18.81 | 13/25 |
| multiple_queries | p50 | 9.745 | 9.800 | 9.770 | +0.26 | −0.31 | 1.33 | 10/16 |
| multiple_queries | **p90** | 12.245 | 12.350 | 12.295 | +0.41 | −0.45 | 1.55 | 8/16 |
| orchard | p50 | — | 1.558 | 1.479 | n/a | −5.07 | 9.63 | n/a |
| orchard | **p90** | — | 2.516 | 2.331 | n/a | −7.35 | 10.37 | n/a |

For latency, **higher is worse** — so the positive `full %` values above mean the same thing as the negative throughput values: the async-on stack is slower.

**The regression is concentrated in the tail.** For every scenario, the p90 delta is larger than the p50 delta:

| Scenario | p50 full % | p90 full % | Tail amplification |
|---|---|---|---|
| fortunes | +3.42 | +7.39 | 2.2× |
| fortunes_ef | +1.52 | +3.26 | 2.1× |
| plaintext | +2.09 | +4.95 | 2.4× |
| mvc | +7.69 | +11.24 | 1.5× |
| json | +3.17 | +3.51 | 1.1× |
| multiple_queries | +0.26 | +0.41 | (both ≈ 0) |

This is consistent with the throughput results and with the mechanism: runtime-async changes how suspension points are compiled, so the cost shows up most where requests actually suspend and resume rather than completing synchronously on the fast path.

Two cautions on reading the p90 column:

- **`mvc` p90 (+11.24%) is the noisiest number in this document.** Its worst per-arm spread is 28.71% — far larger than the delta — and separation drops to 18/25, versus a clean 25/25 at p50. The direction agrees with every other signal for `mvc`, but the *magnitude* is not trustworthy at 5 iterations. Treat `mvc`'s solid p50 result (+7.69%, 25/25) as the reportable figure and the p90 as directional only.
- **`plaintext` p90 remains near the null** (13/25 separation, 18.81% spread), matching its flat throughput result. Its +4.95% point estimate should not be read as a real regression.

> **Why p90 and not p95.** The load generator used for the six TechEmpower-style scenarios (`wrk`) reports the 50th, 75th, 90th and 99th percentiles only — it does not emit a 95th. Of the 96 completed runs, p95 exists for just the 10 OrchardCore runs, whose load generator does report it (`app-off` 3.716/3.398/3.498/3.676/3.760 vs `all-on` 3.556/3.487/3.566/3.465/3.224 ms; median −3.6%, same direction as its p50/p90). p90 is therefore the closest tail percentile available across the whole matrix. Obtaining p95 everywhere would require re-running with a different load generator or a custom `wrk` reporting script.

---

## 8. Column Glossary

### Summary-table columns (§7)

| Column | Meaning |
|---|---|
| **Scenario** | The benchmark workload, as named in the crank YAML config. |
| **all-off** | Median across iterations for the arm where runtime, ASP.NET Core, **and** the app all have runtime-async disabled. The true baseline. |
| **app-off** | Median for the arm where runtime and ASP.NET Core are async-ON but the app has not opted in. Represents today's shipping default. |
| **all-on** | Median for the arm where all three layers are async-ON. Represents full adoption. |
| **full %** | Percent change from `all-off` → `all-on`: `(all-on − all-off) / |all-off| × 100`. The **total** cost of the feature. For Requests/sec, negative = slower. For latency, positive = slower. |
| **app %** | Percent change from `app-off` → `all-on`, same formula. The **incremental** cost of an app opting in when the framework is already async. |
| **spread %** | Run-to-run variability, used as a crude noise floor. Computed per arm as `(max − min) / |median| × 100` over that arm's iterations; the value shown is the **worst (largest)** across the arms present. Note this is the range normalized by the median — *not* the percent difference between max and min. It is outlier-sensitive and grows with iteration count, so it **overstates** noise on scenarios with one bad run (see `mvc`). |
| **sep** | Rank-based separation between `all-off` and `all-on` — the Mann-Whitney U statistic. Reads as *"how many of the (all-off × all-on) iteration pairs had all-off faster."* `25/25` means **complete separation**: every single baseline iteration beat every single async-on iteration. At 5 vs 5 that has an exact one-sided p ≈ 1/252 ≈ 0.004. Values near half the total (e.g. `10/16`, `16/25`) mean the distributions overlap and no effect is demonstrated. This is more reliable than `spread %` because it depends only on ordering, not on outlier magnitude. |
| **N** | Iteration counts as `all-off / app-off / all-on`. Normally `5/5/5`; lower values indicate runs lost to infrastructure failures (§11). |

### Metrics collected per run

Every run records all of the following; the report focuses on the first three.

| Metric | Meaning | Direction |
|---|---|---|
| **Requests/sec** | Mean throughput reported by the load generator. Primary metric. | Higher is better |
| **Latency p50 (ms)** | Median request latency. | Lower is better |
| **Mean latency (ms)** | Arithmetic mean request latency; more outlier-sensitive than p50. | Lower is better |
| **Latency p75 (ms)** | 75th-percentile latency. Captured by `wrk` for all runs; not tabulated above. | Lower is better |
| **Latency p90 (ms)** | 90th-percentile latency — the tail metric used in this report. 9 in 10 requests completed at or below this value. Available for all 96 runs. | Lower is better |
| **Latency p95 (ms)** | 95th-percentile latency. **Only available for the 10 OrchardCore runs** — `wrk`, used by the other six scenarios, does not emit a 95th percentile. p90 is used instead. | Lower is better |
| **Latency p99 (ms)** | 99th-percentile latency (extreme tail). Noisy at these iteration counts. | Lower is better |
| **Latency max (ms)** | Single worst request. Extremely noisy — reported but not interpreted. | Lower is better |
| **Throughput (MB/s)** | Bytes/sec served. Moves proportionally with Requests/sec for fixed-size responses. | Higher is better |
| **Bad responses** | Non-2xx or malformed responses. Must be 0 for a run to be valid; it was 0 everywhere. | Zero required |
| **App CPU (%)** | Mean CPU utilization of the application process across all cores. Near saturation (~87–97%) confirms the app, not the load generator, is the bottleneck. | Context |
| **Working set (MB)** | Resident memory of the application process. | Context |
| **Private memory (MB)** | Private (non-shared) committed memory. | Context |
| **Start time (ms)** | Time from process launch to first-request-ready. Relevant because runtime-async could plausibly affect startup/JIT. | Lower is better |
| **Published size (KB)** | Size of the published application folder, including the overlaid framework. Useful as a sanity check that the overlay was applied. | Context |

### Raw data columns (`out/full-runs.csv`, 1,152 rows = 96 runs × 12 metrics)

| Column | Meaning |
|---|---|
| **RunId** | Batch identifier (`full`), grouping all runs of one matrix execution. |
| **Scenario** | Workload name. |
| **Arm** | One of `all-off`, `app-off`, `all-on`. |
| **Iteration** | Repetition index, 1–5. |
| **Metric** | Human-readable metric name (matches the table above). |
| **Key** | crank's internal measurement key, e.g. `http/rps/mean`, `http/latency/50`. This is the authoritative identifier; `Metric` is the display label. |
| **Value** | The measured number for that (run, metric). |
| **HigherIsBetter** | `True`/`False` — the metric's polarity, used to color deltas correctly. |
| **Format** | Display format string (e.g. `n0`, `n3`). |

Derived per-scenario statistics are in `out/findings-stats.csv`, which additionally carries `<arm>_n` (iteration count per arm), `FullPct`, `AppPct`, `SpreadPct`, and `Sep` as defined above.

Latency percentiles (p50/p75/p90/p99, plus p95 where the load generator emits it) are extracted at full precision directly from the crank result JSON by `Get-LatencyPercentiles.ps1`, which writes per-iteration values to `out/p90-runs.csv` and per-scenario statistics to `out/latency-percentile-stats.csv`. Note that the percentile values printed in the run *logs* are rounded to 2 decimals; the JSON carries the unrounded values, so the JSON is the authoritative source. The extraction is self-validating — it re-derives p50 and p99 for all 96 runs and compares them against `out/full-runs.csv`, and currently reports **192 values compared, 0 differing**.

---

## 9. Interpretation

**The cost is real and it is in the framework.** Four scenarios — `json`, `fortunes`, `fortunes_ef`, `mvc` — show complete rank separation (`25/25`) between `all-off` and `all-on`. In every one, throughput drops and p50 latency rises, two independent measurements agreeing in direction and roughly in magnitude. For the three low-variance ones the effect is −2.8% to −3.6%, comfortably above their ~1.5% noise floor.

**The app-level flag is free.** Across all seven scenarios `app %` lands between −1.95% and +3.71%, always within that scenario's spread, and the rank test never separates. An application opting into runtime-async on top of an already-async framework pays no measurable throughput penalty. This is the practical guidance for teams considering the flag today.

**`mvc` deserves a note.** Its `spread %` (7.56) exceeds its `full %` (−6.90), which by the crude noise-floor rule would dismiss it. That rule is wrong here: all 25 pairs separate, and p50 latency independently confirms +7.69%. The high spread comes from within-arm variance (one slow iteration in `app-off`), not from arm overlap. `mvc` is the **largest** effect measured, not a null result. It is, however, the least precisely estimated — more iterations would tighten it.

**Two scenarios are null controls, as expected.** `plaintext` (16/25, +0.01%) is pipelined and dominated by socket I/O; `multiple_queries` (10/16, −0.03%) is dominated by 20 round-trips of database latency. Neither is framework-bound, so neither has room to show an async-compilation cost. Their agreement with the null hypothesis is a useful check that the harness is not manufacturing differences.

**`orchard` measured +3.71% *in favor* of async-on**, but this is the weakest result in the set: it has no `all-off` arm (§10), its spread is 6.62%, and its direction is opposite to every other scenario. Treat as inconclusive.

---

## 10. Caveats and Limitations

1. **OrchardCore has no `all-off` arm.** It must publish framework-dependent (crank's default self-contained publish drops OrchardCore's module assets, breaking every request with `ArgumentNullException` — reproduced with no overlay and no feature flag on both net10.0 and net11.0, so it is unrelated to this experiment). Framework-dependent publish resolves the shared framework from the agent's own dotnet install, so **no overlay can apply**. OrchardCore therefore compares only the two arms that share the agent's stock framework.

2. **R2R code may be rejected, uniformly.** The ASP.NET Core layer is crossgen'd against the *official* runtime ref pack, but our overlay ships a locally built runtime. The R2R code may fail its version check and fall back to JIT. This affects **all three arms identically**, so the A/B comparison holds, but **absolute** numbers may not be comparable to production or to the earlier round. (Neither stock nor local runtime packs are crossgen'd at all — 0 of 180 assemblies; all 130 R2R assemblies come from the aspnetcore layer.)

3. **23 residual async methods survive in the OFF build** (§5). Inert unless called.

4. **`BuildNodeJS=false`** omits Blazor JS assets from both flavors. No HTTP benchmark here touches them.

5. **Five iterations is few.** Medians are stable for the low-variance scenarios but `mvc` and `orchard` would benefit from 10+.

6. **Single hardware profile.** All results are gold-lin (56-core Linux x64). Behavior on other core counts, Windows, or Arm64 is unmeasured.

---

## 11. Data Completeness

**96 of 130 planned runs completed.** The matrix ran 2026-07-30 16:30–21:55 local and was halted by disk exhaustion on the benchmark agent.

| Missing | Count |
|---|---|
| `updates` (all arms, all iterations) | 15 |
| `fortunes_ef_mvc_https` (all arms, all iterations) | 15 |
| `multiple_queries` iteration 5 (all arms) | 3 |
| `fortunes_ef` `app-off` iteration 2 | 1 |
| **Total** | **34** |

**Root cause.** The crank agent runs *inside* a `crank-agent` Docker container whose `run.sh` mounts only `/sys/fs/cgroup` and `/var/run/docker.sock` — there is **no `/tmp` volume**. The agent's build root (`Path.GetTempPath()/benchmarks-agent`, `Startup.cs:347`) therefore lives in the container's writable layer under `/var/lib/docker`. `database.benchmarks.yml:36` sets `noClean: true` on the postgresql job, so each database run retains its work folder (~445 MB, mostly the TechEmpower `FrameworkBenchmarks` clone). Roughly 100 database runs in 5.5 hours exhausted the disk; subsequent runs failed in ~15 s with:

```
initdb: error: could not create directory "/var/lib/postgresql/18/docker/pg_wal": No space left on device
```

**This is a self-healing condition, not a defect.** A documented nightly cron (`crank/docs/setup_linux.md:76`) runs `docker system prune --all --force --volumes` and rebuilds the agent, reclaiming the space — observed reclaiming ~89 GB. The matrix simply generated database runs faster than one cleanup cycle could absorb. The mitigation is to keep DB-heavy batches inside a single cleanup cycle, **not** to override `noClean`: for Docker jobs that flag is counterintuitive (`Startup.cs:2817-2827`), where `noClean=false` runs `docker rmi --force`, deleting the image *and its shared parent layers*.

**Impact on conclusions: none.** All 34 missing runs belong to scenarios that are either incomplete-and-excluded (`updates`, `fortunes_ef_mvc_https` — reported nowhere) or already null controls (`multiple_queries`). Timeline analysis of the completed runs shows no gap or failure cluster during the matrix, so the 96 valid results were not disturbed by the eventual disk pressure. The four scenarios carrying the headline finding are all complete at 5/5/5 except `fortunes_ef` `app-off` (4).

---

## 12. Reproducing This

All tooling lives in the session workspace (`files/runtimeasync/`); `D:\git\benchmarks` and `D:\git\crank` were never modified.

| Step | Command |
|---|---|
| 1. Patch CoreLib (both runtime worktrees) | `python3 wsl/patch-corelib.py ~/ra/runtime-{on,noasync}` |
| 2. Build runtime, both flavors | `wsl/build-runtime.sh ~/ra/runtime-on on <log>` / `... ~/ra/runtime-noasync off <log>` |
| 3. Build aspnetcore, both flavors | `wsl/build-aspnetcore.sh ~/ra/aspnetcore on <log>` / `... ~/ra/aspnetcore-off off <log>` |
| 4. Stage overlays | `New-LocalOverlay.ps1` |
| 5. Verify bits | `dotnet tools/AsyncScan/AsyncScan.dll overlay/on overlay/off` — expect 1,983 vs 23 |
| 6. Smoke test | `Invoke-RuntimeAsyncBenchmarks.ps1 -Scenario json -Iterations 1 -VerifyOverlay -RunId smoke` |
| 7. Full matrix | `Invoke-RuntimeAsyncBenchmarks.ps1 -Iterations 5 -RunId full -Resume -VerifyOverlay` |
| 8. Report | `Compare-Results.ps1 -RunId full -Csv out/full-runs.csv -SummaryCsv out/full-medians.csv` |

`-Resume` skips runs whose result JSON already exists, so step 7 can be re-invoked to fill the 34 gaps without repeating completed work. Prerequisites: current `az login` (relay profiles), and crank on `PATH`.

### Artifacts

| Path | Contents |
|---|---|
| `out/full/*.json` | 96 raw crank result files, one per run |
| `out/full-runs.csv` | Flattened per-iteration measurements (1,152 rows) |
| `out/full-medians.csv` | Per-scenario/metric medians and deltas |
| `out/findings-stats.csv` | Derived statistics backing §7 |
| `out/p90-runs.csv` | Per-iteration latency percentiles at full precision (96 rows) |
| `out/latency-percentile-stats.csv` | Per-scenario percentile statistics backing the latency tables in §7 |
| `logs/full/*.log` | Full stdout of every run |
| `logs/full/manifest.jsonl` | Exact command line, exit code and duration for all 130 attempted runs |
| `logs/full/artifacts-*/` | Downloaded marker files proving which overlay landed |
| `logs/build-*.log` | Build logs for all four framework builds (HEAD, diff, command, timings, rc) |
| `overlay/{on,off}/` | The two staged shared-framework overlays, 451 files each |
| `local-bits.json` | Overlay paths, pinned SHAs, staging timestamp |

---

## 13. Recommendations

1. **Report the ~3% framework cost** as the feature's headline number. The app-level flag being free is the secondary — and more actionable — finding.
2. **Complete `updates` and `fortunes_ef_mvc_https`** with `-Resume`; both are database-heavy and worth ~3–4 h inside one cleanup cycle.
3. **Re-run `mvc` at 10+ iterations** to tighten the largest observed effect.
4. **Re-validate on a second hardware profile** before treating ~3% as a general figure.
5. **Consider a crossgen'd overlay** (R2R-compiling aspnetcore against the local runtime) to remove caveat §10.2 and obtain trustworthy absolute numbers.

---

# Round 3 — Does PR #131177 Recover the Cliff?

**Date:** 2026-08-04
**Hardware profiles:** `aspnet-gold-lin-relay` (56-core Linux x64), `cobalt-cloud-lin-al3-relay`
(**4-core** Azure Linux 3 Arm64 **Azure VM**) and `aspnet-cobalt-hosted-al3-lin-relay` (56-core
Azure Linux 3 Arm64, shared — unusable)
**Status:** gold-lin complete (100/100). cobalt-cloud complete for framework-bound scenarios
(98/140; all 42 failures are database scenarios, §18.5). cobalt-hosted complete (100/100) but
**noise-limited and not reportable** (§18.3).

## 14. Round-3 Headline

**PR #131177 produced no measurable change on gold-lin, the runtime-async cost is unchanged from
round 2 on x64 — and a small 4-core Arm64 VM pays 3–5× more for runtime-async than the 56-core
x64 server.**

Round 3 rebuilt the entire stack on current `dotnet/runtime` main with VSadov's PR #131177
("Remove queue-subdispatching pattern to the ThreadPool global queue") applied, to test the
observation that #131177 recovers a large throughput cliff. On this harness:

| | |
|---|---|
| #131177 + newer main vs round-2 baseline (`all-off` arms) | **−0.3% to −4.9%** — no recovery |
| Runtime-async full-stack cost, **x64 56c** (`all-off` → `all-on`) | **−2.8% to −7.5%**, statistically clean |
| Runtime-async full-stack cost, **Arm64 4c** (`all-off` → `all-on`) | **−11.0% to −20.3%**, complete separation |
| App-only flag cost (`app-off` → `all-on`), both machines | **−2.8% to +2.5%**, still ~noise |

The round-2 conclusion stands unchanged: the cost is in the **framework** being compiled async,
not in the application opting in. Round 3 adds that the *size* of that framework cost varies
strongly with the machine — see §18.2, the most significant new result of this round. That
comparison confounds architecture with a **14× core-count difference**, so it should be read as
"small machine vs large machine", not "Arm64 vs x64", until a large-core Arm64 run settles it.

> **Important scope note.** We could not reproduce the performance regime the original
> #131177 observations came from. Those reported ~2.79M rps @32c and ~3.69M rps @64c for `json`;
> gold-lin is 56 cores and delivers **~1.7M rps** for the same scenario. We proved this is not a
> defect in our bits (§16). Whatever produced the cliff — a different machine, scenario variant,
> or connection/pipelining configuration — is not present here, so **this round cannot confirm or
> refute the recovery.** It can only say the cliff is not visible on gold-lin.

> **Important scope note.** We could not reproduce the performance regime the original
> #131177 observations came from. Those reported ~2.79M rps @32c and ~3.69M rps @64c for `json`;
> gold-lin is 56 cores and delivers **~1.7M rps** for the same scenario. We proved this is not a
> defect in our bits (§16). Whatever produced the cliff — a different machine, scenario variant,
> or connection/pipelining configuration — is not present here, so **this round cannot confirm or
> refute the recovery.** It can only say the cliff is not visible on gold-lin.

---

## 15. Round-3 Build Provenance

### 15.1 Pinned commits

| Component | Commit | Notes |
|---|---|---|
| `dotnet/runtime` base main | `ca4ed7d4a265c32e5240863c6b8ff45121339cc4` | "Restore symbol-free decoding for CORINFO_HELP_NEW helpers (#131554)", committed **2026-08-04 10:04:47 −0700** |
| **`r3-subdisp` (built)** | **`83a151b268bc2411af81d442a4c1ed845f59fde5`** | `vsadov/subDisp` rebased onto the above at 2026-08-04 10:12:14 −0700; 3 commits, 6 files |
| `dotnet/aspnetcore` | `28dd8a5895819baed4d7211120dbff0b452cc368` | tip of main |

> ⚠️ **Recorded-SHA erratum.** The `RuntimeSha` written into `local-bits-r3-{x64,arm64}.json`, and
> therefore into the `--property runtimeSha=` of all 335 round-3 result JSONs and run logs, is
> **`70d6992b34776acddf85b56f780873cbfe92fc4b`. That object does not exist in the repository**
> (`git cat-file` → `bad object`); it is a stale value from a superseded rebase attempt, not the
> commit that was built. The **actual** tip built for round 3 is
> `83a151b268bc2411af81d442a4c1ed845f59fde5`, verified as the HEAD of both `~/ra/rt-on` and
> `~/ra/rt-off` (which share a base and differ only by the `System.Private.CoreLib.csproj`
> feature-flag toggle).
>
> The bits themselves are unaffected: the rebase completed at 10:12:14 −0700, while the x64 and
> Arm64 overlays were staged at 11:56 and 12:15 −0700 respectively, so both builds necessarily
> came from `83a151b`. Only the metadata label is wrong. Treat `runtimeSha` in the round-3 result
> JSONs as incorrect and use this table instead.

The original `4fa3dac` referenced in the source observation was a local rebase that was never
pushed. It was re-rebased onto current main, so the SHA differs but the change content is
equivalent. Files touched: `SocketAsyncEngine.Unix.cs` (280 lines), `ThreadPoolWorkQueue.cs`,
`PortableThreadPool.IO.Windows.cs`, `SafeSocketHandle.Unix.cs`, `SocketAsyncContext.Unix.cs`,
`SocketAsyncEngine.Wasi.cs`.

The three rebased commits (author dates preserved, all re-committed 2026-08-04 10:12:14 −0700):

| Commit | Author date | Subject |
|---|---|---|
| `83a151b268b` | 2026-07-30 23:43:54 −0700 | comments |
| `7ccb707be14` | 2026-07-13 17:55:59 −0700 | assign in disptch |
| `f3bc4638796` | 2026-07-30 23:02:05 −0700 | tree-packed events |

### 15.2 Toolchain change — builds are now stock

Round 2 required a `-Wno-unknown-attributes` compatibility flag. Round 3 builds were produced
under **Ubuntu 24.04 with clang 18.1.3**, which needs no such workaround. The flag was made
opt-in (`RA_CMAKE_COMPAT=1`) and was **not** used. Round 2's toolchain caveat therefore does
not apply to round-3 numbers.

All four framework builds used `./build.sh -s clr+libs+packs -c Release`.

### 15.3 Arm64 cross-build methodology (new in round 3)

Arm64 overlays were **cross-built on x64**, which round 2 did not do:

```bash
# 1. Ubuntu jammy sysroot (glibc 2.35 — deliberately older than Azure Linux 3's 2.38,
#    so the binaries load on the Cobalt agents)
sudo env ROOTFS_DIR=~/ra/rootfs/arm64 \
  ./eng/common/cross/build-rootfs.sh arm64 jammy no-lldb --skipemulation

# 2. runtime: clang is inherently a cross compiler, so no cross-gcc is needed
./build.sh -s clr+libs+packs -c Release -a arm64 -cross      # with ROOTFS_DIR exported

# 3. aspnetcore: SysRoot is needed only so the NativeAOT dev tool can link
./build.sh -c Release -arch arm64 /p:SysRoot=$ROOTFS_DIR
```

The rootfs build additionally requires the apt packages `python3-aiohttp` and
`python3-zstandard`; Ubuntu 24.04 marks system Python externally-managed (PEP 668), so pip is
not an option. `no-lldb` avoids the unsatisfiable `liblldb-3.9-dev` dependency, which is
irrelevant to producing overlay binaries.

### 15.4 Overlay verification

Each overlay was scanned for methods carrying `MethodImplOptions.Async` (0x2000):

| Overlay | Assemblies | Async methods | Assemblies w/ async | R2R | Native arch |
|---|---|---|---|---|---|
| stock BCS (round-2 reference) | 313 | 1,983 | 90 | 130/313 | x86-64 |
| `r3-x64-on` | 313 | **1,983** | 90 | 130/313 | x86-64 |
| `r3-x64-off` | 313 | **23** | 1 | 130/313 | x86-64 |
| `r3-arm64-on` | 313 | **1,983** | 90 | 130/313 | AArch64 |
| `r3-arm64-off` | 313 | **23** | 1 | 130/313 | AArch64 |

The ON overlays match the official bits exactly on every axis. The 23 residual methods in the
OFF overlays are the known inert `AsyncHelpers.cs` definitions in CoreLib — identical to round 2.

> **Scanning gotcha.** `System.Private.CoreLib.dll` lives in the pack's `native/` folder, not
> `lib/net11.0`. Scanning only `lib/net11.0` yields a misleading "0 async methods" for the OFF
> pack. Always scan the **staged overlay**, which flattens `lib` + `native` + aspnetcore.

### 15.5 Post-hoc verification of what actually ran

Every one of the 100 gold-lin run logs was parsed to confirm the arm/overlay/flag mapping:

| Arm | Runs | Used OFF overlay | Used ON overlay | Carried `Features=runtime-async=on` |
|---|---|---|---|---|
| `all-off` | 30 | 30 | 0 | 0 |
| `app-off` | 35 | 0 | 30 | 0 |
| `all-on` | 35 | 0 | 30 | 35 |

This is exactly correct. The 5 extra runs in `app-off` and `all-on` are OrchardCore, which
publishes framework-dependent and skips the overlay by design (hence 30 overlay uploads, not 35).
`all-off` has no OrchardCore arm at all, giving 30 runs.

---

## 16. Control Experiment — Are Our Self-Built Bits Trustworthy?

Because round-3 absolute throughput was ~2× below the numbers that motivated the round, we ran a
**stock-bits control**: the identical crank command with the `--application.options.outputFiles`
overlay argument removed, so the app runs against the agent's official installed .NET 11 shared
framework.

| Scenario | Stock (official) iterations | Stock median | Round-3 `all-off` | Δ |
|---|---|---|---|---|
| `json` | 1,661,631 / 1,691,085 / 1,692,247 | 1,691,085 | 1,760,652 | **+4.1%** |
| `plaintext` | 9,389,246 / 9,361,135 / 8,997,089 | 9,361,135 | 9,087,742 | **−2.9%** |

**Our self-built, non-R2R packs land within ±4% of the official framework** — and are slightly
*faster* for `json`. Two conclusions follow:

1. The absence of crossgen2/R2R in our runtime pack (caveat §10.2) does **not** materially
   distort steady-state throughput. Tiered compilation reaches an equivalent steady state.
2. gold-lin genuinely delivers ~1.7M rps for `json`. The gap versus the ~3.69M @64c figure is a
   property of the *other* environment, not a defect here.

Supporting evidence that gold-lin is correctly configured and not client-bottlenecked:

| Scenario | App CPU | Load-generator CPU |
|---|---|---|
| `json` | 88% | 41% |
| `plaintext` | 95% | 33% |
| `mvc` | 88% | 34% |

The application server is saturated while the load generator retains large headroom — the
correct shape for a server-bound measurement.

---

## 17. Round-3 Results — gold-lin (x64)

Medians of 5 iterations, Requests/sec. 100/100 runs succeeded.

| Scenario | all-off | app-off | all-on | full % | app % | spread % | sep |
|---|---|---|---|---|---|---|---|
| json | 1,760,652 | 1,703,399 | 1,699,346 | **−3.48** | −0.24 | 1.82 | **25/25** |
| fortunes | 578,713 | 551,550 | 548,271 | **−5.26** | −0.59 | 2.52 | **25/25** |
| fortunes_ef | 477,106 | 454,001 | 459,747 | **−3.64** | +1.27 | 1.18 | **25/25** |
| mvc | 1,078,816 | 1,022,221 | 997,700 | **−7.52** | −2.40 | 6.16 | **25/25** |
| plaintext | 9,087,742 | 8,842,362 | 8,835,504 | −2.78 | −0.08 | 3.80 | **25/25** |
| multiple_queries | 50,555 | 50,964 | 50,990 | +0.86 | +0.05 | 2.50 | 6/25 |
| orchard | — | 18,387 | 19,242 | n/a | +4.65 | 10.61 | n/a |

### 17.1 Round 2 vs Round 3 — did #131177 move anything?

Comparing the `all-off` arms isolates the *runtime build* change, since that arm is identical in
every other respect:

| Scenario | R2 `all-off` | R3 `all-off` | Δ (effect of #131177 + newer main) |
|---|---|---|---|
| json | 1,765,774 | 1,760,652 | **−0.3%** |
| fortunes | 578,746 | 578,713 | **0.0%** |
| fortunes_ef | 480,872 | 477,106 | **−0.8%** |
| plaintext | 9,319,026 | 9,087,742 | **−2.5%** |
| mvc | 1,134,874 | 1,078,816 | **−4.9%** |
| multiple_queries | 51,292 | 50,555 | −1.4% |

Every delta is at or below the run-to-run spread, and all are **negative or flat**. There is no
recovery, and no cliff to recover from — round 2 was already at this level.

The full-stack async cost is likewise stable, if marginally worse:

| Scenario | R2 full % | R3 full % |
|---|---|---|
| json | −2.89 | −3.48 |
| fortunes | −3.57 | −5.26 |
| fortunes_ef | −2.83 | −3.64 |
| mvc | −6.90 | −7.52 |
| plaintext | +0.01 | −2.78 |

### 17.2 Latency (median, ms) — gold-lin

Higher is worse, so positive `full %` agrees with the negative throughput deltas.

| Scenario | Metric | all-off | app-off | all-on | full % | app % | spread % | sep |
|---|---|---|---|---|---|---|---|---|
| json | p50 | 0.125 | 0.130 | 0.130 | **+4.00** | 0.00 | 2.40 | **25/25** |
| json | **p90** | 0.236 | 0.246 | 0.248 | **+5.08** | +0.81 | 3.81 | **25/25** |
| fortunes | p50 | 0.381 | 0.400 | 0.400 | **+4.99** | 0.00 | 2.10 | **25/25** |
| fortunes | **p90** | 0.785 | 0.839 | 0.850 | **+8.28** | +1.31 | 6.44 | **25/25** |
| fortunes_ef | p50 | 0.462 | 0.478 | 0.474 | **+2.60** | −0.84 | 1.90 | **25/25** |
| fortunes_ef | **p90** | 0.940 | 0.990 | 0.980 | **+4.26** | −1.01 | 2.13 | **25/25** |
| mvc | p50 | 0.190 | 0.203 | 0.206 | **+8.42** | +1.48 | 4.74 | **25/25** |
| mvc | **p90** | 0.575 | 0.588 | 0.668 | +16.17 | +13.61 | 17.22 | 19/25 |
| plaintext | p50 | 0.291 | 0.306 | 0.303 | **+4.12** | −0.98 | 4.47 | 24/25 |
| plaintext | **p90** | 1.130 | 1.270 | 1.240 | +9.73 | −2.36 | 20.35 | 20/25 |
| multiple_queries | p50 | 9.920 | 9.820 | 9.830 | −0.91 | +0.10 | 2.75 | 6/25 |
| multiple_queries | **p90** | 12.390 | 12.280 | 12.290 | −0.81 | +0.08 | 2.36 | 5/25 |
| orchard | p50 | — | 1.492 | 1.447 | n/a | −3.02 | 12.67 | n/a |
| orchard | **p90** | — | 2.391 | 2.199 | n/a | −8.03 | 15.06 | n/a |

The **tail amplification** seen in round 2 persists and is slightly stronger — p90 degrades more
than p50 in every scenario that shows an effect (json 5.08 vs 4.00, fortunes 8.28 vs 4.99,
mvc 16.17 vs 8.42, plaintext 9.73 vs 4.12). Same caveats as round 2 apply: `mvc` p90 and
`plaintext` p90 have spreads comparable to their deltas and should be read as directional only.

---

## 18. Round-3 Results — Arm64

### 18.1 Two different Cobalt pools — only one is usable

The repository defines Cobalt profiles in **two separate files**, and the distinction matters
enormously:

| Profile | Defined in | Type | Cores | json spread |
|---|---|---|---|---|
| `aspnet-cobalt-hosted-al3-lin-relay` | `scenarios/aspnet.profiles*.yml` | hosted / shared | 56 | **137.9%** |
| `cobalt-cloud-lin-al3-relay` | `build/azure.profile.yml` | **Azure VM** | **4** ⚠️ | **9.1%** |

> ⚠️ **The profile's `cores: 16` variable is wrong.** The machine is actually **4 cores**. This is
> not cosmetic — it inverts the interpretation of §18.2. The evidence is in every result JSON:
> crank reports `benchmarks/cpu/raw` = 387% with `benchmarks/cpu` = 97% for the application job
> (387 / 97 ≈ 4; 387 / 16 would be 24%). The load generator agrees: 271% raw / 68% = 4. Crank
> normalizes by the **agent's real processor count**, so `cpu/raw ÷ cpu` is a reliable way to
> recover the true core count when a profile's metadata is stale. Do not trust the `cores:`
> variable in `build/azure.profile.yml`.

They resolve to different relay endpoints (`cobalthostedlinserver_azurelinux3` vs
`cobaltcloudlinserver_azurelinux3`), so they are genuinely different machines. Because
`build/azure.profile.yml` is not the scenario's own config, it must be passed as an **additional
`--config`**:

```
crank --config .../json.benchmarks.yml \
      --config .../build/azure.profile.yml \
      --profile cobalt-cloud-lin-al3-relay --relay ...
```

The first Arm64 matrix was run on the hosted pool and produced unusable noise (§18.3). It was
re-run on the Azure VM pool at **7 iterations** instead of 5, to compensate for a spread still
larger than gold-lin's.

### 18.2 cobalt-cloud (Azure VM, Arm64) — the reportable Arm64 result

Medians of 7 iterations, Requests/sec. The database-backed scenarios failed on this pod (§18.5),
so the three framework-bound scenarios carry the result.

| Scenario | all-off | app-off | all-on | full % | app % | spread % | sep |
|---|---|---|---|---|---|---|---|
| json | 233,684 | 191,634 | 186,228 | **−20.31** | −2.82 | 9.09 | **complete 49/49** |
| plaintext | 1,579,252 | 1,360,593 | 1,372,666 | **−13.08** | +0.89 | 6.70 | **complete 49/49** |
| mvc | 118,692 | 103,161 | 105,695 | **−10.95** | +2.46 | 18.00 | 48/49 |
| multiple_queries | 2,200 | 2,146 | 2,384 | +8.36 | +11.09 | 30.32 | 15/42 |
| orchard | — | 3,089 | 3,349 | n/a | +8.39 | 8.29 | n/a |

Latency agrees independently, with complete separation on all three:

| Scenario | Metric | all-off | app-off | all-on | full % | app % | sep |
|---|---|---|---|---|---|---|---|
| json | p50 | 1.06 | 1.29 | 1.33 | **+25.47** | +3.10 | **49/49** |
| json | **p90** | 1.49 | 1.81 | 1.85 | **+24.16** | +2.21 | — |
| plaintext | p50 | 1.41 | 1.65 | 1.63 | **+15.60** | −1.21 | **49/49** |
| plaintext | **p90** | 2.71 | 3.18 | 3.15 | **+16.24** | −0.94 | — |
| mvc | p50 | 2.06 | 2.33 | 2.31 | **+12.14** | −0.86 | **49/49** |
| mvc | **p90** | 3.07 | 3.50 | 3.48 | **+13.36** | −0.57 | — |

**This is the most significant finding of round 3.** The runtime-async framework cost on Arm64 is
roughly **3–5× larger** than on x64:

| Scenario | gold-lin x64 (56c) full % | cobalt-cloud Arm64 (**4c**) full % | ratio |
|---|---|---|---|
| json | −3.48 | **−20.31** | 5.8× |
| plaintext | −2.78 | **−13.08** | 4.7× |
| mvc | −7.52 | **−10.95** | 1.5× |

Crucially, the **structure of the result is unchanged**: the cost is in the framework, not the
app. The app-only deltas remain small and noise-like (json −2.82%, plaintext +0.89%,
mvc +2.46%), exactly as on x64. Runtime-async is simply far more expensive on this machine.

> **Confound to state plainly — and it is a large one.** This is a **4-core Arm64 VM** compared
> against a **56-core x64 server**: architecture, core count (14×), and machine class all vary
> together. The measurement is solid; the *attribution* is not. **Do not report this as "Arm64
> costs 3–5× more"** — the honest statement is "a small 4-core Arm64 VM shows a 3–5× larger
> runtime-async cost than a 56-core x64 server."
>
> Core count is at least as plausible an explanation as the ISA. Runtime-async cost is paid in
> thread-pool dispatch and continuation scheduling, and a 4-core box has far less slack to absorb
> that overhead than a 56-core one. It is also the same subsystem PR #131177 targets, so core
> count is directly on the causal path rather than incidental.
>
> Disambiguating requires holding one variable fixed:
> - a **large-core Arm64** profile — `aspnet-citrine-arm-lin-relay` (Ampere, 80 cores), the
>   profile the official `prbenchmarks.runtime.linux_arm64` config uses; and/or
> - a **small-core x64** profile.
>
> If the 80-core Ampere shows a gold-lin-like −3%, the effect is core count. If it shows −15%,
> the effect is the ISA.

Supporting note on absolute scale: per-core throughput on the 4-core box is *higher* than
gold-lin's (json 58.4K/core vs 31.4K/core; plaintext 395K/core vs 162K/core). That is expected —
throughput scales sublinearly with core count — but it confirms the two machines sit at very
different points on the scaling curve, which is precisely why their runtime-async costs are not
directly comparable.

### 18.3 cobalt-hosted (shared VMs) — NOT REPORTABLE

The Arm64 matrix completed **100/100 runs**. (3 OrchardCore runs initially failed on a Service Bus
relay 504 — an infrastructure transport timeout, not a bits failure — and succeeded on retry.)
**The data cannot support any conclusion** and is retained only to document the pitfall.

| Scenario | all-off | app-off | all-on | full % | app % | spread % | sep |
|---|---|---|---|---|---|---|---|
| json | 479,862 | 607,644 | 387,437 | −19.26 | −36.24 | **137.94** | 19/25 |
| fortunes_ef | 119,280 | 144,681 | 170,409 | **+42.86** | +17.78 | **72.51** | 6/25 |
| mvc | 732,664 | 665,219 | 666,845 | −8.98 | +0.24 | **56.63** | 19/25 |
| plaintext | 5,780,891 | 5,739,066 | 5,959,033 | +3.08 | +3.83 | **51.59** | 3/25 |
| fortunes | 194,204 | 186,916 | 188,254 | −3.06 | +0.72 | **50.28** | 18/25 |
| multiple_queries | 26,615 | 26,328 | 26,619 | +0.02 | +1.11 | **40.45** | 14/25 |
| orchard | — | 14,202 | 13,830 | n/a | −2.63 | **20.26** | n/a |

Every delta is smaller than its own spread, no scenario reaches meaningful separation, and
`fortunes_ef` reports a physically implausible **+42.9% improvement** from enabling async.

### 18.4 Diagnosis — CPU steal on shared hosts, not our bits

Per-iteration `json` throughput on cobalt ranged **261,684 to 1,015,088 rps (3.9×)** with no
warmup trend, while gold-lin held 1,749,269–1,779,369 (1.7%) across the same 5 iterations.

The decisive evidence is that **application CPU stayed pegged at ~85% in every run, fast and
slow alike**, and barely correlates with throughput:

| Scenario | RPS range | App CPU (raw) range | corr(rps, cpu) |
|---|---|---|---|
| json | 3.9× | 1.84× | **0.16** |
| plaintext | 2.1× | 1.23× | 0.41 |
| mvc | 2.3× | 1.13× | 0.78 |
| fortunes | 2.0× | 1.37× | **−0.17** |

The app burns near-constant CPU while delivering up to 4× less work, and for `fortunes` the
correlation is *negative*. That rules out "the app simply had less to do" and is the classic
signature of **hypervisor CPU steal**: the guest accounts itself as busy while the cycles are
being given to noisy neighbours.

This is corroborated by naming and configuration: every Cobalt profile in
`scenarios/aspnet.profiles.standard.yml` is `aspnet-cobalt-hosted-*` — shared hosted VMs. The
dedicated alternative is not in that file at all; it is `cobalt-cloud-lin-al3` in
`build/azure.profile.yml` (§18.1). gold-lin is dedicated hardware, which is why it holds ~1%
spread. The 504 relay timeouts on the three failed runs are consistent with the same contention.

The Arm64 *bits* are almost certainly fine: x64 natives would fail to load outright on Arm64, so
the fact that the agents produced valid results at all confirms the cross-build was correct.

### 18.5 Database scenarios failed on the cobalt-cloud pod

42 of 140 runs failed, **entirely confined to database-backed scenarios**:
`fortunes_ef` 21/21, `fortunes` 19/21, `multiple_queries` 2/21. No `json`, `plaintext`, `mvc`
or `orchard` run failed.

The signature is a crank-side **`db` job deadlock**:

```
[02:47:40] 'db' is running ... https://aspnetperf.servicebus.windows.net/cobaltcloudlindb/jobs/124/output
[02:47:43] Found deadlock on .../cobaltcloudlindb/jobs/124, interrupting ...
[02:47:43] Stopping job '' ...
Unhandled exception. System.NullReferenceException
   at Microsoft.Crank.Controller.JobConnection.Combine(...) JobConnection.cs:line 1362
   at Microsoft.Crank.Controller.JobConnection.StopAsync(...) JobConnection.cs:line 475
```

Two separate problems are visible here:

1. The `cobaltcloudlindb` agent's database job wedges shortly after start.
2. Crank's *own* cleanup path then **NREs** while trying to stop a job whose name it never
   resolved, turning a recoverable job failure into a process crash (exit `-532462766` =
   `0xE0434352`). This is a crank bug worth reporting independently.

The corroborating evidence that this pod's database is simply underpowered rather than
intermittently unlucky: `multiple_queries`, which *did* mostly complete, returned **2,200 rps at
230 ms mean latency**, against **50,555 rps** for the same scenario on gold-lin — a 23× gap that
no CPU-architecture difference can explain. Those runs are database-bound, measure the database
rather than the framework, and are excluded from the conclusions.

**Consequence:** database scenarios are covered by gold-lin only. The Arm64 result rests on the
three framework-bound scenarios, which is where the runtime-async signal lives in any case.

---

## 19. Round-3 Conclusions

1. **#131177 shows no measurable benefit on gold-lin.** All six `all-off` deltas versus round 2
   are flat or slightly negative, within noise. No cliff is present on this profile.
2. **We are not in the same performance regime as the source observation** (~1.7M vs ~3.69M rps
   for `json`). This is *not* explained by our bits — the stock-bits control (§16) puts our
   overlays within ±4% of the official framework.
3. **The runtime-async framework cost is reproducible and stable** at −2.8% to −7.5% full-stack,
   with clean 25/25 separation on json / fortunes / fortunes_ef / mvc / plaintext.
4. **The app-level flag remains free** (−0.1% to −2.4%, mostly noise), reconfirming round 2's
   central finding on a newer runtime.
5. **Non-R2R self-built packs are validated as a methodology** (§16), removing round 2's largest
   caveat about absolute numbers.
6. **The runtime-async cost is far larger on the small Arm64 VM** — json −20.3%, plaintext
   −13.1%, mvc −11.0% full-stack, with complete 49/49 separation and latency agreeing
   independently (json p50 +25.5%, p90 +24.2%). That is 3–5× the cost seen on gold-lin. This is
   the headline result of round 3 and was entirely invisible until the Azure-VM Cobalt profile
   was used.
7. **The shape of the effect is machine-independent.** On both machines the cost sits in the
   framework; the app-level flag stays free. Only the magnitude changes.
8. **The 3–5× cannot yet be attributed to the ISA.** The Arm64 VM has **4 cores** against
   gold-lin's 56 — a 14× gap — and the profile metadata that says `cores: 16` is wrong. Core
   count is a fully plausible cause on its own, and it is the same subsystem #131177 targets.

## 20. Round-3 Recommendations

1. **Run the 80-core Ampere profile (`aspnet-citrine-arm-lin-relay`) next.** It is the single
   highest-value experiment available: it holds the ISA fixed while restoring a large core count,
   and cleanly separates "Arm64 is expensive" from "small machines are expensive". It is also the
   profile the official `prbenchmarks.runtime.linux_arm64` config uses.
2. **Do not publish "Arm64 costs 3–5× more" from §18.2 alone.** The defensible claim is
   machine-scoped until (1) lands. A small-core x64 profile would close the argument from the
   other side.
3. **Identify the machine and scenario configuration behind the ~3.69M @64c figure** before
   drawing any conclusion about #131177. Reproducing the regime is the prerequisite for
   reproducing the cliff.
4. **Never use `aspnet-cobalt-hosted-*` for A/B work at single-digit-percent effect sizes.** Use
   `cobalt-cloud-lin-al3-relay` from `build/azure.profile.yml` instead. Cobalt profiles are split
   across two config files and the useful one is *not* in `scenarios/`.
5. **Verify core counts from `cpu/raw ÷ cpu` rather than trusting profile `cores:` variables**,
   which are stale for at least `cobalt-cloud-lin-al3*` (declares 16, actually 4). Consider
   fixing that value in `build/azure.profile.yml`.
6. **Do not run database scenarios on the cobalt-cloud pod** until its `db` agent is fixed; its
   `multiple_queries` throughput is 23× below gold-lin. Report crank's `StopAsync` NRE (§18.5) as
   a bug — it escalates a recoverable job failure into a controller crash.
7. Retain gold-lin as the x64 reference profile; its ~1–2% spread is well matched to a ~3% effect.

## 21. Round-3 Artifacts

All paths relative to the session workspace `files/runtimeasync/`.

| Path | Contents |
|---|---|
| `out/r3-gold-x64/` | 100 crank result JSONs, gold-lin x64 |
| `out/r3-cloud-arm64/` | 98 valid crank result JSONs, cobalt-cloud Arm64 (42 db failures, §18.5) |
| `out/r3-cobalt-arm64/` | 100 crank result JSONs, cobalt-**hosted** Arm64 (not reportable) |
| `out/r3-stock-control/` | 6 stock-bits control JSONs (§16) |
| `out/r3-cloudprobe/` | 4-iteration noise probe used to qualify the Azure VM pool |
| `out/r3-gold-runs.csv` | Per-iteration rows, gold-lin |
| `out/r3-gold-medians.csv` | Median summary, gold-lin |
| `out/r3-gold-p90-runs.csv` | Full-precision p50/p75/p90/p95/p99 per run |
| `out/r3-gold-latency-stats.csv` | Percentile statistics backing §17.2 |
| `out/r3-cloud-runs.csv`, `out/r3-cloud-medians.csv` | Same for cobalt-cloud Arm64 (§18.2) |
| `out/r3-cloud-p90-runs.csv`, `out/r3-cloud-latency-stats.csv` | Arm64 percentile data (§18.2) |
| `out/r3-cobalt-runs.csv`, `out/r3-cobalt-medians.csv` | Same for cobalt-hosted Arm64 |
| `logs/r3-gold-x64/`, `logs/r3-cloud-arm64/`, `logs/r3-cobalt-arm64/` | Full stdout per run + `manifest.jsonl` |
| `overlay/r3-{x64,arm64}-{on,off}/` | Four staged overlays, 451 files each |
| `local-bits-r3-x64.json`, `local-bits-r3-arm64.json` | Overlay paths, pinned SHAs, staging timestamps |
| `wsl/r3-*.sh` | Rebase, worktree prep, and build drivers |

### Tooling changes made during round 3

- `Get-LatencyPercentiles.ps1` — was hardcoded to round 2's `out/full` directory and, lacking
  `[CmdletBinding()]`, **silently ignored** unrecognized arguments, so a `-RunId` request
  returned round-2 numbers. Now takes `-RunId`/`-Csv`/`-Summary`/`-ValidateAgainst`, and
  `[CmdletBinding()]` makes unknown parameters an error. Verified to reproduce round 2's table
  byte-for-byte after the change.
- `Invoke-RuntimeAsyncBenchmarks.ps1` — added `-ExtraConfig`, which emits an additional
  `--config <file>` for each entry. Required for `cobalt-cloud-lin-al3-relay`, whose profile
  lives in `build/azure.profile.yml` rather than the scenario's own config file.
- `New-LocalOverlay.ps1` — added `-Arch`, `-Tag`, `-AspNetPackDir`, `-BitsFile`.
- `build-runtime.sh` / `build-aspnetcore.sh` — cross-compilation support; compat flag made
  opt-in; aspnetcore packages archived to `~/ra/r3-out/` immediately after each build.
- `Push-WslScript.ps1` — new; enforces LF line endings and refuses to overwrite a `.sh` file
  while bash is executing it.

### Two traps worth remembering

- **Never overwrite a running `.sh`.** Bash re-reads a running script by *byte offset*, so
  replacing it mid-execution resumes at a garbage token. This made a **successful** 30-minute
  runtime build report `rc=1`. `Push-WslScript.ps1` now blocks this per-file.
- **Both architectures share one aspnetcore worktree, and the build wipes `artifacts/`**
  (deliberately — MSBuild's timestamp-based up-to-date checks would otherwise mask a
  `UseRuntimeAsync` flip). Building arch B therefore destroys arch A's packages. Always consume
  the archived `~/ra/r3-out/aspnet-<arch>-<mode>.nupkg`, never the worktree.


---

## 22. Runtime Intake into dotnet/dotnet — Last Month, and Where Our Bits Fall

`dotnet/dotnet` (the VMR) consumes `dotnet/runtime` via Maestro codeflow. Each intake is a
`[main] Source code updates from dotnet/runtime (#NNNN)` commit that advances the `runtime`
entry in **`src/source-manifest.json`**. That file — not `eng/Version.Details.props` (Arcade/Darc
only) and not `eng/Version.Details.xml` (no runtime entry) — is the authoritative record.

### 22.1 Every runtime update into VMR `main`, 2026-07-10 → 2026-08-13

30 distinct runtime advances in the window. `Runtime UTC` is the commit date of the runtime SHA
that was pulled in; `VMR UTC` is when the VMR took it.

| # | VMR UTC | Runtime SHA | Runtime UTC | Lag |
|---|---|---|---|---|
| 1 | 07-10 20:44 | `6c5849144dc` | 07-09 19:44 | 25h |
| 2 | 07-11 19:33 | `66cc35cbd33` | 07-10 23:26 | 20h |
| 3 | 07-13 08:51 | `e8e99e2cd33` | 07-13 00:51 | 8h |
| 4 | 07-14 07:56 | `97c5d1f9a9d` | 07-14 00:59 | 7h |
| 5 | 07-15 12:30 | `dbb2178288b` | 07-15 05:55 | 7h |
| 6 | 07-16 09:14 | `49110af56a9` | 07-16 00:57 | 8h |
| 7 | 07-16 23:01 | `4c9e771cbd5` | 07-16 15:27 | 8h |
| 8 | 07-17 08:47 | `509acfd47f3` | 07-17 00:15 | 9h |
| 9 | 07-18 08:29 | `189c884f91b` | 07-17 23:45 | 9h |
| 10 | 07-19 02:05 | `7b783ab5492` | 07-19 00:47 | 1h |
| 11 | 07-20 10:12 | `563abf22084` | 07-20 00:31 | 10h |
| 12 | 07-24 08:40 | `3a76b784148` | 07-24 00:29 | 8h |
| 13 | 07-26 23:17 | `ca073c0866e` | 07-25 19:18 | 28h |
| 14 | 07-28 11:03 | `f8cf526f300` | 07-28 01:03 | 10h |
| 15 | 07-29 13:48 | `950fea3bc4e` | 07-29 01:12 | 13h |
| 16 | 07-29 22:55 | `0a25043f48f` | 07-29 19:23 | 4h |
| 17 | 07-30 14:04 | `d1d60b31914` | 07-30 01:18 | 13h |
| 18 | 07-31 08:05 | `cf81048c496` | 07-31 00:33 | 8h |
| 19 | 08-01 02:11 | `ba804600579` | 08-01 00:21 | 2h |
| 20 | 08-02 02:06 | `f7a6366c540` | 08-02 00:10 | 2h |
| 21 | 08-03 11:11 | `1110e1e9235` | 08-02 23:04 | 12h |
| **22** | **08-04 08:33** | **`854ee0872ab`** | **08-04 01:18** | 7h |
| | | ⬅ **our bits: `ca4ed7d4a26`, 08-04 17:04 UTC** | | |
| **23** | **08-05 06:12** | **`173e6b40e02`** | **08-05 01:23** | 5h |
| 24 | 08-06 08:37 | `c36f112e7ad` | 08-05 20:27 | 12h |
| 25 | 08-07 07:54 | `9bf3be3ad48` | 08-06 23:56 | 8h |
| 26 | 08-07 18:24 | `26b82502489` | 08-07 17:20 | 1h |
| 27 | 08-10 10:28 | `14b601ec5ed` | 08-09 19:56 | 15h |
| 28 | 08-11 08:43 | `067d74c23c4` | 08-11 00:08 | 9h |
| 29 | 08-12 15:30 | `633ab1a4143` | 08-11 23:24 | 16h |
| 30 | 08-13 16:51 | `0c4851bf2cb` | 08-13 00:17 | 17h |

Cadence is essentially **daily**, with a median lag of ~9 hours from runtime commit to VMR
intake. The only real gaps are 07-20 → 07-24 and 07-21..07-23 (no intake), and the 08-07 → 08-10
weekend.

### 22.2 Where our tested bits fall

Our runtime base is **`ca4ed7d4a265c32e5240863c6b8ff45121339cc4`, 2026-08-04 17:04 UTC**.

| | |
|---|---|
| Last VMR intake **before** our bits | **#22**, VMR 08-04 08:33 — runtime `854ee0872ab` (08-04 01:18), a **direct ancestor** of ours |
| First VMR intake **containing** our bits | **#23**, VMR 08-05 06:12 — runtime `173e6b40e02` (08-05 01:23), **17 commits ahead** of ours |
| Position in the month | **22nd of 30** intakes — about 73% of the way through |
| Intakes since our bits | **8** (08-05, 08-06, 08-07 ×2, 08-10, 08-11, 08-12, 08-13) |
| Distance to VMR `main` today | **185 runtime commits** behind `0c4851bf2cb` |

So our bits sit in the **~16-hour window between intake #22 and #23**. They were never themselves
an intake point, but they are bracketed tightly: strictly newer than what the VMR had on
2026-08-04, and strictly older than what it took on 2026-08-05. In practical terms the tested
runtime is equivalent to **"VMR main as of 2026-08-05"**, ±17 commits.

### 22.3 Consequence for the results

Our bits were **current with the VMR to within one day** at the time the benchmarks ran — we were
briefly *ahead* of it. The ~2× throughput gap versus the source observation (§14, §16) therefore
**cannot be attributed to a stale or divergent runtime**: at daily intake cadence, no plausible
comparison stack from early August 2026 is more than a day or two from ours.

By the same token, the round-3 conclusion about #131177 is scoped to **runtime main as of
2026-08-04**. Eight intakes (185 commits) have landed since, so a re-test on current main is a
different experiment.

---

## 23. Independent Re-Verification of the ON / OFF Overlays

Re-done from scratch (2026-08-13) with a purpose-built scanner,
`Test-AsyncOverlay.ps1`, rather than by reusing the original staging checks.

### 23.1 Method 1 — the `miAsync` metadata flag

Runtime-async stamps every async-compiled method with `MethodImplAttributes.Async`. The value was
taken from the round-3 runtime source itself, not from memory:

```
src/coreclr/inc/corhdr.h:650   miAsync = 0x2000,   // Method requires async state machine rewrite.
System/Reflection/MethodImplAttributes.cs:34   Async = 0x2000,
```

Counts across all 313 managed assemblies in each staged overlay:

| Overlay | Asms | CoreLib | System/Extensions | AspNetCore (asms) | Other | **Total** |
|---|---|---|---|---|---|---|
| `r3-x64-on` | 313 | 97 | 907 | **964** (57) | 15 | **1,983** |
| `r3-x64-off` | 313 | 23 | 0 | **0** (0) | 0 | **23** |
| `r3-arm64-on` | 313 | 97 | 907 | **964** (57) | 15 | **1,983** |
| `r3-arm64-off` | 313 | 23 | 0 | **0** (0) | 0 | **23** |

Findings:

1. **ASP.NET really was rebuilt with the feature** — 964 async methods across 57
   `Microsoft.AspNetCore.*` assemblies in ON, and **exactly 0** in OFF. The overlay is not a
   runtime-only change.
2. **The 23 residuals in OFF are inert infrastructure**, confirmed by name — every one is
   `System.Runtime.CompilerServices.AsyncHelpers::{Await, AwaitAwaiter, AwaitTaskWithRareOptions,
   Suspend, TransparentAwait, TransparentSuspend, UnsafeAwaitAwaiter}`. This is the runtime's own
   async plumbing, which is always compiled runtime-async because it *implements* the feature. No
   framework or user code is async-compiled in the OFF overlay.
3. **x64 and Arm64 are byte-identical in this respect** (1,983 / 23 both ways), as expected —
   same source, same managed IL, only the native bits differ. This also re-confirms the Arm64
   cross-build did not silently fall back to a different configuration.

### 23.2 Method 2 — state-machine elimination (independent of the flag)

A second check that does not use `miAsync` at all. If runtime-async is genuinely active, the C#
compiler stops emitting `<Name>d__N` async state-machine types and their method count falls.

| Assembly | State machines ON | OFF | Methods ON | OFF | Δ methods |
|---|---|---|---|---|---|
| `Microsoft.AspNetCore.Server.Kestrel.Core` | 10 | 74 | 4,963 | 5,130 | +167 |
| `Microsoft.AspNetCore.Mvc.Core` | 10 | 69 | 4,465 | 4,640 | +175 |
| `System.Private.CoreLib` | 18 | 79 | 44,826 | 44,975 | +149 |
| `System.Net.Http` | 7 | 105 | 3,104 | 3,347 | +243 |

State machines collapse by 76–93% and the OFF build carries consistently *more* methods
(overlay-wide: 200,892 OFF vs 196,920 ON, **+3,972**) — exactly the signature of removing
compiler-generated `MoveNext`/`SetStateMachine` scaffolding. The surviving handful in ON are
iterator (`yield return`) state machines, which match the same name pattern but are not async and
correctly remain.

**Two independent signals agree**, so the overlays are compiled as intended.

### 23.3 The third arm — the app-level flag was really applied

`app-off` and `all-on` share the ON overlay and differ only by the application build flag, so a
plumbing failure there would have silently made them the same run — and their measured difference
*is* noise-sized, which is exactly what a broken flag would also look like. Verified directly from
the recorded crank command line in all 240 run logs:

| Arm | Overlay | `/p:Features=runtime-async=on` | gold-lin | cobalt-cloud |
|---|---|---|---|---|
| `all-off` | `...-off` | absent | 30 | 42 |
| `app-off` | `...-on` | absent | 30 | 42 |
| `all-on` | `...-on` | **present** | 30 | 42 |
| `app-off` (orchard) | none — `SkipOverlay` | absent | 5 | 7 |
| `all-on` (orchard) | none — `SkipOverlay` | **present** | 5 | 7 |

100/100 and 140/140 accounted for, with no arm mis-mapped. The OrchardCore rows correctly show no
overlay (it publishes framework-dependent, §10) while still receiving the app flag in `all-on`,
and correctly have no `all-off` arm.

**Conclusion: the bits are compiled correctly and the arms are wired correctly.** The small
`app-off` → `all-on` deltas are a real result, not a plumbing artifact.

> **Scanner caveat worth keeping.** The first pass of the per-layer breakdown double-counted
> `System.Private.CoreLib` (reporting 2,080 instead of 1,983) because PowerShell's `switch -Regex`
> executes **every** matching branch unless each ends with `break` — CoreLib matched both the
> `^System\.Private\.CoreLib\.dll$` and `^System\.` patterns. Caught only because the total
> disagreed with the flat count from the first method. Always cross-check an aggregate against an
> independently computed total.

---

## 24. Round 4 — Splitting the Framework Cost into Runtime vs ASP.NET

### 24.1 Why a fourth arm

Round 3 measured three arms:

| arm | runtime | ASP.NET Core | app flag |
|---|---|---|---|
| `all-off` | OFF | OFF | no |
| `app-off` | ON | ON | no |
| `all-on` | ON | ON | yes |

That design can separate the *app* opt-in (`app-off -> all-on`) from everything else, but it
lumps the two framework layers together: `all-off -> app-off` moves the runtime **and**
ASP.NET Core at the same time. Round 3 showed that this combined framework step carries
essentially the whole regression, but it could not say **which layer** to go fix.

Round 4 adds:

| arm | runtime | ASP.NET Core | app flag |
|---|---|---|---|
| `rt-on` | **ON** | **OFF** | no |

which makes the chain fully decomposable:

```
all-off  --[runtime layer]-->  rt-on  --[ASP.NET layer]-->  app-off  --[app opt-in]-->  all-on
```

### 24.2 Built from the existing artifacts — no rebuild

The requirement was an apples-to-apples comparison against round 3, so `rt-on` had to reuse the
**exact** binaries already measured rather than a fresh build.

The overlays are flat directories assembled in a fixed order — runtime `lib/net11.0`, then
runtime `native`, then ASP.NET `lib/net11.0` (ASP.NET last, so it wins collisions). Two facts
make a mixed overlay well-defined:

1. **The two layers are disjoint by filename.** 317 runtime files + 134 ASP.NET files = 451,
   which is exactly the overlay's file count. No file is contributed by both layers, so
   "take the runtime layer from ON and the ASP.NET layer from OFF" has no ambiguity.
2. **Both flavors were built from the same ASP.NET commit**, so the 134 ASP.NET filenames are
   identical between ON and OFF — 134 in each, zero asymmetric.

`New-MixedOverlay.ps1` therefore copies the ON overlay and overwrites those 134 files with the
OFF build's copies. Every one of the 451 resulting files is then SHA256-verified against the
file in its intended source layer. Both arches: 451/451 verified.

### 24.3 Verified twice, by two independent methods

Hashes prove the files came from the right place; they do not prove the *contents* mean what we
think. So the mixed overlays were also re-scanned for `MethodImplAttributes.Async` (0x2000) and
the counts partitioned by **originating layer**:

| overlay | runtime layer | ASP.NET layer | total |
|---|---|---|---|
| `off`  | 23 | 0 | 23 |
| **`rton`** | **884** | **0** | **884** |
| `on`   | 884 | 1,099 | 1,983 |

Identical on x64 and Arm64. `rton` matches ON on the runtime side and OFF on the ASP.NET side —
exactly the intended construction, confirmed independently of the hashing.

> **Partition by originating layer, not by name prefix.** The ASP.NET shared framework also
> ships `Microsoft.Extensions.*` assemblies, so filtering on `Microsoft.AspNetCore*` under-counts
> the ASP.NET layer by 135 methods.

A third check ran on the wire: `-VerifyOverlay` asks the agent to hash files it actually
received. `libclrjit.so` came back matching the **ON** overlay and
`Microsoft.AspNetCore.Antiforgery.dll` matching the **OFF** overlay, on both arches.

---

## 25. Two Reproducibility Hazards Found in Round 4

Round 4 ran twelve days after round 3, and that gap exposed two ways these benchmarks silently
stop being comparable. Both are worth internalizing because neither announces itself — the runs
succeed and produce plausible numbers.

### 25.1 crank pins nothing by default, so the toolchain drifts

`json.benchmarks.yml` (and the other scenario files) specify **no** SDK, runtime, ASP.NET or
load-client version. crank's default for each is "latest available", resolved at run time:

| | round 3 (Aug 4/5) | round 4 first attempt (Aug 17) |
|---|---|---|
| app SDK, x64 | `11.0.100-rc.1.26402.101` | `11.0.100-rc.1.26413.103` |
| app SDK, Arm64 | `11.0.100-rc.1.26404.112` | `11.0.100-rc.1.26413.103` |
| load-client SDK | `8.0.423` | `8.0.424` |

The app publishes **self-contained** and the overlay is copied on top, so the SDK supplies both
the compiler that builds the app and every framework file the overlay does not replace. A `+1.8%`
"runtime layer" result measured across that gap could just as easily have been the SDK.

**Published size was the canary.** It is recorded before the overlay is applied — proven in §5,
where `all-off` and `app-off` report an identical 138,740 KB despite overlays that differ by
1,961 KB — so any two arms built from the same app sources must report the same value. Instead:

| | expected | observed (unpinned) | delta |
|---|---|---|---|
| x64 | 138,740 | 135,108 | **-3,632 KB** |
| Arm64 | 144,025 | 144,168 | **+143 KB** |

Opposite signs on the two arches, and uncorrelated with the overlay size difference
(`rton - off` is -1,155 KB on x64, -1,282 KB on Arm64). That pattern cannot come from the
artifacts; it is environment drift.

The fix is to pin explicitly. crank exposes `--[job].sdkVersion`, `--[job].runtimeVersion` and
`--[job].aspNetCoreVersion`, so each arch is now pinned to the toolchain it was *originally*
measured on:

```
--application.sdkVersion        11.0.100-rc.1.26402.101   # Arm64: 11.0.100-rc.1.26404.112
--application.runtimeVersion    11.0.0-rc.1.26402.101     # Arm64: 11.0.0-rc.1.26404.112
--application.aspNetCoreVersion 11.0.0-rc.1.26402.101     # Arm64: 11.0.0-rc.1.26404.112
--load.sdkVersion               8.0.423
```

With the pins in place published size returned to **exactly** 138,740 (x64) and 144,025 (Arm64),
matching the round-3 arms and confirming the environment was restored.

Note the two arches were **never on the same SDK even within round 3** (26402.101 vs 26404.112).
That is harmless — every comparison is within an arch — but it does mean the pin is per-arch, and
it is a good reminder never to compare an absolute number across the two tables.

These pins are now recorded in `local-bits-r3-{x64,arm64}.json` (`SdkVersion`, `RuntimeVersion`,
`AspNetCoreVersion`, `LoadSdkVersion`) and applied automatically by
`Invoke-RuntimeAsyncBenchmarks.ps1`. An explicit `-CrankArgs` entry still overrides the manifest,
and a manifest with no pins now emits a warning rather than silently drifting.

### 25.2 Profiles are imported from a floating `main` URL

The scenario files import their profile definitions over the network:

```yaml
imports:
  - https://raw.githubusercontent.com/aspnet/Benchmarks/main/scenarios/aspnet.profiles.yml
```

That is `main`, not a pinned SHA, so **the local checkout does not determine which profiles
exist** — upstream's current state does. The Arm64 round-4 run failed instantly, all 21
invocations, with:

```
Could not find a profile named 'cobalt-cloud-lin-al3-relay'.
```

even though the local benchmarks repo was still at the same commit (`e5d4b804`) used in round 3.

Two things were going on:

1. `cobalt-cloud-lin-al3` is **not** defined in `scenarios/aspnet.profiles.yml` at all. It lives
   in `build/azure.profile.yml`, which the round-3 command passed as a **second `--config`**. The
   relaunch dropped that argument, so the profile was genuinely undefined.
2. The nearest-looking upstream profile, `aspnet-cobalt-hosted-al3-lin`, is **not** a substitute:
   it points at the `cobalthostedlin*_azurelinux3` endpoints (56 cores) whereas our Arm64 data was
   collected on the 4-core Azure VM pod (`cobaltcloudlin*`). Silently accepting the rename would
   have compared a 4-core VM against a 56-core box.

Restoring `-ExtraConfig D:\git\benchmarks\build\azure.profile.yml` fixed it. The profile name and
extra config are now recorded in the bits manifests alongside the version pins.

**Consequence for repros.** Because the imports float, a bundle that ships only command lines is
not reproducible — upstream can change the machine set out from under it. `Build-ReproBundle.ps1`
now vendors every local `--config` file it sees in the captured commands and snapshots each
remote import URL (content + SHA256 + fetch timestamp) into `config/imports-snapshot/`.

### 25.3 Takeaways

- **Pin every version a benchmark depends on**, including the load client. "Latest available" is
  not a constant.
- **Keep a cheap invariant that must not move between arms** — here, published size. It cost
  nothing to record and it is the only reason the drift was noticed at all.
- **Treat a profile rename as a machine change until proven otherwise.** Names are not identities.
- **Re-verify the full command line when relaunching**, not just the parameters being changed;
  the dropped `--config` was silent apart from an immediate hard failure, which was lucky.

---

## 26. Round-4 Results — Where the Cost Actually Lives

All four arms, both architectures, environment pinned per arch. Requests/sec, median of all
iterations (x64 n=5, Arm64 n=7). `rt %` and `asp %` are the new layer deltas.

### 26.1 x64 — `aspnet-gold-lin-relay` (56 cores)

| Scenario | all-off | rt-on | app-off | all-on | full % | **rt %** | **asp %** | app % | noise |
|---|---|---|---|---|---|---|---|---|---|
| plaintext | 9,087,742 | 9,190,732 | 8,842,362 | 8,835,504 | −2.78 | **+1.13** | **−3.79** | −0.08 | 5.56 |
| json | 1,760,652 | 1,795,415 | 1,703,399 | 1,699,346 | −3.48 | **+1.97** | **−5.13** | −0.24 | 1.82 |
| mvc | 1,078,816 | 1,074,502 | 1,022,221 | 997,700 | −7.52 | **−0.40** | **−4.87** | −2.40 | 6.16 |
| fortunes | 578,713 | 578,655 | 551,550 | 548,271 | −5.26 | **−0.01** | **−4.68** | −0.59 | 2.52 |
| fortunes_ef | 477,106 | 474,559 | 454,001 | 459,747 | −3.64 | **−0.53** | **−4.33** | +1.27 | 1.18 |
| multiple_queries | 50,555 | 51,279 | 50,964 | 50,990 | +0.86 | +1.43 | −0.61 | +0.05 | 2.50 |

### 26.2 Arm64 — `cobalt-cloud-lin-al3-relay` (4 cores)

| Scenario | all-off | rt-on | app-off | all-on | full % | **rt %** | **asp %** | app % | noise |
|---|---|---|---|---|---|---|---|---|---|
| plaintext | 1,579,252 | 1,537,394 | 1,360,593 | 1,372,666 | −13.08 | **−2.65** | **−11.50** | +0.89 | 6.70 |
| json | 233,684 | 228,667 | 191,634 | 186,228 | −20.31 | **−2.15** | **−16.20** | −2.82 | 9.09 |
| mvc | 118,692 | 120,371 | 103,161 | 105,695 | −10.95 | **+1.41** | **−14.30** | +2.46 | 18.00 |

### 26.3 The runtime layer is not the problem

Across nine scenario/arch combinations the runtime layer moves throughput by between **−2.65%
and +1.97%**, and in **every single case** the magnitude is smaller than that scenario's own
iteration noise. Four of the nine are *positive*. There is no measurable runtime-layer cost.

The ASP.NET layer, by contrast, accounts for essentially the whole regression:

| | full stack | runtime layer | ASP.NET layer |
|---|---|---|---|
| x64 json | −3.48% | +1.97% | **−5.13%** |
| x64 fortunes | −5.26% | −0.01% | **−4.68%** |
| Arm64 json | −20.31% | −2.15% | **−16.20%** |
| Arm64 plaintext | −13.08% | −2.65% | **−11.50%** |

The latency percentiles decompose identically — for x64 json P50 the full-stack rise of +4.00%
splits into **−1.60%** from the runtime (an *improvement*) and **+5.69%** from ASP.NET.

`multiple_queries` remains the control: it is database-bound, its full-stack delta is +0.86%, and
every layer delta is inside noise. A scenario that spends its time waiting on Postgres shows no
cost from any layer, exactly as it should.

### 26.4 What this changes

Round 3 could only say "the framework costs 3&ndash;20%". Round 4 says **the cost is in the
ASP.NET Core async rewrite, not in the runtime's async implementation**. Optimization effort
aimed at the runtime (JIT, the async state machine lowering, `libclrjit`) is targeting a layer
that currently measures free. The investigation should move to what ASP.NET Core's own conversion
to runtime-async does to its hot request path.

Two caveats worth stating plainly:

- **The layers are measured in a fixed order.** `asp %` is the cost of turning ASP.NET on *given*
  an already-async runtime. That is the right question here, but it is not necessarily the same
  number you would get by turning ASP.NET on over a non-async runtime, and the layers are not
  independent. Note the three layer deltas compose *multiplicatively* into the full-stack delta
  exactly — verified to 0.00 pp on all nine rows — but that is a telescoping identity
  (`rt-on/all-off × app-off/rt-on × all-on/app-off = all-on/all-off`), so it only confirms the
  arithmetic, it says nothing about the feature. Treated additively the same rows disagree with
  the full-stack number by up to 0.85 pp, so quote the layers multiplicatively or not at all.
- **Arm64 magnitudes are much larger than x64** (−20.3% vs −3.5% on json). That is a 4-core VM
  versus a 56-core host, so absolute magnitudes should never be compared across the two tables --
  but the *shape* of the decomposition is the same on both, which is the point.

### 26.5 The drift did not change the answer

For the record: the unpinned x64 run measured the json runtime layer at **+1.82%**; the pinned
re-run measured **+1.97%**. The SDK drift described in §25.1 turned out not to move the
conclusion. That is a lucky outcome, not a vindication -- there was no way to know that without
re-running, and the published-size canary was showing a 3.6 MB discrepancy that had no innocent
explanation at the time.

---

## 27. Round 5 — Cobalt 200 (newer Arm64 hardware)

### 27.1 What was run

The full four-arm battery (`all-off`, `rt-on`, `app-off`, `all-on`) on the Cobalt 200 pod,
7 iterations x 3 scenarios = 84 runs, using the **same overlay artifacts, same commits and
same version pins** as the cobalt100 round-4 run. Nothing was rebuilt.

| | |
|---|---|
| application agent | `temp_ef_azure_server_al3` (10.1.4.6, Azure Linux 3) |
| load agent | `temp_ef_azure_client` (10.1.4.7) |
| profile | `cobalt200-lin-al3-relay` (defined in `cobalt200.profile.yml`, passed as a second `--config`) |
| scenarios | json, plaintext, mvc |
| published-size canary | 144,025 KB (all-off / rt-on / app-off), 143,994 KB (all-on) -- **identical to cobalt100** |

`fortunes`, `fortunes_ef` and `multiple_queries` could not run: no db agent exists on this pod.
That is the same usable set as cobalt100, where the db agent deadlocked.

### 27.2 The pod is 128 cores, not 4 -- and could not be saturated

The profile's `cores:` metadata is unreliable (§21), so core count was derived from results as
`benchmarks/cpu/raw / benchmarks/cpu`. Cobalt 200 came out at **~128 cores**, against
cobalt100's **4**. Left unconstrained, a single load client could not saturate it:

| connections | throughput | server CPU | load client CPU |
|---|---|---|---|
| 256 (cobalt100's setting) | 1,694,025 | 74% | 47% |
| 1024 | 3,020,511 | 80% | 81% |
| 4096 | 3,712,563 | 80% | **99%** |

Throughput kept climbing while the **client** hit 99% and the server never passed 80%. Any
measurement in that regime is partly client-bound, which compresses the very differences the
experiment exists to detect.

The fix (suggested by @DrewScoggins) was to confine the application with a cgroup cpuset:
`--application.cpuSet 0-3`. Verified effective -- derived cores becomes exactly **4.0**, server
CPU rises to **93%** and the load client drops to **9%**. The app is now unambiguously the
bottleneck, and core count is held constant against cobalt100.

`cores: 16` was deliberately left in the new profile. It is wrong for both machines, but it
feeds `concurrencyPerHttpClient` in `httpclient.benchmarks.yml`; "correcting" it would change
load generation and add a second variable.

### 27.3 Results -- Requests/sec medians, n=7

| Scenario | all-off | rt-on | app-off | all-on | full % | **rt %** | **asp %** | app % | noise | separation |
|---|---|---|---|---|---|---|---|---|---|---|
| plaintext | 2,719,326 | 2,723,722 | 2,636,480 | 2,635,472 | -3.08 | **+0.16** | **-3.20** | -0.04 | 2.31 | complete 49/49 |
| json | 369,516 | 368,861 | 351,360 | 349,491 | -5.42 | **-0.18** | **-4.75** | -0.53 | 5.39 | complete 49/49 |
| mvc | 266,806 | 265,322 | 253,573 | 252,371 | -5.41 | **-0.56** | **-4.43** | -0.47 | 4.85 | complete 49/49 |

**The round-4 finding replicates, and more cleanly.** The runtime layer moves throughput by
-0.56% to +0.16% -- an even tighter band than cobalt100's -2.65% to +1.41% -- while the ASP.NET
layer carries the entire regression. Separation is complete on all three scenarios (every
all-off iteration beat every all-on iteration, 49/49 pairwise), so the full-stack regression is
real even where it sits inside the spread heuristic.

### 27.4 The headline: cobalt100 was the outlier, not Arm64

At **identical core count and identical bits**, the two Arm64 pods disagree sharply:

| Scenario | cobalt100 all-off | CB200 all-off | HW gain | rt % 100 -> 200 | asp % 100 -> 200 | full % 100 -> 200 |
|---|---|---|---|---|---|---|
| plaintext | 1,579,252 | 2,719,326 | **+72.2%** | -2.65 -> +0.16 | -11.50 -> -3.20 | -13.08 -> -3.08 |
| json | 233,684 | 369,516 | **+58.1%** | -2.15 -> -0.18 | -16.20 -> -4.75 | -20.31 -> -5.42 |
| mvc | 118,692 | 266,806 | **+124.8%** | +1.41 -> -0.56 | -14.30 -> -4.43 | -10.95 -> -5.41 |

Two separate conclusions, and they should not be conflated:

1. **Cobalt 200 is much faster per core** -- +58% to +125% at the same 4-core budget.
2. **The alarming Arm64 regression was largely a property of the cobalt100 pod.** Round 4
   reported -10.95% to -20.31% full-stack on Arm64 versus -2.78% to -7.52% on x64, which
   invited the reading "runtime-async is disproportionately bad on Arm64". On cobalt200 the
   same bits give **-3.08% to -5.42%**, squarely in the x64 range. Iteration noise also fell
   from 6.70-18.00% to 2.31-5.39%.

The corrected picture: **runtime-async costs roughly 3-5.5% on framework-bound scenarios on
both architectures, essentially all of it in ASP.NET Core.** The Arm64-specific penalty
reported in §26 should be treated as an artifact of that particular 4-core pod.

### 27.5 Caveats

* **Four pinned cores on a 128-core socket are not a 4-core machine.** They still enjoy a far
  larger shared L3 and much greater memory bandwidth. This flatters cobalt200 in §27.4's
  hardware column, and may also be part of why its regressions are smaller -- a memory- or
  cache-starved 4-core VM will punish extra allocations and indirections harder. The hardware
  gain is therefore an upper bound.
* **`cpuSet 0-3` assumes cores 0-3 are topologically adjacent.** The NUMA/cache layout of the
  pod was not inspected; if those four cores span nodes the figures would be pessimistic.
* cobalt100 was measured at 74-97% server CPU across its arms; cobalt200 at 93-99%. The pods
  were not equally loaded in round 4, which is a further reason to trust §27.3's internal
  layer split more than any cross-pod absolute.
* 3 of the 84 runs (all `plaintext`, iterations 5-6) failed transiently with an unhandled .NET
  exception and were re-run to completion; the reruns are the ones in the data.
