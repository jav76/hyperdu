#!/usr/bin/env python3
"""Export SonarQube CE analysis results to SARIF 2.1.0, GitHub Pages, and step summary."""

from __future__ import annotations

import argparse
import base64
from dataclasses import dataclass
from datetime import datetime, timezone
import html
import io
import json
import os
from pathlib import Path
import re
import sys
import time
from typing import Any, Callable
import urllib.error
import urllib.parse
import urllib.request
import zipfile


METRIC_KEYS = [
    "ncloc",
    "coverage",
    "duplicated_lines_density",
    "bugs",
    "vulnerabilities",
    "code_smells",
    "security_hotspots",
    "reliability_rating",
    "security_rating",
    "sqale_rating",
    "sqale_index",
    "software_quality_reliability_issues",
    "software_quality_security_issues",
    "software_quality_maintainability_issues",
    "software_quality_reliability_rating",
    "software_quality_security_rating",
    "software_quality_maintainability_rating",
    "software_quality_maintainability_remediation_effort",
    "alert_status",
]

LEGACY_SARIF_SEVERITIES = {"MAJOR", "CRITICAL", "BLOCKER"}
MQR_SARIF_SEVERITIES = {"MEDIUM", "HIGH", "BLOCKER"}

SEVERITY_RANK = {
    "INFO": 1,
    "LOW": 2,
    "MINOR": 2,
    "MEDIUM": 3,
    "MAJOR": 3,
    "HIGH": 4,
    "CRITICAL": 4,
    "BLOCKER": 5,
}

RATING_MAP = {
    "1": ("A", "#4c1"),
    "1.0": ("A", "#4c1"),
    "A": ("A", "#4c1"),
    "2": ("B", "#97ca00"),
    "2.0": ("B", "#97ca00"),
    "B": ("B", "#97ca00"),
    "3": ("C", "#dfb317"),
    "3.0": ("C", "#dfb317"),
    "C": ("C", "#dfb317"),
    "4": ("D", "#fe7d37"),
    "4.0": ("D", "#fe7d37"),
    "D": ("D", "#fe7d37"),
    "5": ("E", "#e05d44"),
    "5.0": ("E", "#e05d44"),
    "E": ("E", "#e05d44"),
}

RSPEC_LANG_MAP = {
    "csharpsquid": "csharp",
    "vbnet": "vbnet",
    "java": "java",
    "javascript": "javascript",
    "typescript": "typescript",
    "python": "python",
    "go": "go",
    "rust": "rust",
    "c": "c",
    "cpp": "cpp",
    "xml": "xml",
    "web": "html",
    "html": "html",
    "css": "css",
    "php": "php",
    "ruby": "ruby",
    "kotlin": "kotlin",
    "scala": "scala",
    "docker": "docker",
    "kubernetes": "kubernetes",
    "terraform": "terraform",
    "secrets": "secrets",
    "azureresourcemanager": "azureresourcemanager",
    "cloudformation": "cloudformation",
    "githubactions": "githubactions",
    "flex": "flex",
}

MAX_SONAR_SEARCH_RESULTS = 10_000
SEARCH_PAGE_SIZE = 500


@dataclass(frozen=True)
class ExportConfig:
    host_url: str
    token: str
    project_key: str
    ce_task_id: str | None
    sarif_out: Path
    pages_dir: Path
    summary_out: Path
    badge_token: str | None
    repo_url: str | None
    timeout: float


class SonarApiClient:
    """Minimal stdlib HTTP client for SonarQube Web API."""

    def __init__(
        self,
        host_url: str,
        token: str,
        request_timeout: float = 30.0,
        opener: Callable[..., Any] | None = None,
    ) -> None:
        self.host_url = host_url.rstrip("/")
        self.token = token
        self.request_timeout = request_timeout
        self._opener = opener or urllib.request.urlopen
        encoded = base64.b64encode(f"{token}:".encode("utf-8")).decode("ascii")
        self._auth_header = f"Basic {encoded}"

    def _build_url(self, path: str, params: dict[str, Any] | None = None) -> str:
        base = f"{self.host_url}/{path.lstrip('/')}"
        if not params:
            return base
        filtered = [(k, str(v)) for k, v in params.items() if v is not None and v != ""]
        if not filtered:
            return base
        return f"{base}?{urllib.parse.urlencode(filtered)}"

    def get_bytes(
        self,
        path: str,
        params: dict[str, Any] | None = None,
        *,
        authenticated: bool = True,
    ) -> bytes:
        url = self._build_url(path, params)
        headers = {"Accept": "application/json, image/svg+xml, */*"}
        if authenticated:
            headers["Authorization"] = self._auth_header
        req = urllib.request.Request(url, headers=headers, method="GET")
        try:
            with self._opener(req, timeout=self.request_timeout) as resp:
                return resp.read()
        except urllib.error.HTTPError as exc:
            body = ""
            try:
                body = exc.read().decode("utf-8", errors="replace")[:300]
            except Exception:
                body = ""
            safe_msg = sanitize_text(
                f"HTTP {exc.code} calling {path}: {body}",
                host_url=self.host_url,
                secrets=[self.token],
            )
            raise RuntimeError(safe_msg) from None
        except urllib.error.URLError as exc:
            safe_msg = sanitize_text(
                f"Network error calling {path}: {exc.reason}",
                host_url=self.host_url,
                secrets=[self.token],
            )
            raise RuntimeError(safe_msg) from None

    def get_json(
        self,
        path: str,
        params: dict[str, Any] | None = None,
        *,
        authenticated: bool = True,
    ) -> dict[str, Any]:
        raw = self.get_bytes(path, params, authenticated=authenticated)
        try:
            data = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise RuntimeError(f"Invalid JSON from {path}: {exc}") from None
        if not isinstance(data, dict):
            raise RuntimeError(f"Expected JSON object from {path}")
        return data


def sanitize_text(
    text: str,
    *,
    host_url: str | None = None,
    secrets: list[str] | None = None,
    replacement: str = "[redacted]",
) -> str:
    """Remove secret tokens and internal SonarQube host URLs from public text."""
    if not text:
        return text
    result = text
    for secret in secrets or []:
        if secret:
            result = result.replace(secret, "[redacted]")
    if host_url:
        normalized = host_url.rstrip("/")
        if normalized:
            result = result.replace(normalized, replacement)
            parsed = urllib.parse.urlparse(normalized)
            if parsed.netloc:
                result = result.replace(f"https://{parsed.netloc}", replacement)
                result = result.replace(f"http://{parsed.netloc}", replacement)
                result = result.replace(parsed.netloc, replacement)
    return result


def validate_public_repo_url(repo_url: str | None) -> str | None:
    if not repo_url:
        return None
    cleaned = repo_url.strip().rstrip("/")
    parsed = urllib.parse.urlparse(cleaned)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        return None
    return cleaned


