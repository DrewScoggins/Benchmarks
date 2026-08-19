"""Generate self-contained Trend configs with Benchmarks imports flattened."""

import copy
import json
import os
import re
import shutil
from typing import Any, Dict, List, Optional, Set, Tuple


BENCHMARKS_COMMIT_VARIABLE = "benchmarksCommit"
BENCHMARKS_COMMIT_TEMPLATE = "{{benchmarksCommit}}"
TREND_CONFIG_SOURCES = (
    "build/azure.profile.yml",
    "build/ci.profile.yml",
    "scenarios/steadystate.profile.yml",
    "scenarios/platform.benchmarks.yml",
    "scenarios/plaintext.benchmarks.yml",
    "scenarios/json.benchmarks.yml",
    "scenarios/antiforgery.benchmarks.yml",
    "scenarios/tls.benchmarks.yml",
    "scenarios/rejection.benchmarks.yml",
    "scenarios/database.benchmarks.yml",
    "src/BenchmarksApps/TechEmpower/Minimal/minimal.benchmarks.yml",
    "src/BenchmarksApps/TechEmpower/BlazorSSR/blazorssr.benchmarks.yml",
    "src/BenchmarksApps/TechEmpower/RazorPages/razorpages.benchmarks.yml",
)

_BENCHMARKS_IMPORT_RE = re.compile(
    r"^https://raw\.githubusercontent\.com/aspnet/Benchmarks/"
    r"(?:refs/heads/main|[^/]+)/(?P<path>.+)$",
    re.IGNORECASE,
)
_BENCHMARKS_RAW_RE = re.compile(
    r"https://raw\.githubusercontent\.com/aspnet/Benchmarks/"
    r"(?:refs/heads/)?main/",
    re.IGNORECASE,
)


class TrendConfigBundleError(ValueError):
    """Raised when a Trend config graph cannot be flattened safely."""


def bundle_repo_path(source_path: str) -> str:
    """Return the repository-relative generated path for a source config."""
    normalized = source_path.replace("\\", "/").lstrip("/")
    return f"build/trend-configs/{normalized}"


def build_trend_config_document(
    repo_root: str,
    source_path: str,
) -> Tuple[Dict[str, Any], Set[str]]:
    """Build one generated config and return its transitive source files."""
    external_imports, document, dependencies = _load_document(
        os.path.abspath(repo_root),
        _normalize_source_path(source_path),
        [],
        {},
    )
    _pin_benchmarks_references(document)

    variables = document.setdefault("variables", {})
    if not isinstance(variables, dict):
        raise TrendConfigBundleError(
            f"Config {source_path!r} has a non-object variables section"
        )
    variables[BENCHMARKS_COMMIT_VARIABLE] = "main"

    if external_imports:
        document = {"imports": external_imports, **document}
    return document, dependencies


def generate_trend_config_bundle(
    repo_root: str,
    output_dir: str,
) -> List[str]:
    """Regenerate every committed Trend config bundle file."""
    repo_root = os.path.abspath(repo_root)
    output_dir = os.path.abspath(output_dir)
    if os.path.isdir(output_dir):
        shutil.rmtree(output_dir)
    os.makedirs(output_dir, exist_ok=True)

    outputs = []
    for source_path in TREND_CONFIG_SOURCES:
        document, _ = build_trend_config_document(repo_root, source_path)
        output_path = os.path.join(
            output_dir,
            source_path.replace("/", os.sep),
        )
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        with open(output_path, "w", newline="\n", encoding="utf-8") as file:
            json.dump(document, file, indent=2, ensure_ascii=False)
            file.write("\n")
        outputs.append(output_path)
    return outputs


