import json
import os
import tempfile
import unittest

import tests  # noqa: F401  # ensures sys.path is set up

from config_loader import ConfigError, load_config


_BASE = {
    "metadata": {
        "name": "t",
        "schedule": "0 3/12 * * *",
        "queues": ["a", "b"],
    },
    "pods": [
        {
            "name": "p1",
            "machines": {"sut": "m1"},
            "profiles": {"sut": "m1-app"},
        }
    ],
    "scenarios": [
        {
            "name": "S",
            "template": "s.yml",
            "type": 1,
            "pods": ["p1"],
        }
    ],
}


def _write(tmp, payload):
    path = os.path.join(tmp, "cfg.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f)
    return path


def _enable_trend(payload):
    payload["metadata"]["trend_lane_registry"] = "lanes.json"
    payload["metadata"]["pipeline"] = {
        "trend_benchmarks_raw_base_url": (
            "https://raw.githubusercontent.com/aspnet/Benchmarks/"
            "$(Build.SourceVersion)"
        )
    }
    payload["scenarios"][0]["template"] = "trend-scenarios.yml"


class TestLoadConfig(unittest.TestCase):
    def test_happy_path(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = _write(tmp, _BASE)
            cfg = load_config(path)
            self.assertIn("p1", cfg.pods)
            self.assertEqual(len(cfg.scenarios), 1)
            self.assertEqual(cfg.pipeline.pool, "server")

    def test_unsupported_cron_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            payload["metadata"]["schedule"] = "0 * * * *"
            path = _write(tmp, payload)
            with self.assertRaises(ConfigError):
                load_config(path)

    def test_empty_queues_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            payload["metadata"]["queues"] = []
            path = _write(tmp, payload)
            with self.assertRaises(ConfigError):
                load_config(path)

    def test_duplicate_pods_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            payload["pods"].append(dict(payload["pods"][0]))
            path = _write(tmp, payload)
            with self.assertRaises(ConfigError):
                load_config(path)

    def test_duplicate_pods_in_scenario_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            payload["scenarios"][0]["pods"] = ["p1", "p1"]
            path = _write(tmp, payload)
            with self.assertRaises(ConfigError):
                load_config(path)

    def test_pipeline_settings_loaded(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            payload["metadata"]["pipeline"] = {
                "pool": "custompool",
                "service_bus_namespace": "myns",
            }
            path = _write(tmp, payload)
            cfg = load_config(path)
            self.assertEqual(cfg.pipeline.pool, "custompool")
            self.assertEqual(cfg.pipeline.service_bus_namespace, "myns")
            self.assertEqual(
                cfg.pipeline.service_bus_connection,
                "ASPNET Benchmarks Service Bus",
            )

    def test_optional_timeout_loaded(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            payload["scenarios"][0]["timeout"] = 199
            path = _write(tmp, payload)
            cfg = load_config(path)
            self.assertEqual(cfg.scenarios[0].timeout, 199)

    def test_negative_timeout_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            payload["scenarios"][0]["timeout"] = 0
            path = _write(tmp, payload)
            with self.assertRaises(ConfigError):
                load_config(path)

    def test_trend_scenario_requires_lane_registry_entry(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            payload["scenarios"][0]["template"] = "trend-scenarios.yml"
            payload["metadata"]["pipeline"] = {
                "trend_benchmarks_raw_base_url": (
                    "https://raw.githubusercontent.com/aspnet/Benchmarks/"
                    "$(Build.SourceVersion)"
                )
            }
            path = _write(tmp, payload)
            with self.assertRaisesRegex(ConfigError, "no entry"):
                load_config(path)

    def test_trend_lane_registry_is_loaded(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            _enable_trend(payload)
            with open(
                os.path.join(tmp, "lanes.json"), "w", encoding="utf-8"
            ) as f:
                json.dump({
                    "schemaVersion": 1,
                    "lanes": {
                        "p1": {
                            "name": "stable-lane",
                            "queue": "Ubuntu.2204.Amd64.Test.Perf",
                            "os": "Ubuntu 22.04",
                            "architecture": "x64",
                            "locale": "en-US",
                            "cores": 4,
                            "hardware": "Test",
                        }
                    },
                }, f)
            path = _write(tmp, payload)

            cfg = load_config(path)

            lane = cfg.pods["p1"].perf_lab_lane
            self.assertIsNotNone(lane)
            self.assertEqual(lane.queue, "Ubuntu.2204.Amd64.Test.Perf")

    def test_trend_lane_cannot_reuse_service_bus_routing_queue(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            _enable_trend(payload)
            with open(
                os.path.join(tmp, "lanes.json"), "w", encoding="utf-8"
            ) as f:
                json.dump({
                    "schemaVersion": 1,
                    "lanes": {
                        "p1": {
                            "name": "stable-lane",
                            "queue": "a",
                            "os": "Ubuntu 22.04",
                            "architecture": "x64",
                            "locale": "en-US",
                            "cores": 4,
                            "hardware": "Test",
                        }
                    },
                }, f)
            path = _write(tmp, payload)

            with self.assertRaisesRegex(
                ConfigError,
                "routing queues cannot be used",
            ):
                load_config(path)

    def test_trend_raw_base_url_must_pin_build_source_version(self):
        with tempfile.TemporaryDirectory() as tmp:
            payload = json.loads(json.dumps(_BASE))
            _enable_trend(payload)
            payload["metadata"]["pipeline"][
                "trend_benchmarks_raw_base_url"
            ] = "https://raw.githubusercontent.com/aspnet/Benchmarks/main"
            with open(
                os.path.join(tmp, "lanes.json"), "w", encoding="utf-8"
            ) as f:
                json.dump({
                    "schemaVersion": 1,
                    "lanes": {
                        "p1": {
                            "name": "stable-lane",
                            "queue": "Ubuntu.2204.Amd64.Test.Perf",
                            "os": "Ubuntu 22.04",
                            "architecture": "x64",
                            "locale": "en-US",
                            "cores": 4,
                            "hardware": "Test",
                        }
                    },
                }, f)
            path = _write(tmp, payload)

            with self.assertRaisesRegex(
                ConfigError,
                "must be pinned",
            ):
                load_config(path)


if __name__ == "__main__":
    unittest.main()