def derive_gh_pages_url(repo_url: str | None) -> str | None:
    validated = validate_public_repo_url(repo_url)
    if not validated:
        return None
    parsed = urllib.parse.urlparse(validated)
    if parsed.netloc.lower() != "github.com":
        return None
    parts = [p for p in parsed.path.split("/") if p]
    if len(parts) < 2:
        return None
    owner, repo = parts[0], parts[1].removesuffix(".git")
    return f"https://{owner}.github.io/{repo}/"


def parse_report_task(path: Path) -> dict[str, str]:
    """Parse SonarScanner report-task.txt key=value file."""
    data: dict[str, str] = {}
    if not path.is_file():
        return data
    for raw_line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        data[key.strip()] = value.strip()
    return data


def poll_ce_task(
    client: SonarApiClient,
    ce_task_id: str,
    *,
    timeout: float = 180.0,
    poll_interval: float = 2.0,
    sleep_fn: Callable[[float], None] = time.sleep,
    monotonic_fn: Callable[[], float] = time.monotonic,
) -> dict[str, Any]:
    """Poll /api/ce/task until SUCCESS, or fail on FAILED/CANCELED/timeout."""
    deadline = monotonic_fn() + max(0.0, timeout)
    while True:
        resp = client.get_json("/api/ce/task", {"id": ce_task_id})
        task = resp.get("task")
        if not isinstance(task, dict):
            raise RuntimeError(f"Unexpected /api/ce/task response for task {ce_task_id}")
        status = str(task.get("status", "")).upper()
        if status == "SUCCESS":
            return task
        if status in {"FAILED", "CANCELED"}:
            err = task.get("errorMessage") or "no error message reported"
            raise RuntimeError(f"SonarQube CE task {ce_task_id} ended with status {status}: {err}")
        if status not in {"PENDING", "IN_PROGRESS"}:
            raise RuntimeError(f"Unexpected SonarQube CE task status '{status}' for {ce_task_id}")
        remaining = deadline - monotonic_fn()
        if remaining <= 0:
            raise TimeoutError(f"Timed out after {timeout}s waiting for SonarQube CE task {ce_task_id}")
        sleep_fn(min(poll_interval, remaining))


def fetch_paginated_items(
    client: SonarApiClient,
    path: str,
    base_params: dict[str, Any],
    items_key: str,
    *,
    page_size: int = SEARCH_PAGE_SIZE,
    max_results: int = MAX_SONAR_SEARCH_RESULTS,
) -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]], dict[str, dict[str, Any]], int]:
    """Fetch paginated /api/issues/search or /api/hotspots/search responses."""
    all_items: list[dict[str, Any]] = []
    components_by_key: dict[str, dict[str, Any]] = {}
    rules_by_key: dict[str, dict[str, Any]] = {}
    reported_total = 0
    page = 1

    while True:
        params = dict(base_params)
        params["ps"] = page_size
        params["p"] = page
        data = client.get_json(path, params)

        for comp in data.get("components") or []:
            if isinstance(comp, dict) and comp.get("key"):
                components_by_key[str(comp["key"])] = comp
        for rule in data.get("rules") or []:
            if isinstance(rule, dict) and rule.get("key"):
                rules_by_key[str(rule["key"])] = rule

        items = [it for it in (data.get(items_key) or []) if isinstance(it, dict)]
        all_items.extend(items)

        paging = data.get("paging") or {}
        if isinstance(paging, dict) and "total" in paging:
            reported_total = int(paging["total"])
        else:
            reported_total = len(all_items)

        if not items:
            break
        effective_cap = min(reported_total, max_results)
        if page * page_size >= effective_cap or len(all_items) >= max_results:
            break
        page += 1

    return all_items, components_by_key, rules_by_key, reported_total


def resolve_repo_relative_path(
    component_key: str,
    project_key: str,
    components_by_key: dict[str, dict[str, Any]],
) -> str:
    """Map a SonarQube component key to a clean repository-relative POSIX path."""
    comp = components_by_key.get(component_key)
    if comp:
        raw_path = comp.get("path") or ""
        if raw_path:
            cleaned = str(raw_path).replace("\\", "/").lstrip("/")
            if cleaned.startswith("./"):
                cleaned = cleaned[2:]
            if cleaned:
                return cleaned

    if component_key == project_key or not component_key:
        return "."

    prefix = f"{project_key}:"
    if component_key.startswith(prefix):
        rel = component_key[len(prefix) :]
    elif ":" in component_key:
        rel = component_key.rsplit(":", 1)[-1]
    else:
        rel = component_key

    rel = rel.replace("\\", "/").lstrip("/")
    if rel.startswith("./"):
        rel = rel[2:]
    return rel or "."


def rule_help_uri(rule_key: str) -> str | None:
    """Build a public rules.sonarsource.com URL when rule_key matches repo:Sxxxx."""
    if ":" not in rule_key:
        return None
    repo, rule_id = rule_key.split(":", 1)
    lang = RSPEC_LANG_MAP.get(repo.lower())
    match = re.fullmatch(r"S(\d+)", rule_id.strip())
    if not lang or not match:
        return None
    return f"https://rules.sonarsource.com/{lang}/RSPEC-{match.group(1)}/"


def issue_highest_rank(issue: dict[str, Any]) -> int:
    rank = SEVERITY_RANK.get(str(issue.get("severity", "")).upper(), 0)
    for impact in issue.get("impacts") or []:
        if isinstance(impact, dict):
            imp_rank = SEVERITY_RANK.get(str(impact.get("severity", "")).upper(), 0)
            rank = max(rank, imp_rank)
    return rank


def should_include_issue_in_sarif(issue: dict[str, Any]) -> bool:
    """Include issues that are Major/MEDIUM or higher (Reliability, Security, Maintainability)."""
    if str(issue.get("severity", "")).upper() in LEGACY_SARIF_SEVERITIES:
        return True
    for impact in issue.get("impacts") or []:
        if isinstance(impact, dict):
            if str(impact.get("severity", "")).upper() in MQR_SARIF_SEVERITIES:
                return True
    return False


def rank_to_sarif_level(rank: int) -> str:
    if rank >= 4:
        return "error"
    if rank == 3:
        return "warning"
    return "note"


def build_region(item: dict[str, Any]) -> dict[str, int] | None:
    text_range = item.get("textRange")
    if isinstance(text_range, dict) and text_range.get("startLine"):
        start_line = max(1, int(text_range["startLine"]))
        end_line = max(start_line, int(text_range.get("endLine") or start_line))
        region: dict[str, int] = {"startLine": start_line, "endLine": end_line}
        start_offset = text_range.get("startOffset")
        end_offset = text_range.get("endOffset")
        if isinstance(start_offset, int) and start_offset >= 0:
            region["startColumn"] = start_offset + 1
            if isinstance(end_offset, int) and end_offset > start_offset:
                region["endColumn"] = end_offset + 1
        return region

    line = item.get("line")
    if isinstance(line, int) and line > 0:
        return {"startLine": line}
    return None


