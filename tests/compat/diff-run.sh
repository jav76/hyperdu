#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/PINNED_VERSION"

exec python3 - "$SCRIPT_DIR" "$PINNED_GNU_DU_VERSION" "$@" <<'PY'
import argparse
import concurrent.futures
import json
import os
import random
import re
import shutil
import subprocess
import sys
import tempfile
import time

script_dir = sys.argv[1]
pinned_version = sys.argv[2]
argv = sys.argv[3:]

parser = argparse.ArgumentParser(description="Differential du compatibility harness")
parser.add_argument("--fixtures", help="Existing fixture root directory")
parser.add_argument("--with-mounts", action="store_true", help="Pass --with-mounts when creating temp fixtures")
parser.add_argument("--ref-du", help="Path to reference GNU du binary")
parser.add_argument("--candidate-du", help="Path to candidate du binary (invoked as du)")
parser.add_argument("--hyperdu", help="Path to hyperdu binary (symlinked as du in a scratch dir)")
parser.add_argument("--random", type=int, default=20, help="Number of seeded random option vectors")
parser.add_argument("--seed", type=int, default=42, help="Random seed")
parser.add_argument("--timeout", type=float, default=2.0, help="Per-invocation timeout in seconds")
parser.add_argument("--jobs", type=int, default=4, help="Parallel worker count")
parser.add_argument("--json", dest="json_out", help="Write JSON report to path")
parser.add_argument("--markdown", dest="md_out", help="Write Markdown report to path")
parser.add_argument("--category", action="append", help="Filter vectors by category (repeatable)")
parser.add_argument("--baseline", action="store_true", help="Exit 0 regardless of score (Phase 0 baseline mode)")
parser.add_argument("--min-score", type=float, default=100.0, help="Minimum passing percentage (ignored with --baseline)")
args = parser.parse_args(argv)

def find_ref_du(override: str | None) -> str:
    if override:
        return override
    for name in ("gnudu", "gdu", "du"):
        found = shutil.which(name)
        if not found:
            continue
        try:
            res = subprocess.run([found, "--version"], capture_output=True, text=True, timeout=2)
            if "GNU coreutils" in res.stdout:
                return found
        except Exception:
            pass
    fallback = shutil.which("du")
    if not fallback:
        raise RuntimeError("Reference 'du' binary not found on PATH.")
    return fallback

def parse_du_version(du_path: str) -> str:
    try:
        res = subprocess.run([du_path, "--version"], capture_output=True, text=True, timeout=2)
        first = (res.stdout or res.stderr).splitlines()[0].strip() if (res.stdout or res.stderr) else "unknown"
        return first
    except Exception:
        return "unknown"

scratch_dirs: list[str] = []

def resolve_candidate_du() -> str:
    if args.candidate_du:
        return os.path.abspath(args.candidate_du)
    hyperdu_path = args.hyperdu
    if not hyperdu_path:
        repo_root = os.path.abspath(os.path.join(script_dir, "..", ".."))
        for cand in (
            os.path.join(repo_root, "artifacts", "compat-bin", "du"),
            os.path.join(repo_root, "artifacts", "bench-bin", "du"),
            os.path.join(repo_root, "artifacts", "compat-bin", "hyperdu"),
            os.path.join(repo_root, "artifacts", "bench-bin", "hyperdu"),
        ):
            if os.path.exists(cand):
                if os.path.basename(cand) == "du":
                    return cand
                hyperdu_path = cand
                break
    if not hyperdu_path:
        hyperdu_path = shutil.which("hyperdu")
    if not hyperdu_path:
        raise RuntimeError("Candidate binary not found. Pass --candidate-du or --hyperdu.")

    tmp_bin = tempfile.mkdtemp(prefix="hyperdu-compat-bin-")
    scratch_dirs.append(tmp_bin)
    du_link = os.path.join(tmp_bin, "du")
    os.symlink(os.path.abspath(hyperdu_path), du_link)
    return du_link

ref_du = find_ref_du(args.ref_du)
ref_version_line = parse_du_version(ref_du)
cand_du = resolve_candidate_du()

cleanup_fixtures_path: str | None = None
if args.fixtures:
    fixture_root = os.path.abspath(args.fixtures)
else:
    fixture_root = tempfile.mkdtemp(prefix="hyperdu-compat-fixtures-")
    cleanup_fixtures_path = fixture_root
    make_cmd = [os.path.join(script_dir, "make-fixtures.sh"), fixture_root]
    if args.with_mounts:
        make_cmd.append("--with-mounts")
    subprocess.run(make_cmd, check=True)

