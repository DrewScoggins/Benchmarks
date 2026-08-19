import json
import os
import re
import textwrap
import unittest


_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.abspath(os.path.join(_HERE, "..", "..", ".."))
_BUILD = os.path.join(_REPO, "build")
_TEMPLATES = [
    "trend-scenarios.yml",
    "trend-database-scenarios.yml",
]
_PIPELINES = [
    "benchmarks-ci-01.yml",
    "benchmarks-ci-02.yml",
    "benchmarks-ci-azure.yml",
    "benchmarks-ci-cobalt.yml",
]
_ROUTING_QUEUES = {
    "citrine1",
    "citrine2",
    "citrine3",
    "mono",
    "azure",
    "azurearm64",
    "cobalthosted",
    "cobalthosted_azurelinux3",
}


def _read(name):
    with open(os.path.join(_BUILD, name), encoding="utf-8-sig") as file:
        return file.read()


def _scenarios(template):
    text = _read(template)
    default = text.index("  default:")
    steps = text.index("\nsteps:", default)
    block = text[default:steps]
    entries = []
    current = None
    for line in block.splitlines():
        match = re.match(r"^  - displayName: (.+)$", line)
        if match:
            if current:
                entries.append(current)
            current = {"displayName": match.group(1)}
            continue
        match = re.match(
            r"^    (testName|family|categories|arguments): (.*)$", line
        )
        if match and current is not None:
            current[match.group(1)] = match.group(2)
    if current:
        entries.append(current)
    return entries


def _expected_family(test_name):
    if test_name.startswith("Plaintext") or test_name.startswith(
        "ConnectionClose"
    ):
        return "aspnet-plaintext"
    if test_name.startswith("Json"):
        return "aspnet-json"
    if test_name.startswith("Antiforgery"):
        return "aspnet-antiforgery"
    if "TLS" in test_name:
        return "aspnet-tls"
    if test_name.startswith("Rejection"):
        return "aspnet-request-rejection"
    if test_name.startswith("Fortunes"):
        return "aspnet-fortunes"
    if test_name.startswith("SingleQuery"):
        return "aspnet-single-query"
    if test_name.startswith("MultipleQueries"):
        return "aspnet-multiple-queries"
    if test_name.startswith("Updates"):
        return "aspnet-updates"
    if test_name.startswith("Caching"):
        return "aspnet-caching"
    raise AssertionError(f"No expected family rule for {test_name}")