def build_sarif(
    *,
    project_key: str,
    issues: list[dict[str, Any]],
    hotspots: list[dict[str, Any]],
    components_by_key: dict[str, dict[str, Any]],
    rules_by_key: dict[str, dict[str, Any]],
    repo_url: str | None = None,
    host_url: str | None = None,
) -> dict[str, Any]:
    """Convert filtered SonarQube issues and open hotspots to SARIF 2.1.0."""
    public_repo = validate_public_repo_url(repo_url)
    driver_info_uri = public_repo or "https://www.sonarsource.com/products/sonarqube/"

    rule_descriptors: dict[str, dict[str, Any]] = {}
    results: list[dict[str, Any]] = []

    def ensure_rule(rule_key: str, fallback_name: str | None = None) -> None:
        if rule_key in rule_descriptors:
            return
        meta = rules_by_key.get(rule_key, {})
        name = str(meta.get("name") or fallback_name or rule_key)
        desc: dict[str, Any] = {
            "id": rule_key,
            "name": name,
            "shortDescription": {"text": sanitize_text(name, host_url=host_url)},
        }
        help_url = rule_help_uri(rule_key)
        if help_url:
            desc["helpUri"] = help_url
        rule_descriptors[rule_key] = desc

    for issue in issues:
        if not should_include_issue_in_sarif(issue):
            continue
        rule_key = str(issue.get("rule") or "sonarqube:unknown")
        ensure_rule(rule_key)
        rel_path = resolve_repo_relative_path(
            str(issue.get("component") or ""),
            project_key,
            components_by_key,
        )
        physical_location: dict[str, Any] = {
            "artifactLocation": {
                "uri": rel_path,
                "uriBaseId": "%SRCROOT%",
            }
        }
        region = build_region(issue)
        if region:
            physical_location["region"] = region

        rank = issue_highest_rank(issue)
        message_text = sanitize_text(
            str(issue.get("message") or rule_key),
            host_url=host_url,
        )
        impacts = [
            f"{imp.get('softwareQuality')}:{imp.get('severity')}"
            for imp in (issue.get("impacts") or [])
            if isinstance(imp, dict)
        ]
        results.append(
            {
                "ruleId": rule_key,
                "level": rank_to_sarif_level(rank),
                "message": {"text": message_text},
                "locations": [{"physicalLocation": physical_location}],
                "partialFingerprints": {
                    "sonarIssueKey": str(issue.get("key") or ""),
                },
                "properties": {
                    "kind": "issue",
                    "legacySeverity": str(issue.get("severity") or ""),
                    "impacts": impacts,
                    "issueStatus": str(issue.get("issueStatus") or issue.get("status") or ""),
                },
            }
        )

    for hotspot in hotspots:
        rule_key = str(hotspot.get("ruleKey") or "sonarqube:security-hotspot")
        category = str(hotspot.get("securityCategory") or "security-hotspot")
        ensure_rule(rule_key, fallback_name=f"Security Hotspot ({category})")
        rel_path = resolve_repo_relative_path(
            str(hotspot.get("component") or ""),
            project_key,
            components_by_key,
        )
        physical_location = {
            "artifactLocation": {
                "uri": rel_path,
                "uriBaseId": "%SRCROOT%",
            }
        }
        region = build_region(hotspot)
        if region:
            physical_location["region"] = region

        prob = str(hotspot.get("vulnerabilityProbability") or "MEDIUM").upper()
        rank = SEVERITY_RANK.get(prob, 3)
        message_text = sanitize_text(
            str(hotspot.get("message") or f"Security Hotspot ({category})"),
            host_url=host_url,
        )
        results.append(
            {
                "ruleId": rule_key,
                "level": rank_to_sarif_level(rank),
                "message": {"text": f"[Security Hotspot - {prob}] {message_text}"},
                "locations": [{"physicalLocation": physical_location}],
                "partialFingerprints": {
                    "sonarIssueKey": str(hotspot.get("key") or ""),
                },
                "properties": {
                    "kind": "security_hotspot",
                    "securityCategory": category,
                    "vulnerabilityProbability": prob,
                    "status": str(hotspot.get("status") or "TO_REVIEW"),
                },
            }
        )

    return {
        "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
        "version": "2.1.0",
        "runs": [
            {
                "tool": {
                    "driver": {
                        "name": "SonarQube",
                        "informationUri": driver_info_uri,
                        "rules": [rule_descriptors[k] for k in sorted(rule_descriptors)],
                    }
                },
                "results": results,
            }
        ],
    }


def extract_measures_map(measures_resp: dict[str, Any]) -> dict[str, str]:
    """Flatten /api/measures/component response into {metric_key: value_str}."""
    component = measures_resp.get("component") or {}
    out: dict[str, str] = {}
    for item in component.get("measures") or []:
        if not isinstance(item, dict) or not item.get("metric"):
            continue
        metric = str(item["metric"])
        if "value" in item and item["value"] is not None:
            out[metric] = str(item["value"])
        elif isinstance(item.get("period"), dict) and item["period"].get("value") is not None:
            out[metric] = str(item["period"]["value"])
    return out


def format_rating(raw: str | None) -> tuple[str, str]:
    if not raw:
        return ("N/A", "#9f9f9f")
    return RATING_MAP.get(str(raw).strip().upper(), (str(raw), "#9f9f9f"))


def format_ncloc(raw: str | None) -> str:
    if not raw:
        return "N/A"
    try:
        val = int(float(raw))
    except ValueError:
        return str(raw)
    if val >= 1_000_000:
        return f"{val / 1_000_000:.1f}M".replace(".0M", "M")
    if val >= 1_000:
        return f"{val / 1_000:.1f}k".replace(".0k", "k")
    return str(val)


def format_effort_minutes(raw: str | None) -> str:
    if not raw:
        return "0min"
    try:
        minutes = int(float(raw))
    except ValueError:
        return str(raw)
    if minutes <= 0:
        return "0min"
    days, rem = divmod(minutes, 8 * 60)
    hours, mins = divmod(rem, 60)
    parts: list[str] = []
    if days:
        parts.append(f"{days}d")
    if hours:
        parts.append(f"{hours}h")
    if mins or not parts:
        parts.append(f"{mins}min")
    return "".join(parts[:2])


def format_coverage(raw: str | None) -> tuple[str, str]:
    if raw is None or raw == "":
        return ("N/A", "#9f9f9f")
    try:
        pct = float(raw)
    except ValueError:
        return (str(raw), "#9f9f9f")
    if pct >= 80.0:
        color = "#4c1"
    elif pct >= 50.0:
        color = "#dfb317"
    else:
        color = "#e05d44"
    text = f"{pct:.1f}%".replace(".0%", "%")
    return (text, color)