def _load_document(
    repo_root: str,
    source_path: str,
    stack: List[str],
    cache: Dict[str, Tuple[List[str], Dict[str, Any], Set[str]]],
) -> Tuple[List[str], Dict[str, Any], Set[str]]:
    if source_path in stack:
        cycle = " -> ".join([*stack, source_path])
        raise TrendConfigBundleError(
            f"Benchmarks config import cycle detected: {cycle}"
        )
    if source_path in cache:
        imports, document, dependencies = cache[source_path]
        return (
            copy.deepcopy(imports),
            copy.deepcopy(document),
            set(dependencies),
        )

    full_path = os.path.abspath(
        os.path.join(repo_root, source_path.replace("/", os.sep))
    )
    if os.path.commonpath([repo_root, full_path]) != repo_root:
        raise TrendConfigBundleError(
            f"Config path escapes the repository: {source_path!r}"
        )
    if not os.path.isfile(full_path):
        raise TrendConfigBundleError(
            f"Benchmarks config import does not exist: {source_path!r}"
        )

    try:
        import yaml
    except ImportError as error:
        raise TrendConfigBundleError(
            "Trend config generation requires PyYAML. Install "
            "scripts/pod-scheduler/requirements.txt."
        ) from error

    with open(full_path, encoding="utf-8-sig") as file:
        document = yaml.safe_load(file)
    if not isinstance(document, dict):
        raise TrendConfigBundleError(
            f"Config {source_path!r} must contain a YAML object"
        )

    imports = document.get("imports", [])
    if imports is None:
        imports = []
    if not isinstance(imports, list) or any(
        not isinstance(item, str) for item in imports
    ):
        raise TrendConfigBundleError(
            f"Config {source_path!r} imports must be a string array"
        )

    external_imports: List[str] = []
    merged: Dict[str, Any] = {}
    dependencies = {source_path}
    next_stack = [*stack, source_path]
    for imported in imports:
        benchmarks_path = _benchmarks_import_path(imported)
        if benchmarks_path is None:
            if merged:
                raise TrendConfigBundleError(
                    f"Config {source_path!r} has external import {imported!r} "
                    "after a Benchmarks-owned import. Crank loads imports "
                    "before applying variables, so this order cannot be "
                    "flattened without changing merge precedence."
                )
            external_imports.append(imported)
            continue

        (
            nested_external,
            nested_document,
            nested_dependencies,
        ) = _load_document(
            repo_root,
            benchmarks_path,
            next_stack,
            cache,
        )
        if nested_external and merged:
            raise TrendConfigBundleError(
                f"Config {source_path!r} imports {benchmarks_path!r} after "
                "Benchmarks content whose external import order cannot be "
                "preserved."
            )
        external_imports.extend(nested_external)
        merged = _merge_documents(merged, nested_document)
        dependencies.update(nested_dependencies)

    local_document = copy.deepcopy(document)
    local_document.pop("imports", None)
    merged = _merge_documents(merged, local_document)
    cached = (
        copy.deepcopy(external_imports),
        copy.deepcopy(merged),
        set(dependencies),
    )
    cache[source_path] = cached
    return external_imports, merged, dependencies


def _merge_documents(
    current: Dict[str, Any],
    overlay: Dict[str, Any],
) -> Dict[str, Any]:
    result = copy.deepcopy(current)
    for key, value in overlay.items():
        if isinstance(result.get(key), dict) and isinstance(value, dict):
            result[key] = _merge_documents(result[key], value)
        else:
            result[key] = copy.deepcopy(value)
    return result


def _pin_benchmarks_references(node: Any) -> None:
    if isinstance(node, dict):
        repository_key = next(
            (
                key for key in node
                if isinstance(key, str) and key.lower() == "repository"
            ),
            None,
        )
        repository = (
            node.get(repository_key)
            if repository_key is not None
            else None
        )
        if (
            isinstance(repository, str)
            and _is_benchmarks_repository(repository)
        ):
            branch_key = next(
                (
                    key for key in node
                    if isinstance(key, str) and
                    key.lower() == "branchorcommit"
                ),
                "branchOrCommit",
            )
            node[branch_key] = BENCHMARKS_COMMIT_TEMPLATE
        for key, value in list(node.items()):
            if isinstance(value, str):
                node[key] = _BENCHMARKS_RAW_RE.sub(
                    "https://raw.githubusercontent.com/aspnet/Benchmarks/"
                    f"{BENCHMARKS_COMMIT_TEMPLATE}/",
                    value,
                )
            else:
                _pin_benchmarks_references(value)
    elif isinstance(node, list):
        for value in node:
            _pin_benchmarks_references(value)


def _benchmarks_import_path(value: str) -> Optional[str]:
    match = _BENCHMARKS_IMPORT_RE.match(value)
    if match is None:
        return None
    return _normalize_source_path(match.group("path"))


def _normalize_source_path(value: str) -> str:
    normalized = value.replace("\\", "/").lstrip("/")
    if not normalized or normalized.startswith("../"):
        raise TrendConfigBundleError(
            f"Invalid Benchmarks config path: {value!r}"
        )
    return normalized


def _is_benchmarks_repository(value: str) -> bool:
    normalized = value.strip().rstrip("/").lower()
    if normalized.endswith(".git"):
        normalized = normalized[:-4]
    return normalized == "https://github.com/aspnet/benchmarks"
