# Crank PerfLab exporter

`Crank.PerfLabExporter` is the external adapter between Crank and the canonical
PerfLab result contract. Crank continues to emit raw `--json` output and write
its existing SQL rows; this tool owns conversion and optional Azure publication.

The default Trend counter policy is
[`build/crank-perflab-counter-policy.json`](../../build/crank-perflab-counter-policy.json).
It maps requests/sec, mean latency, P99 latency, startup time, and published
size. Published size is normalized from Crank KB to bytes. Every other finite
numeric scalar below `jobs.<job>.results` is retained with its fully qualified
source path, `value` unit, unknown direction, and non-top/non-default roles.
Non-finite scalars fail conversion.

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

Current Crank `--json` output contains one aggregate per result and averages
multiple Crank iterations before writing the file. The exporter therefore emits
one independent result value per scalar and records that sample model in test
additional data. Timestamped `measurements` are never treated as independent
PerfLab samples.

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
managed identity with `--managed-identity-client-id`. Certificate workers can
instead pass tenant/client IDs plus `--certificate-path`, or name an environment
variable containing a base64 PFX with
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

Run `Crank.PerfLabExporter --help` for all options. Conversion, validation,
authentication, upload, and queue failures return a non-zero exit code.
