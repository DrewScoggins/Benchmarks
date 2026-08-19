# Crank PerfLab exporter

`Crank.PerfLabExporter` is the external adapter between Crank and the canonical
PerfLab result contract. Crank continues to emit raw `--json` output and write
its existing SQL rows; this tool owns conversion and optional Azure publication.

The default Trend counter policy is
[`build/crank-perflab-counter-policy.json`](../../build/crank-perflab-counter-policy.json).
It maps requests/sec, mean latency, P99 latency, startup time, and published
size. Published size is normalized from Crank KB to bytes. Every other finite
numeric result entry whose value is itself a scalar is retained with its fully
qualified source path, `value` unit, unknown direction, and
non-top/non-default roles. Object and array payloads (including raw
distributions and histograms) are skipped with diagnostics and are never
flattened into counters or samples. Top-level non-finite numeric
representations fail conversion.

The policy is copied beside build, publish, and .NET tool outputs. Relative
input paths are resolved from the working directory first and then from the
exporter installation directory, so workers can use
`--counter-policy crank-perflab-counter-policy.json`.

## Convert locally

```powershell
dotnet run --project src\Crank.PerfLabExporter -- convert `
  --crank-json artifacts\crank.json `
  --counter-policy build\crank-perflab-counter-policy.json `
  --identity artifacts\perflab-identity.json `
  --output-directory artifacts
```

The identity file supplies:

- runtime repo, branch, hash, build name, version/artifact ID, and optional
  commit timestamp;
- stable lane OS, queue, and run configurations;
- scenario name, family, categories, and scenario data;
- dependency metadata, pinned Benchmarks hash, and Crank version;
- Azure DevOps project/pipeline/build metadata;
- Crank SQL session/table/record identity;
- an optional real Helix correlation GUID.

Runtime is selected from Crank dependencies by normalized
`Microsoft.NETCore.App`/`dotnet/runtime` identity, never array position. The
supplied runtime hash must match. ASP.NET Core and other dependencies are
recorded in build additional data. If no runtime commit timestamp is supplied,
the exporter resolves it through the GitHub commits API; an optional token is
read from `GITHUB_TOKEN` or the environment variable selected with
`--github-token-environment-variable`.

## Live worker identity

Trend workers do not need to generate or base64-encode an identity file. Use
`--identity-source crank` to construct `ExportIdentity` from the raw runtime
and ASP.NET Core dependencies, the application job environment, and explicit
`perflab.*` Crank properties. Dedicated CLI options can override the same
fields for diagnostics and backfills.

Trend supplies these property groups:

- `perflab.build.*`: runtime repository and branch; version, commit, and the
  default `runtime-<version>` build name come from the normalized
  `Microsoft.NETCore.App` dependency;
- `perflab.lane.*` and `perflab.configuration.*`: stable lane/queue,
  OS/architecture/locale, and Framework/Runtime/Cores/Topology;
- `perflab.scenario.*`: individual test name, stable family, and categories;
- `perflab.azureDevOps.*`, `perflab.sql.*`, `perflab.perfRepoHash`, and
  `perflab.policy.path`.

Live mode rejects missing required fields, conflicting dependency identity,
and unexpanded `$(...)`/`${{ ... }}` pipeline expressions. It also requires a
Crank version from `perflab.crankVersion`, a root `crankVersion` JSON field, an
explicit `--crank-version`, or the environment variable named by
`--crank-version-environment-variable`.

Current Crank `--json` output contains one aggregate per result and averages
multiple Crank iterations before writing the file. The exporter therefore emits
one independent result value per scalar and records that sample model in test
additional data. Timestamped `measurements` are never treated as independent
PerfLab samples.

## Backfill legacy Trend history

The `backfill` command reads legacy `TrendBenchmarks` rows without changing
Crank or SQL. `Document` is deserialized as the historical Crank `JobResults`
payload, wrapped with return code `0`, and passed through the same converter,
validator, naming, and publisher used by live Trend.

Backfill is fail-safe: it defaults to convert-only dry-run even when storage
options are present. Live publication is enabled only when both `--publish`
and the exact guard
`--confirm-live-publication PUBLISH_TREND_BACKFILL` are supplied. Supplying
storage account, container, queue, or credentials alone never sends data.

