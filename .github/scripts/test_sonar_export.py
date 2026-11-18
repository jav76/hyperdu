#!/usr/bin/env python3
"""Unit tests for sonar_export.py."""

from __future__ import annotations

from datetime import datetime, timezone
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import urllib.error
import urllib.parse
import zipfile

SCRIPTS_DIR = Path(__file__).resolve().parent
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import sonar_export as se  # noqa: E402


class FakeHttpResponse:
    def __init__(self, payload: bytes) -> None:
        self._payload = payload

    def read(self) -> bytes:
        return self._payload

    def __enter__(self) -> FakeHttpResponse:
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        return None


def make_cnes_zip(md_text: str, csv_text: str) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("2026-11-18-hyperdu-analysis-report.md", md_text)
        zf.writestr("2026-11-18-hyperdu-issues-report.csv", csv_text)
    return buf.getvalue()


class SonarExportTests(unittest.TestCase):
    def test_parse_report_task_and_resolve_config(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            rt = tmp_path / "report-task.txt"
            rt.write_text(
                "\n".join(
                    [
                        "# comment",
                        "projectKey=jav76_hyperdu_123",
                        "serverUrl=https://sonarqube.local.jav26122.net",
                        "ceTaskId=TASK-001",
                    ]
                ),
                encoding="utf-8",
            )
            parsed = se.parse_report_task(rt)
            self.assertEqual(parsed["projectKey"], "jav76_hyperdu_123")
            self.assertEqual(parsed["ceTaskId"], "TASK-001")

            parser = se.build_arg_parser()
            args = parser.parse_args(
                [
                    "--token",
                    "sqa_secret",
                    "--report-task",
                    str(rt),
                    "--repo-url",
                    "https://github.com/jav76/hyperdu/",
                ]
            )
            cfg = se.resolve_config(args)
            self.assertEqual(cfg.host_url, "https://sonarqube.local.jav26122.net")
            self.assertEqual(cfg.project_key, "jav76_hyperdu_123")
            self.assertEqual(cfg.ce_task_id, "TASK-001")
            self.assertEqual(cfg.repo_url, "https://github.com/jav76/hyperdu")

    def test_resolve_config_env_fallbacks_and_errors(self) -> None:
        with mock.patch.dict(
            os.environ,
            {
                "SONAR_HOST_URL": "https://sonarqube.local.jav26122.net/",
                "SONAR_TOKEN": "sqa_env_token",
                "SONAR_BADGE_TOKEN": "sqb_env_badge",
            },
            clear=False,
        ):
            parser = se.build_arg_parser()
            args = parser.parse_args(
                ["--project-key", "proj-1", "--report-task", "/nonexistent/report-task.txt"]
            )
            cfg = se.resolve_config(args)
            self.assertEqual(cfg.host_url, "https://sonarqube.local.jav26122.net")
            self.assertEqual(cfg.token, "sqa_env_token")
            self.assertEqual(cfg.badge_token, "sqb_env_badge")
            self.assertIsNone(cfg.ce_task_id)

        with mock.patch.dict(os.environ, {}, clear=True):
            parser = se.build_arg_parser()
            args = parser.parse_args(["--report-task", "/nonexistent/report-task.txt"])
            with self.assertRaises(ValueError):
                se.resolve_config(args)

    def test_poll_ce_task_transitions_failures_and_timeout(self) -> None:
        statuses = iter(["PENDING", "IN_PROGRESS", "SUCCESS"])

        class StubClient:
            def get_json(self, path: str, params=None):
                self.last = (path, params)
                return {"task": {"id": "T1", "status": next(statuses)}}

        sleeps: list[float] = []
        now = [0.0]

        def fake_monotonic() -> float:
            return now[0]

        def fake_sleep(sec: float) -> None:
            sleeps.append(sec)
            now[0] += sec

        task = se.poll_ce_task(
            StubClient(),  # type: ignore[arg-type]
            "T1",
            timeout=10.0,
            poll_interval=1.5,
            sleep_fn=fake_sleep,
            monotonic_fn=fake_monotonic,
        )
        self.assertEqual(task["status"], "SUCCESS")
        self.assertEqual(sleeps, [1.5, 1.5])

        class FailedClient:
            def get_json(self, path: str, params=None):
                return {"task": {"id": "T2", "status": "FAILED", "errorMessage": "boom"}}

        with self.assertRaises(RuntimeError) as ctx:
            se.poll_ce_task(FailedClient(), "T2", timeout=5.0)  # type: ignore[arg-type]
        self.assertIn("FAILED", str(ctx.exception))

        class PendingClient:
            def get_json(self, path: str, params=None):
                return {"task": {"id": "T3", "status": "PENDING"}}

        t = [0.0]
        with self.assertRaises(TimeoutError):
            se.poll_ce_task(
                PendingClient(),  # type: ignore[arg-type]
                "T3",
                timeout=3.0,
                poll_interval=2.0,
                sleep_fn=lambda s: t.__setitem__(0, t[0] + s),
                monotonic_fn=lambda: t[0],
            )

    def test_fetch_paginated_items_and_10k_cap(self) -> None:
        pages = {
            1: {
                "paging": {"pageIndex": 1, "pageSize": 2, "total": 5},
                "issues": [{"key": "i1"}, {"key": "i2"}],
                "components": [{"key": "p:a.cs", "path": "a.cs"}],
                "rules": [{"key": "csharpsquid:S1", "name": "Rule 1"}],
            },
            2: {
                "paging": {"pageIndex": 2, "pageSize": 2, "total": 5},
                "issues": [{"key": "i3"}, {"key": "i4"}],
                "components": [],
                "rules": [],
            },
            3: {
                "paging": {"pageIndex": 3, "pageSize": 2, "total": 5},
                "issues": [{"key": "i5"}],
                "components": [],
                "rules": [],
            },
        }

        class PagedClient:
            def get_json(self, path: str, params=None):
                return pages[int(params["p"])]

        items, comps, rules, total = se.fetch_paginated_items(
            PagedClient(),  # type: ignore[arg-type]
            "/api/issues/search",
            {"componentKeys": "p"},
            "issues",
            page_size=2,
            max_results=4,
        )
        self.assertEqual([i["key"] for i in items], ["i1", "i2", "i3", "i4"])
        self.assertEqual(total, 5)
        self.assertIn("p:a.cs", comps)
        self.assertIn("csharpsquid:S1", rules)

    def test_build_sarif_filtering_mapping_and_redaction(self) -> None:
        project_key = "jav76_hyperdu"
        issues = [
            {
                "key": "issue-major",
                "component": f"{project_key}:hyperdu.Cli/Program.cs",
                "rule": "csharpsquid:S6966",
                "severity": "MAJOR",
                "impacts": [{"softwareQuality": "RELIABILITY", "severity": "MEDIUM"}],
                "message": "Await WriteLineAsync instead of WriteLine on https://sonarqube.local.jav26122.net",
                "textRange": {"startLine": 358, "endLine": 358, "startOffset": 8, "endOffset": 30},
            },
            {
                "key": "issue-mqr-high",
                "component": f"{project_key}:hyperdu.Core\\Native\\NativeMethods.cs",
                "rule": "csharpsquid:S6640",
                "severity": "MINOR",
                "impacts": [{"softwareQuality": "SECURITY", "severity": "HIGH"}],
                "message": "Unsafe code block",
                "line": 12,
            },
            {
                "key": "issue-blocker",
                "component": project_key,
                "rule": "roslyn:CA1000",
                "severity": "BLOCKER",
                "impacts": [],
                "message": "Project level blocker",
            },
            {
                "key": "issue-minor-excluded",
                "component": f"{project_key}:hyperdu.Cli/Other.cs",
                "rule": "csharpsquid:S1234",
                "severity": "MINOR",
                "impacts": [{"softwareQuality": "MAINTAINABILITY", "severity": "LOW"}],
                "message": "Minor style issue",
                "line": 5,
            },
        ]
        hotspots = [
            {
                "key": "hs-1",
                "component": f"{project_key}:hyperdu.Core/Scanning/Helper.cs",
                "ruleKey": "csharpsquid:S2068",
                "securityCategory": "auth",
                "vulnerabilityProbability": "HIGH",
                "status": "TO_REVIEW",
                "line": 42,
                "message": "Check hardcoded credential pattern",
            },
            {
                "key": "hs-2",
                "component": f"{project_key}:hyperdu.Core/Scanning/Helper.cs",
                "ruleKey": "csharpsquid:S5042",
                "securityCategory": "dos",
                "vulnerabilityProbability": "LOW",
                "status": "TO_REVIEW",
                "line": 99,
                "message": "Archive expansion check",
            },
        ]
        components = {
            f"{project_key}:hyperdu.Cli/Program.cs": {"path": "hyperdu.Cli/Program.cs"},
        }
        rules = {
            "csharpsquid:S6966": {"key": "csharpsquid:S6966", "name": "Awaitable method should be used"},
        }

        sarif = se.build_sarif(
            project_key=project_key,
            issues=issues,
            hotspots=hotspots,
            components_by_key=components,
            rules_by_key=rules,
            repo_url="https://github.com/jav76/hyperdu",
            host_url="https://sonarqube.local.jav26122.net",
        )
        serialized = json.dumps(sarif)
        self.assertNotIn("sonarqube.local.jav26122.net", serialized)
        self.assertEqual(sarif["version"], "2.1.0")
        run = sarif["runs"][0]
        self.assertEqual(run["tool"]["driver"]["informationUri"], "https://github.com/jav76/hyperdu")

        results = run["results"]
        self.assertEqual(len(results), 5)  # 3 qualifying issues + 2 hotspots; minor issue excluded
        fps = [r["partialFingerprints"]["sonarIssueKey"] for r in results]
        self.assertEqual(fps, ["issue-major", "issue-mqr-high", "issue-blocker", "hs-1", "hs-2"])

        self.assertEqual(results[0]["level"], "warning")
        self.assertEqual(
            results[0]["locations"][0]["physicalLocation"]["region"],
            {"startLine": 358, "endLine": 358, "startColumn": 9, "endColumn": 31},
        )
        self.assertEqual(results[1]["level"], "error")
        self.assertEqual(
            results[1]["locations"][0]["physicalLocation"]["artifactLocation"]["uri"],
            "hyperdu.Core/Native/NativeMethods.cs",
        )
        self.assertEqual(results[2]["level"], "error")
        self.assertEqual(
            results[2]["locations"][0]["physicalLocation"]["artifactLocation"]["uri"],
            ".",
        )
        self.assertEqual(results[3]["level"], "error")
        self.assertEqual(results[4]["level"], "note")

        driver_rules = {r["id"]: r for r in run["tool"]["driver"]["rules"]}
        self.assertEqual(
            driver_rules["csharpsquid:S6966"]["helpUri"],
            "https://rules.sonarsource.com/csharp/RSPEC-6966/",
        )
        self.assertNotIn("helpUri", driver_rules["roslyn:CA1000"])

    def test_generate_badges_native_and_fallback(self) -> None:
        measures = {
            "alert_status": "ERROR",
            "software_quality_reliability_rating": "3.0",
            "software_quality_security_rating": "2.0",
            "software_quality_maintainability_rating": "1.0",
            "coverage": "45.6",
            "ncloc": "12345",
            "software_quality_maintainability_remediation_effort": "960",
        }

        # Fallback without badge token (including XML escaping check)
        badges_local = se.generate_badges(None, "proj", measures, "ERROR", None)
        self.assertEqual(
            set(badges_local.keys()),
            {
                "quality_gate.svg",
                "reliability.svg",
                "security.svg",
                "maintainability.svg",
                "coverage.svg",
                "ncloc.svg",
                "tech_debt.svg",
            },
        )
        self.assertIn(">failed<", badges_local["quality_gate.svg"])
        self.assertIn(">C<", badges_local["reliability.svg"])
        self.assertIn(">B<", badges_local["security.svg"])
        self.assertIn(">A<", badges_local["maintainability.svg"])
        self.assertIn(">45.6%<", badges_local["coverage.svg"])
        self.assertIn(">12.3k<", badges_local["ncloc.svg"])
        self.assertIn(">2d<", badges_local["tech_debt.svg"])

        escaped_svg = se.render_static_badge_svg("a&b", "<x>", "#4c1")
        self.assertIn("a&amp;b", escaped_svg)
        self.assertIn("&lt;x&gt;", escaped_svg)

        # Native fetch with partial failure fallback
        class BadgeClient:
            def get_bytes(self, path: str, params=None, *, authenticated: bool = True) -> bytes:
                if path == "/api/project_badges/quality_gate":
                    return b"<svg><title>native-qg</title></svg>"
                if params and params.get("metric") == "coverage":
                    raise RuntimeError("404")
                return b"<svg><title>native</title></svg>"

        badges_mixed = se.generate_badges(
            BadgeClient(),  # type: ignore[arg-type]
            "proj",
            measures,
            "ERROR",
            "sqb_tok",
        )
        self.assertIn("native-qg", badges_mixed["quality_gate.svg"])
        self.assertIn("45.6%", badges_mixed["coverage.svg"])

    def test_end_to_end_run_export_sanitization_and_xss_escaping(self) -> None:
        cnes_zip = make_cnes_zip(
            md_text="Report from https://sonarqube.local.jav26122.net with token sqa_top_secret",
            csv_text="key;url\ni1;https://sonarqube.local.jav26122.net/project/issues",
        )

        def fake_opener(req, timeout=30.0):
            parsed = urllib.parse.urlparse(req.full_url)
            qs = urllib.parse.parse_qs(parsed.query)
            auth = req.headers.get("Authorization", "")
            if parsed.path != "/api/project_badges/quality_gate" and not parsed.path.startswith(
                "/api/project_badges/"
            ):
                self.assertTrue(auth.startswith("Basic "))

            if parsed.path == "/api/ce/task":
                return FakeHttpResponse(
                    json.dumps({"task": {"id": qs["id"][0], "status": "SUCCESS"}}).encode("utf-8")
                )
            if parsed.path == "/api/qualitygates/project_status":
                return FakeHttpResponse(
                    json.dumps(
                        {
                            "projectStatus": {
                                "status": "OK",
                                "conditions": [
                                    {
                                        "status": "OK",
                                        "metricKey": "new_reliability_rating",
                                        "comparator": "GT",
                                        "errorThreshold": "2",
                                        "actualValue": "1",
                                    }
                                ],
                            }
                        }
                    ).encode("utf-8")
                )
            if parsed.path == "/api/measures/component":
                return FakeHttpResponse(
                    json.dumps(
                        {
                            "component": {
                                "key": "jav76_hyperdu",
                                "measures": [
                                    {"metric": "ncloc", "value": "12000"},
                                    {"metric": "coverage", "value": "82.4"},
                                    {"metric": "duplicated_lines_density", "value": "0.7"},
                                    {"metric": "software_quality_reliability_rating", "value": "1.0"},
                                    {"metric": "software_quality_security_rating", "value": "1.0"},
                                    {"metric": "software_quality_maintainability_rating", "value": "1.0"},
                                    {"metric": "software_quality_reliability_issues", "value": "0"},
                                    {"metric": "software_quality_security_issues", "value": "0"},
                                    {"metric": "software_quality_maintainability_issues", "value": "1"},
                                    {"metric": "software_quality_maintainability_remediation_effort", "value": "15"},
                                    {"metric": "alert_status", "value": "OK"},
                                ],
                            }
                        }
                    ).encode("utf-8")
                )
            if parsed.path == "/api/issues/search":
                return FakeHttpResponse(
                    json.dumps(
                        {
                            "paging": {"pageIndex": 1, "pageSize": 500, "total": 1},
                            "issues": [
                                {
                                    "key": "iss-xss",
                                    "component": "jav76_hyperdu:hyperdu.Cli/Program.cs",
                                    "rule": "csharpsquid:S1000",
                                    "severity": "MAJOR",
                                    "impacts": [
                                        {"softwareQuality": "MAINTAINABILITY", "severity": "MEDIUM"}
                                    ],
                                    "message": "<script>alert('xss')</script> | pipe\nline2",
                                    "line": 10,
                                }
                            ],
                            "components": [
                                {
                                    "key": "jav76_hyperdu:hyperdu.Cli/Program.cs",
                                    "path": "hyperdu.Cli/Program.cs",
                                }
                            ],
                            "rules": [
                                {"key": "csharpsquid:S1000", "name": "Sample <rule>"}
                            ],
                        }
                    ).encode("utf-8")
                )
            if parsed.path == "/api/hotspots/search":
                qs = urllib.parse.parse_qs(parsed.query)
                self.assertEqual(qs.get("project"), ["jav76_hyperdu"])
                self.assertNotIn("projectKey", qs)
                return FakeHttpResponse(
                    json.dumps(
                        {"paging": {"pageIndex": 1, "pageSize": 500, "total": 0}, "hotspots": []}
                    ).encode("utf-8")
                )
            if parsed.path == "/api/cnesreport/report":
                return FakeHttpResponse(cnes_zip)
            raise AssertionError(f"Unexpected path: {parsed.path}")

        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            rt = tmp_path / "report-task.txt"
            rt.write_text(
                "projectKey=jav76_hyperdu\nceTaskId=TASK-99\nserverUrl=https://sonarqube.local.jav26122.net\n",
                encoding="utf-8",
            )
            sarif_out = tmp_path / "out.sarif"
            pages_dir = tmp_path / "pages"
            summary_out = tmp_path / "summary.md"

            cfg = se.ExportConfig(
                host_url="https://sonarqube.local.jav26122.net",
                token="sqa_top_secret",
                project_key="jav76_hyperdu",
                ce_task_id="TASK-99",
                sarif_out=sarif_out,
                pages_dir=pages_dir,
                summary_out=summary_out,
                badge_token=None,
                repo_url="https://github.com/jav76/hyperdu",
                timeout=30.0,
            )
            client = se.SonarApiClient(cfg.host_url, cfg.token, opener=fake_opener)
            payload = se.run_export(
                cfg,
                client=client,
                now_fn=lambda: datetime(2026, 11, 18, 16, 50, 0, tzinfo=timezone.utc),
            )
            self.assertEqual(payload["qualityGate"]["status"], "OK")

            expected_files = [
                sarif_out,
                summary_out,
                pages_dir / "metrics.json",
                pages_dir / "report.md",
                pages_dir / "index.html",
                pages_dir / "cnes-report.md",
                pages_dir / "cnes-issues.csv",
                pages_dir / "badges" / "quality_gate.svg",
                pages_dir / "badges" / "reliability.svg",
                pages_dir / "badges" / "security.svg",
                pages_dir / "badges" / "maintainability.svg",
                pages_dir / "badges" / "coverage.svg",
                pages_dir / "badges" / "ncloc.svg",
                pages_dir / "badges" / "tech_debt.svg",
            ]
            for fpath in expected_files:
                self.assertTrue(fpath.is_file(), f"Missing output file: {fpath}")
                content = fpath.read_text(encoding="utf-8")
                self.assertNotIn("sonarqube.local.jav26122.net", content, f"Internal URL leaked in {fpath}")
                self.assertNotIn("sqa_top_secret", content, f"Token leaked in {fpath}")
                self.assertNotIn("\u2014", content, f"Em-dash found in {fpath}")

            html_text = (pages_dir / "index.html").read_text(encoding="utf-8")
            self.assertNotIn("<script>alert", html_text)
            self.assertIn("&lt;script&gt;alert(&#x27;xss&#x27;)&lt;/script&gt;", html_text)
            self.assertIn("https://github.com/jav76/hyperdu/blob/HEAD/hyperdu.Cli/Program.cs#L10", html_text)

            summary_text = summary_out.read_text(encoding="utf-8")
            self.assertIn("https://jav76.github.io/hyperdu/", summary_text)
            self.assertIn("\\| pipe line2", summary_text)

    def test_main_redacts_secrets_on_http_error(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            rt = tmp_path / "missing.txt"
            stderr_buf = io.StringIO()

            def failing_urlopen(req, timeout=30.0):
                raise urllib.error.URLError("cannot reach https://sonarqube.local.jav26122.net with sqa_secret")

            with mock.patch.object(se.urllib.request, "urlopen", side_effect=failing_urlopen):
                with mock.patch.object(sys, "stderr", stderr_buf):
                    rc = se.main(
                        [
                            "--host-url",
                            "https://sonarqube.local.jav26122.net",
                            "--token",
                            "sqa_secret",
                            "--project-key",
                            "proj",
                            "--report-task",
                            str(rt),
                        ]
                    )
            self.assertEqual(rc, 1)
            err = stderr_buf.getvalue()
            self.assertNotIn("sqa_secret", err)
            self.assertNotIn("sonarqube.local.jav26122.net", err)


if __name__ == "__main__":
    unittest.main()