class TestTrendTemplateContract(unittest.TestCase):
    def test_every_scenario_has_explicit_stable_identity(self):
        for template in _TEMPLATES:
            with self.subTest(template=template):
                scenarios = _scenarios(template)
                self.assertTrue(scenarios)
                for scenario in scenarios:
                    self.assertIn("testName", scenario)
                    self.assertIn("family", scenario)
                    self.assertIn("categories", scenario)
                    self.assertEqual(
                        _expected_family(scenario["testName"]),
                        scenario["family"],
                    )
                    property_match = re.search(
                        r"--property scenario=([^\s]+)",
                        scenario["arguments"],
                    )
                    self.assertIsNotNone(property_match)
                    self.assertEqual(
                        scenario["testName"], property_match.group(1)
                    )

    def test_raw_json_identity_sql_and_post_process_are_present(self):
        required_properties = [
            "perflab.build.repo",
            "perflab.build.branch",
            "perflab.lane.name",
            "perflab.lane.queue",
            "perflab.lane.os.name",
            "perflab.lane.os.architecture",
            "perflab.lane.os.locale",
            "perflab.configuration.Framework",
            "perflab.configuration.Runtime",
            "perflab.configuration.Cores",
            "perflab.configuration.Topology",
            "perflab.scenario.name",
            "perflab.scenario.family",
            "perflab.scenario.categories",
            "perflab.perfRepoHash",
            "perflab.azureDevOps.project",
            "perflab.azureDevOps.pipeline",
            "perflab.azureDevOps.buildId",
            "perflab.azureDevOps.buildNumber",
            "perflab.azureDevOps.buildUrl",
            "perflab.sql.session",
            "perflab.sql.table",
            "perflab.policy.path",
        ]
        for template in _TEMPLATES:
            with self.subTest(template=template):
                text = _read(template)
                self.assertNotIn(
                    "raw.githubusercontent.com/aspnet/Benchmarks/main",
                    text,
                )
                self.assertIn("--json crank-results.json", text)
                self.assertIn("--no-measurements", text)
                self.assertIn("--table TrendBenchmarks", text)
                self.assertIn("--sql SQL_CONNECTION_STRING", text)
                for property_name in required_properties:
                    self.assertIn(property_name, text)
                self.assertIn('"name": "Crank PerfLab export"', text)
                self.assertIn('"--identity-source", "crank"', text)
                self.assertIn(
                    '"--crank-json", "crank-results.json"', text
                )
                self.assertIn(
                    '"--counter-policy", '
                    '"${{ parameters.adapterPolicyPath }}"',
                    text,
                )
                self.assertIn(
                    '"--storage-account", '
                    '"${{ parameters.perfLabStorageAccount }}"',
                    text,
                )
                self.assertIn(
                    '"--container", '
                    '"${{ parameters.perfLabContainer }}"',
                    text,
                )
                self.assertIn(
                    '"--queue", '
                    '"${{ parameters.perfLabResultsQueue }}"',
                    text,
                )
                message_body = text[text.index("        {\n"):]
                payload = json.loads(textwrap.dedent(message_body))
                self.assertEqual("crank", payload["name"])
                self.assertEqual(
                    "Crank PerfLab export",
                    payload["postProcess"]["name"],
                )

    def test_post_process_uses_only_credential_environment_references(self):
        for template in _TEMPLATES:
            with self.subTest(template=template):
                text = _read(template)
                post_process = text[text.index('"postProcess"'):]
                self.assertIn(
                    "--tenant-id-environment-variable", post_process
                )
                self.assertIn(
                    "--client-id-environment-variable", post_process
                )
                self.assertIn(
                    "--certificate-base64-environment-variable",
                    post_process,
                )
                self.assertIn(
                    "--certificate-password-environment-variable",
                    post_process,
                )
                self.assertNotRegex(
                    post_process,
                    r'"--(tenant-id|client-id|certificate-path)"\s*,',
                )

    def test_generated_trend_callers_use_registered_perflab_lanes(self):
        with open(
            os.path.join(_BUILD, "trend-perflab-lanes.json"),
            encoding="utf-8",
        ) as file:
            registry = json.load(file)["lanes"]
        expected_queues = {
            "gold-lin": "Ubuntu.2204.Amd64.AspNetGold.Perf",
            "gold-win": "Windows.Server2022.Amd64.AspNetGold.Perf",
            "azure-arm64": "Ubuntu.2204.Arm64.AspNetAzure.Perf",
            "azure2-amd64": "Ubuntu.2204.Amd64.AspNetAzure2.Perf",
            "cobalt-cloud-lin": (
                "Ubuntu.2204.Amd64.AspNetCobaltCloud.Perf"
            ),
            "cobalt-cloud-lin-azl3": (
                "AzureLinux.3.Amd64.AspNetCobaltCloud.Perf"
            ),
            "cobalt-cloud-lin-azl3-dual": (
                "AzureLinux.3.Amd64.AspNetCobaltCloud.Perf"
            ),
            "idna-amd-lin": "Ubuntu.2204.Amd64.AspNetIdnaAmd.Perf",
            "idna-amd-win": (
                "Windows.Server2022.Amd64.AspNetIdnaAmd.Perf"
            ),
            "idna-intel-lin": (
                "Ubuntu.2204.Amd64.AspNetIdnaIntel.Perf"
            ),
            "idna-intel-win": (
                "Windows.Server2022.Amd64.AspNetIdnaIntel.Perf"
            ),
            "cobalt-hosted-lin": (
                "Ubuntu.2204.Amd64.AspNetCobaltHosted.Perf"
            ),
            "cobalt-hosted-lin-azl3": (
                "AzureLinux.3.Amd64.AspNetCobaltHosted.Perf"
            ),
            "cobalt-hosted-lin-28": (
                "Ubuntu.2204.Amd64.AspNetCobaltHosted.Perf"
            ),
            "cobalt-hosted-lin-azl3-28": (
                "AzureLinux.3.Amd64.AspNetCobaltHosted.Perf"
            ),
        }
        self.assertEqual(
            expected_queues,
            {pod: lane["queue"] for pod, lane in registry.items()},
        )

        call_count = 0
        for pipeline in _PIPELINES:
            text = _read(pipeline)
            pattern = re.compile(
                r"  - template: trend(?:-database)?-scenarios\.yml\n"
                r"    parameters:\n"
                r"(?P<parameters>(?:      .+\n)+)"
            )
            for match in pattern.finditer(text):
                call_count += 1
                job_start = text.rfind("\n- job:", 0, match.start())
                job_prefix = text[job_start:match.start()]
                for variable in [
                    "ciProfile",
                    "azureProfile",
                    "platformJobs",
                    "plaintextJobs",
                    "databaseJobs",
                    "jsonJobs",
                    "minimalJobs",
                ]:
                    self.assertIn(
                        f'    {variable}: "--config '
                        "https://raw.githubusercontent.com/aspnet/Benchmarks/"
                        "$(Build.SourceVersion)/",
                        job_prefix,
                    )
                parameters = dict(
                    re.findall(
                        r"^      ([A-Za-z0-9]+):\s+\"?([^\"\n]+)\"?$",
                        match.group("parameters"),
                        re.MULTILINE,
                    )
                )
                routing_queue = parameters["serviceBusQueueName"]
                perf_queue = parameters["perfLabQueue"]
                display_name = re.search(
                    r"displayName: \d+- Trends(?: Database)? ([^\n]+)",
                    job_prefix,
                )
                self.assertIsNotNone(display_name)
                pod_name = display_name.group(1)
                lane = registry[pod_name]
                self.assertIn(routing_queue, _ROUTING_QUEUES)
                self.assertNotIn(perf_queue, _ROUTING_QUEUES)
                self.assertNotEqual(routing_queue, perf_queue)
                self.assertEqual(lane["queue"], perf_queue)
                self.assertEqual(lane["name"], parameters["perfLabLaneName"])
                self.assertEqual(lane["os"], parameters["perfLabOs"])
                self.assertEqual(
                    lane["architecture"],
                    parameters["perfLabArchitecture"],
                )
                self.assertEqual(
                    str(lane["cores"]),
                    parameters["perfLabCores"],
                )
                for required in [
                    "perfLabLaneName",
                    "perfLabOs",
                    "perfLabArchitecture",
                    "perfLabLocale",
                    "perfLabCores",
                    "perfLabHardware",
                    "perfLabTopology",
                ]:
                    self.assertIn(required, parameters)
        self.assertGreater(call_count, 0)


if __name__ == "__main__":
    unittest.main()