def render_static_badge_svg(label: str, value: str, color: str) -> str:
    """Render a clean Shields-style flat SVG badge with proper XML escaping."""
    safe_label = html.escape(label, quote=True)
    safe_value = html.escape(value, quote=True)
    safe_color = color if re.fullmatch(r"#[0-9a-fA-F]{3,8}", color) else "#555"

    label_width = max(44, len(label) * 7 + 16)
    value_width = max(32, len(value) * 7 + 16)
    total_width = label_width + value_width
    label_x = label_width / 2.0
    value_x = label_width + value_width / 2.0

    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{total_width}" height="20" '
        f'role="img" aria-label="{safe_label}: {safe_value}">\n'
        f"  <title>{safe_label}: {safe_value}</title>\n"
        f'  <linearGradient id="s" x2="0" y2="100%">\n'
        f'    <stop offset="0" stop-color="#bbb" stop-opacity=".1"/>\n'
        f'    <stop offset="1" stop-opacity=".1"/>\n'
        f"  </linearGradient>\n"
        f'  <clipPath id="r">\n'
        f'    <rect width="{total_width}" height="20" rx="3" fill="#fff"/>\n'
        f"  </clipPath>\n"
        f'  <g clip-path="url(#r)">\n'
        f'    <rect width="{label_width}" height="20" fill="#555"/>\n'
        f'    <rect x="{label_width}" width="{value_width}" height="20" fill="{safe_color}"/>\n'
        f'    <rect width="{total_width}" height="20" fill="url(#s)"/>\n'
        f"  </g>\n"
        f'  <g fill="#fff" text-anchor="middle" '
        f'font-family="Verdana,Geneva,DejaVu Sans,sans-serif" '
        f'text-rendering="geometricPrecision" font-size="11">\n'
        f'    <text x="{label_x:.1f}" y="15" fill="#010101" fill-opacity=".3">{safe_label}</text>\n'
        f'    <text x="{label_x:.1f}" y="14" fill="#fff">{safe_label}</text>\n'
        f'    <text x="{value_x:.1f}" y="15" fill="#010101" fill-opacity=".3">{safe_value}</text>\n'
        f'    <text x="{value_x:.1f}" y="14" fill="#fff">{safe_value}</text>\n'
        f"  </g>\n"
        f"</svg>\n"
    )


def generate_badges(
    client: SonarApiClient | None,
    project_key: str,
    measures: dict[str, str],
    gate_status: str,
    badge_token: str | None,
) -> dict[str, str]:
    """Return {filename: svg_text} for the 7 public badges."""
    gate_upper = (gate_status or measures.get("alert_status") or "NONE").upper()
    if gate_upper == "OK":
        gate_label, gate_color = ("passed", "#4c1")
    elif gate_upper == "WARN":
        gate_label, gate_color = ("warning", "#dfb317")
    elif gate_upper == "ERROR":
        gate_label, gate_color = ("failed", "#e05d44")
    else:
        gate_label, gate_color = (gate_upper.lower(), "#9f9f9f")

    rel_grade, rel_color = format_rating(
        measures.get("software_quality_reliability_rating") or measures.get("reliability_rating")
    )
    sec_grade, sec_color = format_rating(
        measures.get("software_quality_security_rating") or measures.get("security_rating")
    )
    maint_grade, maint_color = format_rating(
        measures.get("software_quality_maintainability_rating") or measures.get("sqale_rating")
    )
    cov_text, cov_color = format_coverage(measures.get("coverage"))
    ncloc_text = format_ncloc(measures.get("ncloc"))
    debt_text = format_effort_minutes(
        measures.get("software_quality_maintainability_remediation_effort")
        or measures.get("sqale_index")
    )

    specs: list[tuple[str, str, list[str], tuple[str, str, str]]] = [
        ("quality_gate.svg", "quality_gate", [], ("quality gate", gate_label, gate_color)),
        (
            "reliability.svg",
            "measure",
            ["software_quality_reliability_rating", "reliability_rating"],
            ("reliability", rel_grade, rel_color),
        ),
        (
            "security.svg",
            "measure",
            ["software_quality_security_rating", "security_rating"],
            ("security", sec_grade, sec_color),
        ),
        (
            "maintainability.svg",
            "measure",
            ["software_quality_maintainability_rating", "sqale_rating"],
            ("maintainability", maint_grade, maint_color),
        ),
        ("coverage.svg", "measure", ["coverage"], ("coverage", cov_text, cov_color)),
        ("ncloc.svg", "measure", ["ncloc"], ("lines of code", ncloc_text, "#007ec6")),
        (
            "tech_debt.svg",
            "measure",
            ["software_quality_maintainability_remediation_effort", "sqale_index"],
            ("tech debt", debt_text, "#007ec6"),
        ),
    ]

    badges: dict[str, str] = {}
    for filename, kind, metrics_to_try, fallback in specs:
        svg_text: str | None = None
        if client and badge_token:
            if kind == "quality_gate":
                try:
                    raw = client.get_bytes(
                        "/api/project_badges/quality_gate",
                        {"project": project_key, "token": badge_token},
                        authenticated=False,
                    ).decode("utf-8", errors="replace")
                    if "<svg" in raw.lower():
                        svg_text = raw
                except Exception:
                    svg_text = None
            else:
                for metric in metrics_to_try:
                    try:
                        raw = client.get_bytes(
                            "/api/project_badges/measure",
                            {"project": project_key, "metric": metric, "token": badge_token},
                            authenticated=False,
                        ).decode("utf-8", errors="replace")
                        if "<svg" in raw.lower() and "invalid" not in raw.lower():
                            svg_text = raw
                            break
                    except Exception:
                        continue
        if not svg_text:
            svg_text = render_static_badge_svg(*fallback)
        badges[filename] = svg_text
    return badges


def fetch_optional_cnes_reports(
    client: SonarApiClient,
    project_key: str,
    *,
    host_url: str,
    token: str,
    repo_url: str | None,
) -> dict[str, str]:
    """Optionally download Markdown and CSV reports from CNES Report plugin."""
    try:
        raw_zip = client.get_bytes(
            "/api/cnesreport/report",
            {
                "key": project_key,
                "token": token,
                "enableDocx": "false",
                "enableXlsx": "false",
                "enableConf": "false",
                "enableMd": "true",
                "enableCsv": "true",
            },
        )
    except Exception:
        return {}

    extracted: dict[str, str] = {}
    replacement = validate_public_repo_url(repo_url) or "[internal-sonar-redacted]"
    try:
        with zipfile.ZipFile(io.BytesIO(raw_zip)) as zf:
            for info in zf.infolist():
                if info.is_dir():
                    continue
                lower = info.filename.lower()
                if lower.endswith(".md") and "cnes-report.md" not in extracted:
                    content = zf.read(info).decode("utf-8", errors="replace")
                    extracted["cnes-report.md"] = sanitize_text(
                        content,
                        host_url=host_url,
                        secrets=[token],
                        replacement=replacement,
                    )
                elif lower.endswith(".csv") and "cnes-issues.csv" not in extracted:
                    content = zf.read(info).decode("utf-8", errors="replace")
                    extracted["cnes-issues.csv"] = sanitize_text(
                        content,
                        host_url=host_url,
                        secrets=[token],
                        replacement=replacement,
                    )
    except zipfile.BadZipFile:
        return {}
    return extracted