The version-controlled
[`build/trend-perflab-legacy-mapping.json`](../../build/trend-perflab-legacy-mapping.json)
contains ordered, explicit lane and scenario-family rules. Lane matching uses
historical profile or description metadata; scenario matching uses the
canonical SQL/JobResults `scenario` metadata or explicit historical
description aliases. Zero or multiple matches are reported as unresolved.
There is no default lane or family. Review and version this file before
backfilling a historical period whose lanes, framework, or scenarios differ.

Dry-run runbook:

```powershell
$env:TREND_SQL_CONNECTION_STRING = '<Azure SQL connection string>'

dotnet run --project src\Crank.PerfLabExporter -- backfill `
  --sql-connection-string-environment-variable TREND_SQL_CONNECTION_STRING `
  --sql-table dbo.TrendBenchmarks `
  --sql-authentication certificate `
  --sql-tenant-id-environment-variable SQL_SERVER_TENANTID `
  --sql-client-id-environment-variable SQL_SERVER_CLIENTID `
  --sql-certificate-path-environment-variable SQL_SERVER_CERT_PATH `
  --dry-run `
  --benchmarks-commit <Benchmarks commit fallback> `
  --crank-version <Crank version fallback> `
  --azdo-project <project fallback> `
  --azdo-pipeline <pipeline fallback> `
  --azdo-build-url-template 'https://dev.azure.com/<org>/<project>/_build/results?buildId={buildId}' `
  --checkpoint artifacts\trend-backfill-dry-run.checkpoint.json `
  --summary artifacts\trend-backfill-dry-run.summary.json `
  --output-directory artifacts\trend-backfill
```

`--convert-only` is an alias for `--dry-run`. The default inclusive window is
the latest 90 days. Its resolved start/end are saved in the checkpoint so a
later restart uses the same window. Use `--start-utc` and `--end-utc` for
explicit inclusive UTC bounds, `--batch-size` for SQL paging, and
`--maximum-rows` for a bounded validation pass.

After reviewing every unresolved/failed row in the summary, use a different
live checkpoint:

```powershell
dotnet run --project src\Crank.PerfLabExporter -- backfill `
  --sql-connection-string-environment-variable TREND_SQL_CONNECTION_STRING `
  --sql-table dbo.TrendBenchmarks `
  --sql-authentication managed-identity `
  --sql-managed-identity-client-id-environment-variable SQL_MANAGED_IDENTITY_CLIENT_ID `
  --benchmarks-commit <Benchmarks commit fallback> `
  --crank-version <Crank version fallback> `
  --azdo-project <project fallback> `
  --azdo-pipeline <pipeline fallback> `
  --azdo-build-url-template 'https://dev.azure.com/<org>/<project>/_build/results?buildId={buildId}' `
  --publish `
  --confirm-live-publication PUBLISH_TREND_BACKFILL `
  --storage-account pvscmdupload `
  --container results `
  --queue resultsqueue `
  --checkpoint artifacts\trend-backfill-live.checkpoint.json `
  --summary artifacts\trend-backfill-live.summary.json `
  --output-directory artifacts\trend-backfill
```

The live command above is an operator runbook only. It was intentionally not
executed while implementing or validating this feature. Tests use fake SQL
repositories, publishers, and commit-time resolvers and do not contact SQL,
GitHub, Azure Storage, or queues.

SQL authentication modes are `connection-string`, `default`,
`managed-identity`, `certificate`, and `token`. Token mode reads only the
environment variable named by `--sql-access-token-environment-variable`.
Connection strings, access tokens, certificate passwords, and document JSON
are never written to logs or summaries.

Rows are paged by `DateTimeUtc, Id`; excluded rows are counted and skipped.
Each SQL row has a stable blob name under `crank/sql/`, so a retry overwrites
the same blob before queue submission. The atomic checkpoint stores the
mapping/configuration fingerprint and last completed timestamp/ID. A changed
mapping, window, identity fallback, dry-run/live mode, or publication target
is rejected as incompatible. A failed or unresolved row blocks checkpoint
progress past that row; later work is safe to repeat.

