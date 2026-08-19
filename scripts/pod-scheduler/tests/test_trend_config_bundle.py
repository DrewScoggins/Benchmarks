import json
import os
import re
import unittest

import tests  # noqa: F401  # ensures sys.path is set up

from trend_config_bundle import (
    BENCHMARKS_COMMIT_TEMPLATE,
    BENCHMARKS_COMMIT_VARIABLE,
    TREND_CONFIG_SOURCES,
    build_trend_config_document,
    bundle_repo_path,
)


_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.abspath(os.path.join(_HERE, "..", "..", ".."))
_BUILD = os.path.join(_REPO, "build")
_BENCHMARKS_RAW_MAIN = (
    "https://raw.githubusercontent.com/aspnet/Benchmarks/main/"
)


def _read_build(name):
    with open(
        os.path.join(_BUILD, name),
        encoding="utf-8-sig",
    ) as file:
        return file.read()


def _is_benchmarks_repository(value):
    normalized = value.strip().rstrip("/").lower()
    if normalized.endswith(".git"):
        normalized = normalized[:-4]
    return normalized == "https://github.com/aspnet/benchmarks"


class TestTrendConfigBundle(unittest.TestCase):
    def test_source_roots_cover_every_trend_config_argument(self):
        roots = {"build/ci.profile.yml"}
        for template in (
            "trend-scenarios.yml",
            "trend-database-scenarios.yml",
        ):
            roots.update(re.findall(
                r"\$\{\{ parameters\.benchmarksRawBaseUrl \}\}/"
                r"([^\s\"\\]+\.ya?ml)",
                _read_build(template),
            ))

        self.assertEqual(set(TREND_CONFIG_SOURCES), roots)

    def test_committed_bundle_matches_recursive_generation(self):
        transitive_sources = set()
        for source_path in TREND_CONFIG_SOURCES:
            with self.subTest(source=source_path):
                expected, dependencies = build_trend_config_document(
                    _REPO,
                    source_path,
                )
                transitive_sources.update(dependencies)
                committed_path = os.path.join(
                    _REPO,
                    bundle_repo_path(source_path).replace("/", os.sep),
                )
                self.assertTrue(
                    os.path.isfile(committed_path),
                    f"Missing generated Trend config {committed_path}",
                )
                with open(committed_path, encoding="utf-8") as file:
                    actual = json.load(file)
                self.assertEqual(expected, actual)

        self.assertIn("scenarios/aspnet.profiles.yml", transitive_sources)
        self.assertIn(
            "scenarios/aspnet.profiles.standard.yml",
            transitive_sources,
        )
        self.assertIn("scenarios/packages.yml", transitive_sources)
        self.assertGreater(
            len(transitive_sources),
            len(TREND_CONFIG_SOURCES),
        )

    def test_recursive_trend_config_graph_has_no_floating_benchmarks_references(
        self,
    ):
        benchmarks_sources = 0
        pinned_raw_assets = 0
        for source_path in TREND_CONFIG_SOURCES:
            with self.subTest(source=source_path):
                generated_path = os.path.join(
                    _REPO,
                    bundle_repo_path(source_path).replace("/", os.sep),
                )
                with open(generated_path, encoding="utf-8") as file:
                    document = json.load(file)

                self.assertEqual(
                    "main",
                    document["variables"][BENCHMARKS_COMMIT_VARIABLE],
                )
                serialized = json.dumps(document)
                self.assertNotIn(_BENCHMARKS_RAW_MAIN, serialized)
                self.assertNotIn(
                    "raw.githubusercontent.com/aspnet/Benchmarks/"
                    "refs/heads/main/",
                    serialized,
                )

                for imported in document.get("imports", []):
                    self.assertNotRegex(
                        imported,
                        r"raw\.githubusercontent\.com/"
                        r"aspnet/Benchmarks/",
                    )

                for node in self._walk(document):
                    if isinstance(node, str):
                        raw_prefix = (
                            "raw.githubusercontent.com/aspnet/Benchmarks/"
                        )
                        if raw_prefix in node:
                            self.assertIn(
                                f"{raw_prefix}"
                                f"{BENCHMARKS_COMMIT_TEMPLATE}/",
                                node,
                            )
                            pinned_raw_assets += 1
                        continue
                    if not isinstance(node, dict):
                        continue
                    repository = node.get("repository")
                    if (
                        isinstance(repository, str)
                        and _is_benchmarks_repository(repository)
                    ):
                        benchmarks_sources += 1
                        self.assertEqual(
                            BENCHMARKS_COMMIT_TEMPLATE,
                            node.get("branchOrCommit"),
                        )
                        self.assertNotEqual(
                            "main",
                            node.get("branchOrCommit"),
                        )

        self.assertGreater(benchmarks_sources, 0)
        self.assertGreater(pinned_raw_assets, 0)

    @classmethod
    def _walk(cls, node):
        if isinstance(node, dict):
            yield node
            for value in node.values():
                yield from cls._walk(value)
        elif isinstance(node, list):
            for value in node:
                yield from cls._walk(value)
        else:
            yield node


if __name__ == "__main__":
    unittest.main()