def summarize_issues(
    issues: list[dict[str, Any]],
    hotspots: list[dict[str, Any]],
    sarif_count: int,
) -> dict[str, Any]:
    by_quality: dict[str, int] = {
        "RELIABILITY": 0,
        "SECURITY": 0,
        "MAINTAINABILITY": 0,
    }
    by_impact_severity: dict[str, int] = {"BLOCKER": 0, "HIGH": 0, "MEDIUM": 0, "LOW": 0, "INFO": 0}
    by_legacy_severity: dict[str, int] = {"BLOCKER": 0, "CRITICAL": 0, "MAJOR": 0, "MINOR": 0, "INFO": 0}

    for issue in issues:
        leg = str(issue.get("severity") or "").upper()
        if leg:
            by_legacy_severity[leg] = by_legacy_severity.get(leg, 0) + 1
        impacts = issue.get("impacts") or []
        for imp in impacts:
            if not isinstance(imp, dict):
                continue
            sq = str(imp.get("softwareQuality") or "").upper()
            sev = str(imp.get("severity") or "").upper()
            if sq:
                by_quality[sq] = by_quality.get(sq, 0) + 1
            if sev:
                by_impact_severity[sev] = by_impact_severity.get(sev, 0) + 1

    return {
        "totalOpenIssues": len(issues),
        "openHotspots": len(hotspots),
        "sarifResults": sarif_count,
        "bySoftwareQuality": by_quality,
        "byImpactSeverity": by_impact_severity,
        "byLegacySeverity": by_legacy_severity,
    }


def build_public_metrics_payload(
    *,
    project_key: str,
    generated_at: str,
    repo_url: str | None,
    quality_gate: dict[str, Any],
    measures: dict[str, str],
    issue_summary: dict[str, Any],
    issues_total_reported: int,
    hotspots_total_reported: int,
) -> dict[str, Any]:
    project_status = quality_gate.get("projectStatus") or {}
    conditions = []
    for cond in project_status.get("conditions") or []:
        if isinstance(cond, dict):
            conditions.append(
                {
                    "status": str(cond.get("status") or ""),
                    "metricKey": str(cond.get("metricKey") or ""),
                    "comparator": str(cond.get("comparator") or ""),
                    "errorThreshold": cond.get("errorThreshold"),
                    "actualValue": cond.get("actualValue"),
                }
            )
    payload: dict[str, Any] = {
        "projectKey": project_key,
        "generatedAt": generated_at,
        "qualityGate": {
            "status": str(project_status.get("status") or measures.get("alert_status") or "NONE"),
            "conditions": conditions,
        },
        "measures": measures,
        "issueSummary": issue_summary,
        "totalsReported": {
            "issues": issues_total_reported,
            "hotspots": hotspots_total_reported,
        },
    }
    public_repo = validate_public_repo_url(repo_url)
    if public_repo:
        payload["repoUrl"] = public_repo
    return payload


def md_cell(value: Any) -> str:
    return str(value).replace("|", "\\|").replace("\r", " ").replace("\n", " ").strip()


def build_markdown_summary(
    metrics_payload: dict[str, Any],
    issues: list[dict[str, Any]],
    hotspots: list[dict[str, Any]],
    components_by_key: dict[str, dict[str, Any]],
    *,
    host_url: str | None = None,
    include_top_findings: bool = True,
    cnes_files: list[str] | None = None,
) -> str:
    project_key = metrics_payload["projectKey"]
    generated_at = metrics_payload["generatedAt"]
    repo_url = metrics_payload.get("repoUrl")
    gate = metrics_payload["qualityGate"]
    measures = metrics_payload["measures"]
    summary = metrics_payload["issueSummary"]

    rel_grade, _ = format_rating(
        measures.get("software_quality_reliability_rating") or measures.get("reliability_rating")
    )
    sec_grade, _ = format_rating(
        measures.get("software_quality_security_rating") or measures.get("security_rating")
    )
    maint_grade, _ = format_rating(
        measures.get("software_quality_maintainability_rating") or measures.get("sqale_rating")
    )
    cov_text, _ = format_coverage(measures.get("coverage"))
    dup_raw = measures.get("duplicated_lines_density")
    dup_text = f"{float(dup_raw):.1f}%" if dup_raw not in (None, "") else "N/A"
    ncloc_text = format_ncloc(measures.get("ncloc"))
    debt_text = format_effort_minutes(
        measures.get("software_quality_maintainability_remediation_effort")
        or measures.get("sqale_index")
    )

    lines: list[str] = [
        "## SonarQube Quality Report",
        "",
        f"- **Project**: `{md_cell(project_key)}`",
        f"- **Quality Gate**: **{md_cell(gate['status'])}**",
        f"- **Generated (UTC)**: `{generated_at}`",
    ]
    if repo_url:
        lines.append(f"- **Repository**: {repo_url}")
    gh_pages = derive_gh_pages_url(repo_url)
    if gh_pages:
        lines.append(f"- **Public Dashboard**: {gh_pages}")

    lines.extend(
        [
            "",
            "| Metric | Value |",
            "| :--- | :--- |",
            f"| Quality Gate | {md_cell(gate['status'])} |",
            f"| Reliability Rating / Issues | {rel_grade} ({measures.get('software_quality_reliability_issues', summary['bySoftwareQuality'].get('RELIABILITY', 0))}) |",
            f"| Security Rating / Issues | {sec_grade} ({measures.get('software_quality_security_issues', summary['bySoftwareQuality'].get('SECURITY', 0))}) |",
            f"| Maintainability Rating / Issues | {maint_grade} ({measures.get('software_quality_maintainability_issues', summary['bySoftwareQuality'].get('MAINTAINABILITY', 0))}) |",
            f"| Coverage | {cov_text} |",
            f"| Duplicated Lines | {dup_text} |",
            f"| Lines of Code (NCLOC) | {ncloc_text} |",
            f"| Technical Debt | {debt_text} |",
            f"| Open Issues (total) | {summary['totalOpenIssues']} |",
            f"| Open Security Hotspots | {summary['openHotspots']} |",
            f"| SARIF Exported Findings | {summary['sarifResults']} |",
        ]
    )

    conditions = gate.get("conditions") or []
    if conditions:
        lines.extend(
            [
                "",
                "### Quality Gate Conditions",
                "",
                "| Metric | Status | Actual | Threshold |",
                "| :--- | :--- | :--- | :--- |",
            ]
        )
        for cond in conditions:
            thresh = cond.get("errorThreshold")
            comp = cond.get("comparator") or ""
            thresh_str = f"{comp} {thresh}".strip() if thresh is not None else "-"
            lines.append(
                f"| `{md_cell(cond.get('metricKey'))}` | {md_cell(cond.get('status'))} "
                f"| {md_cell(cond.get('actualValue', '-'))} | {md_cell(thresh_str)} |"
            )

    if include_top_findings:
        ranked_issues = sorted(
            [it for it in issues if should_include_issue_in_sarif(it)],
            key=lambda it: (-issue_highest_rank(it), str(it.get("rule") or "")),
        )[:25]
        if ranked_issues or hotspots:
            lines.extend(
                [
                    "",
                    "### Top Findings (Major / MEDIUM+ and Open Hotspots)",
                    "",
                    "| Kind | Severity | Rule | Location | Message |",
                    "| :--- | :--- | :--- | :--- | :--- |",
                ]
            )
            for issue in ranked_issues:
                rel_path = resolve_repo_relative_path(
                    str(issue.get("component") or ""),
                    project_key,
                    components_by_key,
                )
                line_no = issue.get("line") or (issue.get("textRange") or {}).get("startLine")
                loc = f"{rel_path}:{line_no}" if line_no else rel_path
                sev = str(issue.get("severity") or "")
                imp_sevs = [
                    str(i.get("severity"))
                    for i in (issue.get("impacts") or [])
                    if isinstance(i, dict) and i.get("severity")
                ]
                sev_label = "/".join([s for s in [sev, *imp_sevs] if s]) or "MEDIUM"
                msg = sanitize_text(str(issue.get("message") or ""), host_url=host_url)
                lines.append(
                    f"| Issue | {md_cell(sev_label)} | `{md_cell(issue.get('rule'))}` "
                    f"| `{md_cell(loc)}` | {md_cell(msg)} |"
                )
            for hs in hotspots[:10]:
                rel_path = resolve_repo_relative_path(
                    str(hs.get("component") or ""),
                    project_key,
                    components_by_key,
                )
                line_no = hs.get("line")
                loc = f"{rel_path}:{line_no}" if line_no else rel_path
                prob = str(hs.get("vulnerabilityProbability") or "MEDIUM")
                msg = sanitize_text(str(hs.get("message") or ""), host_url=host_url)
                lines.append(
                    f"| Hotspot | {md_cell(prob)} | `{md_cell(hs.get('ruleKey'))}` "
                    f"| `{md_cell(loc)}` | {md_cell(msg)} |"
                )

    if cnes_files:
        lines.extend(["", "### Additional Exports", ""])
        for fname in sorted(cnes_files):
            lines.append(f"- [{md_cell(fname)}](./{md_cell(fname)})")

    lines.append("")
    return "\n".join(lines)