try:
    # Curated test vectors covering Section 7 verified GNU behaviors + Phases 1-3 option surface.
    # Tuple: (id, category, env_overrides, argv, stdin_bytes)
    vectors: list[tuple[str, str, dict[str, str], list[str], bytes | None]] = [
        ("s7-trailing-slash-dedupe", "section7", {}, ["d/", "d"], None),
        ("s7-cross-operand-hardlink", "section7", {}, ["d/a", "d"], None),
        ("s7-deref-all-order", "section7", {}, ["-aL", "d"], None),
        ("s7-exclude-operand", "section7", {}, ["--exclude=d", "d"], None),
        ("s7-abbrev-thr-valid", "section7", {}, ["--thr=1M", "d"], None),
        ("s7-abbrev-ambiguous", "section7", {}, ["--t", "d"], None),
        ("s7-conflict-s-a", "section7", {}, ["-s", "-a", "d"], None),
        ("s7-conflict-s-d1", "section7", {}, ["-s", "-d1", "d"], None),
        ("s7-conflict-s-d0", "section7", {}, ["-s", "-d0", "d"], None),
        ("s7-posixly-correct", "section7", {"POSIXLY_CORRECT": "1"}, ["d", "-s"], None),
        ("s7-si-units", "section7", {}, ["--si", "-s", "d"], None),
        ("s7-human-readable", "section7", {}, ["-h", "-s", "d"], None),
        ("s7-blocksize-grouping", "section7", {"DU_BLOCK_SIZE": "'1", "LC_ALL": "en_US.UTF-8"}, ["-s", "d"], None),
        ("s7-time-default", "section7", {"TZ": "UTC"}, ["--time", "-s", "d"], None),
        ("s7-time-full-iso", "section7", {"TZ": "UTC"}, ["--time", "--time-style=full-iso", "-s", "d"], None),
        ("s7-unreadable-d0", "section7", {}, ["--max-depth=0", "err1"], None),
        ("s7-extra-operand-quote", "section7", {"LC_ALL": "en_US.UTF-8"}, ["--files0-from=files0.list", "err1"], None),

        # Core Phase 1 MVP vectors
        ("p1-default-cwd", "phase1", {}, [], None),
        ("p1-file-operand", "phase1", {}, ["d/a/file1.bin"], None),
        ("p1-all-files", "phase1", {}, ["-a", "d"], None),
        ("p1-summarize", "phase1", {}, ["-s", "d"], None),
        ("p1-max-depth-1", "phase1", {}, ["-d", "1", "d"], None),
        ("p1-total", "phase1", {}, ["-c", "-s", "d/a", "d/c"], None),
        ("p1-null-delim", "phase1", {}, ["-0", "-a", "d"], None),
        ("p1-separate-dirs", "phase1", {}, ["-S", "d"], None),
        ("p1-apparent-size", "phase1", {}, ["--apparent-size", "-s", "sparse"], None),
        ("p1-bytes", "phase1", {}, ["-b", "sparse"], None),
        ("p1-kib-mib", "phase1", {}, ["-k", "d"], None),
        ("p1-mib", "phase1", {}, ["-m", "d"], None),
        ("p1-blocksize-env", "phase1", {"BLOCKSIZE": "1024", "BLOCK_SIZE": "512"}, ["-s", "d"], None),
        ("p1-double-dash", "phase1", {}, ["-s", "--", "d"], None),
        ("p1-bundled-flags", "phase1", {}, ["-shc", "d/a", "d/c"], None),

        # Phase 2 traversal & filtering vectors
        ("p2-one-file-system", "phase2", {}, ["-x", "-s", "mnt"], None),
        ("p2-deref-args-H", "phase2", {}, ["-H", "-s", "d/sl"], None),
        ("p2-deref-args-D", "phase2", {}, ["-D", "-a", "d/sl"], None),
        ("p2-no-deref-P", "phase2", {}, ["-P", "-a", "d"], None),
        ("p2-count-links", "phase2", {}, ["-l", "-s", "d"], None),
        ("p2-exclude-glob", "phase2", {}, ["--exclude=*.tmp", "-a", "d"], None),
        ("p2-exclude-from", "phase2", {}, ["-X", "exclude_patterns.txt", "-a", "d"], None),
        ("p2-threshold-pos", "phase2", {}, ["-t", "4096", "-a", "d"], None),
        ("p2-threshold-neg", "phase2", {}, ["-t", "-4096", "-a", "d"], None),
        ("p2-inodes", "phase2", {}, ["--inodes", "-a", "d"], None),
        ("p2-files0-from-file", "phase2", {}, ["--files0-from=files0.list"], None),
        ("p2-files0-from-stdin", "phase2", {}, ["--files0-from=-"], b"d/a\x00sparse\x00"),
        ("p2-non-utf8-names", "phase2", {}, ["-a", "names"], None),
        ("p2-large-dir-10k", "phase2", {}, ["-a", "large-dir"], None),
        ("p2-deep-beyond-pathmax", "phase2", {}, ["-s", "deep"], None),

        # Phase 3 time & locale vectors
        ("p3-time-atime-iso", "phase3", {"TZ": "America/New_York"}, ["--time=atime", "--time-style=iso", "-s", "d"], None),
        ("p3-time-ctime-long", "phase3", {"TZ": "UTC"}, ["--time=ctime", "--time-style=long-iso", "-s", "d"], None),
        ("p3-time-custom-fmt", "phase3", {"TZ": "UTC"}, ["--time", "--time-style=+%Y-%m-%d %H:%M:%S.%N", "-s", "d"], None),
    ]

    # Seeded deterministic random combinations from Section 6
    rng = random.Random(args.seed)
    scope_opts = [[], ["-a"], ["-s"], ["-d", "0"], ["-d", "1"]]
    unit_opts = [[], ["-h"], ["--si"], ["-k"], ["-m"], ["-b"], ["--apparent-size", "-B", "1K"], ["--inodes"]]
    link_opts = [[], ["-P"], ["-L"], ["-H"]]
    extra_opts = [[], ["-c"], ["-S"], ["-0"], ["-l"], ["-x"], ["--exclude=*.tmp"], ["--time"]]
    operand_sets = [["d"], ["d/", "d"], ["d/a", "d/c"], ["sparse"], ["names"], ["err1"]]

    for idx in range(args.random):
        combo: list[str] = []
        combo.extend(rng.choice(scope_opts))
        combo.extend(rng.choice(unit_opts))
        combo.extend(rng.choice(link_opts))
        for flag in rng.sample(extra_opts, k=2):
            combo.extend(flag)
        operands = rng.choice(operand_sets)
        vectors.append((f"rand-{idx:02d}", "random", {}, combo + operands, None))

    def normalize_stderr(raw: bytes, exe_path: str) -> bytes:
        exe_bytes = exe_path.encode("utf-8", errors="surrogateescape")
        base_bytes = os.path.basename(exe_path).encode("utf-8", errors="surrogateescape")
        lines = raw.splitlines(keepends=True)
        out_lines = []
        for line in lines:
            if line.startswith(exe_bytes + b":"):
                line = b"du:" + line[len(exe_bytes) + 1 :]
            elif base_bytes != b"du" and line.startswith(base_bytes + b":"):
                line = b"du:" + line[len(base_bytes) + 1 :]
            if exe_bytes != b"du":
                line = line.replace(b"Try '" + exe_bytes + b" --help'", b"Try 'du --help'")
            out_lines.append(line)
        return b"".join(out_lines)

    def run_one(exe: str, env_overrides: dict[str, str], vec_args: list[str], stdin_data: bytes | None):
        env = os.environ.copy()
        env.setdefault("LC_ALL", "C")
        env.setdefault("TZ", "UTC")
        env.update(env_overrides)
        try:
            proc = subprocess.run(
                [exe, *vec_args],
                cwd=fixture_root,
                env=env,
                input=stdin_data if stdin_data is not None else b"",
                capture_output=True,
                timeout=args.timeout,
            )
            return proc.returncode, proc.stdout, normalize_stderr(proc.stderr, exe), False
        except subprocess.TimeoutExpired as exc:
            out = exc.stdout or b""
            err = exc.stderr or b""
            return 124, out, err, True

    def evaluate_vector(item):
        vec_id, category, env_ov, vec_args, stdin_data = item
        ref_rc, ref_out, ref_err, ref_to = run_one(ref_du, env_ov, vec_args, stdin_data)
        cand_rc, cand_out, cand_err, cand_to = run_one(cand_du, env_ov, vec_args, stdin_data)

        stdout_match = (cand_out == ref_out)
        stderr_match = (cand_err == ref_err)
        exit_match = (cand_rc == ref_rc) and (not cand_to) and (not ref_to)
        passed = stdout_match and stderr_match and exit_match

        reasons = []
        if cand_to:
            reasons.append("candidate-timeout")
        if ref_to:
            reasons.append("reference-timeout")
        if not exit_match and not cand_to and not ref_to:
            reasons.append(f"exit({cand_rc}!={ref_rc})")
        if not stdout_match:
            reasons.append(f"stdout-diff({len(cand_out)}B!={len(ref_out)}B)")
        if not stderr_match:
            reasons.append(f"stderr-diff({len(cand_err)}B!={len(ref_err)}B)")

        return {
            "id": vec_id,
            "category": category,
            "args": vec_args,
            "env": env_ov,
            "passed": passed,
            "stdout_match": stdout_match,
            "stderr_match": stderr_match,
            "exit_match": exit_match,
            "timed_out": cand_to,
            "ref_exit": ref_rc,
            "cand_exit": cand_rc,
            "reasons": reasons,
        }

    if args.category:
        allowed_cats = set(args.category)
        vectors = [v for v in vectors if v[1] in allowed_cats]

    started = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        results = list(pool.map(evaluate_vector, vectors))
    elapsed_s = time.monotonic() - started

    total = len(results)
    passed_count = sum(1 for r in results if r["passed"])
    score_pct = (100.0 * passed_count / total) if total > 0 else 0.0

    by_category: dict[str, dict[str, int]] = {}
    for r in results:
        cat = by_category.setdefault(r["category"], {"total": 0, "passed": 0})
        cat["total"] += 1
        if r["passed"]:
            cat["passed"] += 1

    print(f"Reference du : {ref_du} ({ref_version_line}) [pinned: {pinned_version}]")
    print(f"Candidate du : {cand_du}")
    print(f"Compat Score : {passed_count}/{total} ({score_pct:.1f}%) in {elapsed_s:.2f}s")
    for cat, counts in by_category.items():
        cat_pct = 100.0 * counts["passed"] / counts["total"]
        print(f"  - {cat:10s}: {counts['passed']}/{counts['total']} ({cat_pct:.1f}%)")

    report_obj = {
        "pinned_gnu_du_version": pinned_version,
        "reference_du": ref_du,
        "reference_version": ref_version_line,
        "candidate_du": cand_du,
        "total": total,
        "passed": passed_count,
        "score_percent": round(score_pct, 2),
        "elapsed_seconds": round(elapsed_s, 3),
        "by_category": by_category,
        "results": results,
    }

    if args.json_out:
        os.makedirs(os.path.dirname(os.path.abspath(args.json_out)), exist_ok=True)
        with open(args.json_out, "w", encoding="utf-8") as f:
            json.dump(report_obj, f, indent=2)

    if args.md_out:
        os.makedirs(os.path.dirname(os.path.abspath(args.md_out)), exist_ok=True)
        lines = [
            "# du Compatibility Harness Report",
            "",
            f"- **Pinned Reference Version**: `{pinned_version}`",
            f"- **Observed Reference `du`**: `{ref_version_line}`",
            f"- **Candidate Binary**: `{cand_du}`",
            f"- **Compatibility Score**: **{passed_count} / {total} ({score_pct:.1f}%)**",
            "",
            "## Breakdown by Category",
            "",
            "| Category | Passed | Total | Score |",
            "| :--- | ---: | ---: | ---: |",
        ]
        for cat, counts in by_category.items():
            cat_pct = 100.0 * counts["passed"] / counts["total"]
            lines.append(f"| `{cat}` | {counts['passed']} | {counts['total']} | {cat_pct:.1f}% |")
        lines.extend([
            "",
            "## Vector Sample (Failures / First 20)",
            "",
            "| ID | Category | Args | Result | Reason |",
            "| :--- | :--- | :--- | :--- | :--- |",
        ])
        for r in results[:20]:
            status = "PASS" if r["passed"] else "FAIL"
            reason = ", ".join(r["reasons"]) if r["reasons"] else "exact match"
            arg_str = " ".join(r["args"])
            lines.append(f"| `{r['id']}` | `{r['category']}` | `{arg_str}` | {status} | {reason} |")

        with open(args.md_out, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")

    if not args.baseline and score_pct + 1e-9 < args.min_score:
        sys.exit(1)
    sys.exit(0)
finally:
    if cleanup_fixtures_path:
        subprocess.run([os.path.join(script_dir, "cleanup-fixtures.sh"), cleanup_fixtures_path], check=False)
    for d in scratch_dirs:
        shutil.rmtree(d, ignore_errors=True)
PY
