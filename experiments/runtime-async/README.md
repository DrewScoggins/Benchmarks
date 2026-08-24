# Runtime-async performance experiment

Measurements of the .NET **runtime-async** feature (`dotnet/runtime` PR #131177) on ASP.NET
Core benchmark workloads, run with crank against the perf lab.

**Start with [`FINDINGS.md`](FINDINGS.md) — read §27 first**, then §24–§26. The document is
chronological, so the earliest headline is the least current.

## Result in one paragraph

Runtime-async is enabled at three independent layers (runtime, ASP.NET Core, and the app's
own csproj flag). Building the shared framework twice and swapping it underneath an unmodified
app separates them. The cost is **~3–5.5% throughput on framework-bound scenarios, essentially
all of it in ASP.NET Core**. The runtime layer measures free — across twelve scenario/hardware
combinations it moves throughput between −2.65% and +1.97%, always within that scenario's own
iteration noise. An app opting in on an already-async framework costs nothing measurable.

## The four arms

| Arm | Runtime | ASP.NET Core | App opts in | Isolates |
|---|---|---|---|---|
| `all-off` | OFF | OFF | no | baseline |
| `rt-on` | **ON** | OFF | no | the runtime layer alone |
| `app-off` | ON | **ON** | no | + the ASP.NET layer |
| `all-on` | ON | ON | **yes** | + the application layer |

Compose layer deltas **multiplicatively**: `(1+rt)(1+asp)(1+app)−1` reproduces the full-stack
number exactly, whereas adding them disagrees by up to 0.85 pp.

## Hardware

| Run id | Profile | Notes |
|---|---|---|
| `r3-gold-x64` | `aspnet-gold-lin-relay` | 56-core Linux x64, n=5 |
| `r3-cloud-arm64` | `cobalt-cloud-lin-al3-relay` | 4-core Azure Linux 3 Arm64 VM (Cobalt 100), n=7 |
| `cb200-4c` | `cobalt200-lin-al3-relay` | Cobalt 200 Arm64; 128-core host with the app confined to 4 cores via `--application.cpuSet 0-3`, n=7 |

The Cobalt 200 run exists to re-test the alarming Arm64 numbers from Cobalt 100. At matched core
count and identical bits, the regression drops from −10.95…−20.31% to −3.08…−5.42%, i.e. into
the x64 range. **The large Arm64 penalty was a property of the Cobalt 100 pod, not of Arm64.**

## Layout

```
FINDINGS.md          full analysis, methodology and caveats
analysis/            median + latency percentile CSVs, and Teams-formatted tables
results/<run-id>/    raw crank result JSON, one file per scenario/arm/iteration
manifests/           append-only JSONL record of every crank invocation actually issued
scripts/             the harness (matrix runner, overlay builder/verifier, aggregators)
config/              cobalt200.profile.yml -- the Cobalt 200 profile, passed as a second --config
bits/                overlay provenance: commits, version pins, per-file hashes
```

Not included here, deliberately: the shared-framework overlays (~876 MB of binaries) and the
self-contained repro zip (~142 MB). See "Reproducing" below.

## Reproducing

`manifests/*.jsonl` holds the verbatim command line for every run, so any single measurement can
be replayed exactly. Two hazards will silently invalidate a repro (both detailed in §25):

1. **crank pins nothing by default** — every unpinned version resolves to *latest available* at
   run time. The pins actually used are recorded in `bits/local-bits-*.json` and in the manifest
   command lines. Published size is the cheapest drift canary: 138,740 KB (x64) / 144,025 KB
   (Arm64), and 31 KB less on the `all-on` arm because the app itself is rebuilt.
2. **Profiles are imported from a floating `main` URL**, so the set of available profiles is
   whatever upstream serves at run time, and it is a live network dependency. `cobalt-cloud-lin-al3`
   and `cobalt200-lin-al3` are *not* in that file — they come from `build/azure.profile.yml` and
   `config/cobalt200.profile.yml`, each passed as an additional `--config`. Treat a profile
   rename as a machine change until proven otherwise.

Note also that `cores:` in a profile is frequently stale — derive the real count from results as
`benchmarks/cpu/raw ÷ benchmarks/cpu`.