def build_html_report(
    metrics_payload: dict[str, Any],
    issues: list[dict[str, Any]],
    hotspots: list[dict[str, Any]],
    components_by_key: dict[str, dict[str, Any]],
    *,
    host_url: str | None = None,
    cnes_files: list[str] | None = None,
) -> str:
    """Render a self-contained, HTML-escaped static report for GitHub Pages."""
    project_key = html.escape(str(metrics_payload["projectKey"]), quote=True)
    generated_at = html.escape(str(metrics_payload["generatedAt"]), quote=True)
    repo_url = validate_public_repo_url(metrics_payload.get("repoUrl"))
    gate = metrics_payload["qualityGate"]
    gate_status = str(gate["status"]).upper()
    gate_badge_class = "ok" if gate_status == "OK" else ("warn" if gate_status == "WARN" else "err")
    measures = metrics_payload["measures"]
    summary = metrics_payload["issueSummary"]

    rel_grade, _ = format_rating(
        measures.get("software_quality_reliability_rating") or measures.get("reliability_rating")
    )
    sec_grade, _ = format_rating(
        measures.get("software_quality_security_rating") or measures.get("security_rating")
    )
    maint_grade, _ = format_rating(
        measures.get("software_quality_maintainability_rating") or measures.get("sqale_rating")
    )
    cov_text, _ = format_coverage(measures.get("coverage"))
    ncloc_text = format_ncloc(measures.get("ncloc"))
    debt_text = format_effort_minutes(
        measures.get("software_quality_maintainability_remediation_effort")
        or measures.get("sqale_index")
    )

    badge_files = [
        ("Quality Gate", "badges/quality_gate.svg"),
        ("Reliability", "badges/reliability.svg"),
        ("Security", "badges/security.svg"),
        ("Maintainability", "badges/maintainability.svg"),
        ("Coverage", "badges/coverage.svg"),
        ("Lines of Code", "badges/ncloc.svg"),
        ("Technical Debt", "badges/tech_debt.svg"),
    ]
    badge_imgs = "\n".join(
        f'<img src="{html.escape(src, quote=True)}" alt="{html.escape(alt, quote=True)}">'
        for alt, src in badge_files
    )

    cond_rows = []
    for cond in gate.get("conditions") or []:
        c_status = html.escape(str(cond.get("status") or ""), quote=True)
        c_metric = html.escape(str(cond.get("metricKey") or ""), quote=True)
        c_actual = html.escape(str(cond.get("actualValue") or "-"), quote=True)
        thresh = cond.get("errorThreshold")
        comp = str(cond.get("comparator") or "")
        thresh_str = html.escape(f"{comp} {thresh}".strip() if thresh is not None else "-", quote=True)
        cond_rows.append(
            f"<tr><td><code>{c_metric}</code></td><td>{c_status}</td>"
            f"<td>{c_actual}</td><td>{thresh_str}</td></tr>"
        )
    conditions_html = (
        "<table><thead><tr><th>Metric</th><th>Status</th><th>Actual</th><th>Threshold</th></tr></thead>"
        f"<tbody>{''.join(cond_rows)}</tbody></table>"
        if cond_rows
        else "<p>No Quality Gate conditions reported.</p>"
    )

    ranked_issues = sorted(
        [it for it in issues if should_include_issue_in_sarif(it)],
        key=lambda it: (-issue_highest_rank(it), str(it.get("rule") or "")),
    )[:50]
    finding_rows = []
    for issue in ranked_issues:
        rel_path = resolve_repo_relative_path(
            str(issue.get("component") or ""),
            str(metrics_payload["projectKey"]),
            components_by_key,
        )
        line_no = issue.get("line") or (issue.get("textRange") or {}).get("startLine")
        loc_text = f"{rel_path}:{line_no}" if line_no else rel_path
        safe_loc = html.escape(loc_text, quote=True)
        if repo_url and rel_path != ".":
            encoded_path = "/".join(urllib.parse.quote(part) for part in rel_path.split("/"))
            anchor = f"#L{int(line_no)}" if line_no else ""
            href = html.escape(f"{repo_url}/blob/HEAD/{encoded_path}{anchor}", quote=True)
            loc_cell = f'<a href="{href}"><code>{safe_loc}</code></a>'
        else:
            loc_cell = f"<code>{safe_loc}</code>"

        rule_key = str(issue.get("rule") or "")
        safe_rule = html.escape(rule_key, quote=True)
        h_uri = rule_help_uri(rule_key)
        rule_cell = (
            f'<a href="{html.escape(h_uri, quote=True)}"><code>{safe_rule}</code></a>'
            if h_uri
            else f"<code>{safe_rule}</code>"
        )
        sev = str(issue.get("severity") or "")
        imp_sevs = [
            str(i.get("severity"))
            for i in (issue.get("impacts") or [])
            if isinstance(i, dict) and i.get("severity")
        ]
        sev_label = html.escape("/".join([s for s in [sev, *imp_sevs] if s]) or "MEDIUM", quote=True)
        msg = html.escape(
            sanitize_text(str(issue.get("message") or ""), host_url=host_url),
            quote=True,
        )
        finding_rows.append(
            f"<tr><td>Issue</td><td>{sev_label}</td><td>{rule_cell}</td>"
            f"<td>{loc_cell}</td><td>{msg}</td></tr>"
        )

    for hs in hotspots[:20]:
        rel_path = resolve_repo_relative_path(
            str(hs.get("component") or ""),
            str(metrics_payload["projectKey"]),
            components_by_key,
        )
        line_no = hs.get("line")
        loc_text = f"{rel_path}:{line_no}" if line_no else rel_path
        safe_loc = html.escape(loc_text, quote=True)
        rule_key = html.escape(str(hs.get("ruleKey") or ""), quote=True)
        prob = html.escape(str(hs.get("vulnerabilityProbability") or "MEDIUM"), quote=True)
        msg = html.escape(
            sanitize_text(str(hs.get("message") or ""), host_url=host_url),
            quote=True,
        )
        finding_rows.append(
            f"<tr><td>Hotspot</td><td>{prob}</td><td><code>{rule_key}</code></td>"
            f"<td><code>{safe_loc}</code></td><td>{msg}</td></tr>"
        )

    findings_html = (
        "<table><thead><tr><th>Kind</th><th>Severity</th><th>Rule</th><th>Location</th><th>Message</th></tr></thead>"
        f"<tbody>{''.join(finding_rows)}</tbody></table>"
        if finding_rows
        else "<p>No Major/MEDIUM+ issues or open security hotspots found.</p>"
    )

    repo_link_html = (
        f'<p>Repository: <a href="{html.escape(repo_url, quote=True)}">{html.escape(repo_url, quote=True)}</a></p>'
        if repo_url
        else ""
    )
    extra_links = [
        '<li><a href="metrics.json">metrics.json</a></li>',
        '<li><a href="report.md">report.md</a></li>',
    ]
    for fname in sorted(cnes_files or []):
        safe_f = html.escape(fname, quote=True)
        extra_links.append(f'<li><a href="{safe_f}">{safe_f}</a></li>')

    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>SonarQube Quality Report - {project_key}</title>
  <style>
    body {{ font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; margin: 2rem auto; max-width: 1080px; padding: 0 1rem; color: #1f2328; background: #ffffff; }}
    h1, h2 {{ border-bottom: 1px solid #d0d7de; padding-bottom: 0.3rem; }}
    .badges {{ display: flex; flex-wrap: wrap; gap: 0.5rem; margin: 1rem 0; }}
    .cards {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 0.75rem; margin: 1rem 0; }}
    .card {{ border: 1px solid #d0d7de; border-radius: 6px; padding: 0.75rem 1rem; background: #f6f8fa; }}
    .card .label {{ font-size: 0.8rem; color: #57606a; }}
    .card .val {{ font-size: 1.4rem; font-weight: 600; margin-top: 0.2rem; }}
    .pill {{ display: inline-block; padding: 0.15rem 0.55rem; border-radius: 999px; color: #fff; font-weight: 600; font-size: 0.9rem; }}
    .ok {{ background: #1a7f37; }}
    .warn {{ background: #9a6700; }}
    .err {{ background: #cf222e; }}
    table {{ border-collapse: collapse; width: 100%; margin: 1rem 0; font-size: 0.9rem; }}
    th, td {{ border: 1px solid #d0d7de; padding: 0.45rem 0.65rem; text-align: left; vertical-align: top; }}
    th {{ background: #f6f8fa; }}
    code {{ background: #f6f8fa; padding: 0.1rem 0.3rem; border-radius: 4px; font-size: 0.85em; }}
  </style>
</head>
<body>
  <h1>SonarQube Quality Report</h1>
  <p>Project: <code>{project_key}</code> | Generated (UTC): <code>{generated_at}</code> | Quality Gate: <span class="pill {gate_badge_class}">{html.escape(gate_status, quote=True)}</span></p>
  {repo_link_html}
  <div class="badges">{badge_imgs}</div>
  <div class="cards">
    <div class="card"><div class="label">Reliability</div><div class="val">{html.escape(rel_grade)}</div></div>
    <div class="card"><div class="label">Security</div><div class="val">{html.escape(sec_grade)}</div></div>
    <div class="card"><div class="label">Maintainability</div><div class="val">{html.escape(maint_grade)}</div></div>
    <div class="card"><div class="label">Coverage</div><div class="val">{html.escape(cov_text)}</div></div>
    <div class="card"><div class="label">Lines of Code</div><div class="val">{html.escape(ncloc_text)}</div></div>
    <div class="card"><div class="label">Tech Debt</div><div class="val">{html.escape(debt_text)}</div></div>
    <div class="card"><div class="label">Open Issues</div><div class="val">{int(summary['totalOpenIssues'])}</div></div>
    <div class="card"><div class="label">Open Hotspots</div><div class="val">{int(summary['openHotspots'])}</div></div>
  </div>
  <h2>Quality Gate Conditions</h2>
  {conditions_html}
  <h2>Top Findings (Major / MEDIUM+ and Open Hotspots)</h2>
  {findings_html}
  <h2>Artifacts</h2>
  <ul>{''.join(extra_links)}</ul>
</body>
</html>
"""


def run_export(
    config: ExportConfig,
    *,
    client: SonarApiClient | None = None,
    sleep_fn: Callable[[float], None] = time.sleep,
    monotonic_fn: Callable[[], float] = time.monotonic,
    now_fn: Callable[[], datetime] | None = None,
) -> dict[str, Any]:
    api = client or SonarApiClient(config.host_url, config.token)

    if config.ce_task_id:
        poll_ce_task(
            api,
            config.ce_task_id,
            timeout=config.timeout,
            sleep_fn=sleep_fn,
            monotonic_fn=monotonic_fn,
        )

    quality_gate = api.get_json(
        "/api/qualitygates/project_status",
        {"projectKey": config.project_key},
    )
    measures_resp = api.get_json(
        "/api/measures/component",
        {
            "component": config.project_key,
            "metricKeys": ",".join(METRIC_KEYS),
        },
    )
    measures = extract_measures_map(measures_resp)

    issues, issue_components, rules_by_key, issues_total = fetch_paginated_items(
        api,
        "/api/issues/search",
        {
            "componentKeys": config.project_key,
            "resolved": "false",
            "additionalFields": "rules",
        },
        "issues",
    )
    hotspots, hotspot_components, _, hotspots_total = fetch_paginated_items(
        api,
        "/api/hotspots/search",
        {
            "projectKey": config.project_key,
            "status": "TO_REVIEW",
        },
        "hotspots",
    )

    components_by_key: dict[str, dict[str, Any]] = {}
    components_by_key.update(hotspot_components)
    components_by_key.update(issue_components)

    sarif_doc = build_sarif(
        project_key=config.project_key,
        issues=issues,
        hotspots=hotspots,
        components_by_key=components_by_key,
        rules_by_key=rules_by_key,
        repo_url=config.repo_url,
        host_url=config.host_url,
    )
    sarif_results_count = len(sarif_doc["runs"][0]["results"])

    config.sarif_out.parent.mkdir(parents=True, exist_ok=True)
    config.sarif_out.write_text(json.dumps(sarif_doc, indent=2) + "\n", encoding="utf-8")

    gate_status = str((quality_gate.get("projectStatus") or {}).get("status") or measures.get("alert_status") or "NONE")
    badges = generate_badges(
        api,
        config.project_key,
        measures,
        gate_status,
        config.badge_token,
    )

    cnes_reports = fetch_optional_cnes_reports(
        api,
        config.project_key,
        host_url=config.host_url,
        token=config.token,
        repo_url=config.repo_url,
    )

    utc_now = (now_fn or (lambda: datetime.now(timezone.utc)))()
    generated_at = utc_now.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    issue_summary = summarize_issues(issues, hotspots, sarif_results_count)
    metrics_payload = build_public_metrics_payload(
        project_key=config.project_key,
        generated_at=generated_at,
        repo_url=config.repo_url,
        quality_gate=quality_gate,
        measures=measures,
        issue_summary=issue_summary,
        issues_total_reported=issues_total,
        hotspots_total_reported=hotspots_total,
    )

    pages_dir = config.pages_dir
    badges_dir = pages_dir / "badges"
    badges_dir.mkdir(parents=True, exist_ok=True)
    for badge_name, svg_content in badges.items():
        (badges_dir / badge_name).write_text(svg_content, encoding="utf-8")

    for fname, content in cnes_reports.items():
        (pages_dir / fname).write_text(content, encoding="utf-8")

    cnes_file_names = list(cnes_reports.keys())
    report_md = build_markdown_summary(
        metrics_payload,
        issues,
        hotspots,
        components_by_key,
        host_url=config.host_url,
        include_top_findings=True,
        cnes_files=cnes_file_names,
    )
    index_html = build_html_report(
        metrics_payload,
        issues,
        hotspots,
        components_by_key,
        host_url=config.host_url,
        cnes_files=cnes_file_names,
    )

    (pages_dir / "metrics.json").write_text(json.dumps(metrics_payload, indent=2) + "\n", encoding="utf-8")
    (pages_dir / "report.md").write_text(report_md, encoding="utf-8")
    (pages_dir / "index.html").write_text(index_html, encoding="utf-8")

    step_summary = build_markdown_summary(
        metrics_payload,
        issues,
        hotspots,
        components_by_key,
        host_url=config.host_url,
        include_top_findings=True,
        cnes_files=None,
    )
    config.summary_out.parent.mkdir(parents=True, exist_ok=True)
    config.summary_out.write_text(step_summary, encoding="utf-8")

    return metrics_payload


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Export SonarQube CE issues, hotspots, and metrics to SARIF 2.1.0, GitHub Pages, and Markdown summary."
    )
    parser.add_argument(
        "--host-url",
        default=os.environ.get("SONAR_HOST_URL", ""),
        help="SonarQube server base URL (env: SONAR_HOST_URL)",
    )
    parser.add_argument(
        "--token",
        default=os.environ.get("SONAR_TOKEN", ""),
        help="SonarQube authentication token (env: SONAR_TOKEN)",
    )
    parser.add_argument(
        "--project-key",
        default="",
        help="SonarQube project key (defaults to projectKey from --report-task)",
    )
    parser.add_argument(
        "--report-task",
        default=".sonarqube/out/.sonar/report-task.txt",
        help="Path to SonarScanner report-task.txt (default: .sonarqube/out/.sonar/report-task.txt)",
    )
    parser.add_argument(
        "--sarif-out",
        default="sonar-results.sarif",
        help="Output SARIF 2.1.0 file path (default: sonar-results.sarif)",
    )
    parser.add_argument(
        "--pages-dir",
        default="gh-pages-out",
        help="Output directory for static GitHub Pages bundle (default: gh-pages-out)",
    )
    parser.add_argument(
        "--summary-out",
        default="sonar-summary.md",
        help="Output Markdown summary path for GITHUB_STEP_SUMMARY (default: sonar-summary.md)",
    )
    parser.add_argument(
        "--badge-token",
        default=os.environ.get("SONAR_BADGE_TOKEN", ""),
        help="Optional SonarQube badge token (env: SONAR_BADGE_TOKEN)",
    )
    parser.add_argument(
        "--repo-url",
        default="",
        help="Public repository URL for links in public reports (e.g. https://github.com/jav76/hyperdu)",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=180.0,
        help="Timeout in seconds when polling SonarQube CE task (default: 180)",
    )
    return parser


def resolve_config(args: argparse.Namespace) -> ExportConfig:
    report_data = parse_report_task(Path(args.report_task))
    host_url = (args.host_url or report_data.get("serverUrl") or "").strip().rstrip("/")
    token = (args.token or "").strip()
    project_key = (args.project_key or report_data.get("projectKey") or "").strip()
    ce_task_id = report_data.get("ceTaskId") or None
    badge_token = (args.badge_token or "").strip() or None
    repo_url = validate_public_repo_url(args.repo_url)

    if not host_url:
        raise ValueError("Missing SonarQube host URL (--host-url or SONAR_HOST_URL).")
    if not token:
        raise ValueError("Missing SonarQube token (--token or SONAR_TOKEN).")
    if not project_key:
        raise ValueError(
            "Missing SonarQube project key (--project-key or projectKey in --report-task)."
        )

    return ExportConfig(
        host_url=host_url,
        token=token,
        project_key=project_key,
        ce_task_id=ce_task_id,
        sarif_out=Path(args.sarif_out),
        pages_dir=Path(args.pages_dir),
        summary_out=Path(args.summary_out),
        badge_token=badge_token,
        repo_url=repo_url,
        timeout=args.timeout,
    )


def main(argv: list[str] | None = None) -> int:
    parser = build_arg_parser()
    args = parser.parse_args(argv)
    try:
        config = resolve_config(args)
    except ValueError as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 2

    try:
        metrics_payload = run_export(config)
    except Exception as exc:
        safe_err = sanitize_text(
            str(exc),
            host_url=config.host_url,
            secrets=[config.token, config.badge_token or ""],
        )
        print(f"Error: {safe_err}", file=sys.stderr)
        return 1

    print(
        f"Exported SonarQube results for {config.project_key}: "
        f"gate={metrics_payload['qualityGate']['status']}, "
        f"issues={metrics_payload['issueSummary']['totalOpenIssues']}, "
        f"hotspots={metrics_payload['issueSummary']['openHotspots']}, "
        f"sarif={metrics_payload['issueSummary']['sarifResults']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