The summary JSON reports `scanned`, `excluded`, `converted`, `uploaded`,
`dryRunValidated`, `unresolved`, and `failed`, plus only row IDs and sanitized
reasons. Historical SQL identity, scenario, description, and insertion time
are retained under `historical.known.*`; operator fallbacks are separately
marked under `historical.fallback.*`. PerfLab build time always comes from the
resolved runtime commit, never SQL insertion or session time.

## Upload

```powershell
dotnet run --project src\Crank.PerfLabExporter -- upload `
  --crank-json artifacts\crank.json `
  --counter-policy build\crank-perflab-counter-policy.json `
  --identity artifacts\perflab-identity.json `
  --output-directory artifacts `
  --storage-account pvscmdupload `
  --container results `
  --queue resultsqueue
```

Authentication uses `DefaultAzureCredential` and supports a user-assigned
managed identity with `--managed-identity-client-id` or its environment
variable form. Certificate workers can instead pass tenant/client IDs (or
environment variable names for them) plus `--certificate-path` (or an
environment variable containing that path), or name an environment variable
containing a base64 PFX with
`--certificate-base64-environment-variable`. Certificate passwords are read
only from the named environment variable.

File and blob names are deterministic from runtime, family, lane,
configuration, scenario, and SQL session identity. Blob upload always
overwrites that name, so conversion/upload retries are idempotent. Blob and
queue operations retry independently. The queue body exactly follows
`performance/scripts/upload.py`:

```json
{"container_name": "results", "blob_name": "crank/.../report.perflab.json"}
```

## Worker post-process contract

The Crank worker is configured separately with the exporter executable. After
a successful Crank run and before cleanup, Trend sends this generic payload:

```json
{
  "postProcess": {
    "name": "Crank PerfLab export",
    "args": [
      "upload",
      "--crank-json", "crank-results.json",
      "--counter-policy", "crank-perflab-counter-policy.json",
      "--identity-source", "crank",
      "--identity-property-prefix", "perflab.",
      "--crank-version-environment-variable", "CRANK_VERSION",
      "--storage-account", "pvscmdupload",
      "--container", "results",
      "--queue", "resultsqueue",
      "--tenant-id-environment-variable", "PERFLAB_UPLOAD_TENANT_ID",
      "--client-id-environment-variable", "PERFLAB_UPLOAD_CLIENT_ID",
      "--certificate-base64-environment-variable", "PERFLAB_UPLOAD_CERTIFICATE_BASE64",
      "--certificate-password-environment-variable", "PERFLAB_UPLOAD_CERTIFICATE_PASSWORD"
    ]
  }
}
```

Only credential environment-variable names cross Service Bus. Secret values
remain in the worker environment and are never included in the command.
Worker deployment must configure `CRANK_VERSION`,
`PERFLAB_UPLOAD_TENANT_ID`, `PERFLAB_UPLOAD_CLIENT_ID`,
`PERFLAB_UPLOAD_CERTIFICATE_BASE64`, and
`PERFLAB_UPLOAD_CERTIFICATE_PASSWORD` before enabling this post-process
payload. Deployment configuration outside this repository is intentionally
not implemented here.

## Deploy beside a worker

Framework-dependent .NET tool:

```powershell
dotnet pack src\Crank.PerfLabExporter -c Release -o artifacts\packages
dotnet tool install Crank.PerfLabExporter `
  --tool-path artifacts\crank-perflab-exporter `
  --add-source artifacts\packages `
  --version <version>
```

Framework-dependent app:

```powershell
dotnet publish src\Crank.PerfLabExporter -c Release `
  --self-contained false -o artifacts\crank-perflab-exporter
```

Self-contained single-file app:

```powershell
dotnet publish src\Crank.PerfLabExporter -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -o artifacts\crank-perflab-exporter-win-x64
```

The publish RID can be changed for the worker OS. Configure the resulting
`crank-perflab-export` tool command or `Crank.PerfLabExporter` executable as
the worker's post-processor; the payload intentionally contains no executable
path.

Run `Crank.PerfLabExporter --help` for all options. Conversion, validation,
authentication, upload, and queue failures return a non-zero exit code.
