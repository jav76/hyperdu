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
parser.add_argument("--dialect", choices=["gnu", "bsd", "auto"], default="gnu", help="du dialect under test (gnu, bsd, or auto by OS)")
parser.add_argument("--fixtures", help="Existing fixture root directory")
parser.add_argument("--with-mounts", action="store_true", help="Pass --with-mounts when creating temp fixtures")
parser.add_argument("--ref-du", help="Path to reference du binary")
parser.add_argument("--candidate-du", help="Path to candidate du/gdu binary")
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
parser.add_argument("--threads-matrix", help="Comma-separated HYPERDU_THREADS modes to verify (e.g. 1,4,auto)")
args = parser.parse_args(argv)

active_dialect = args.dialect
if active_dialect == "auto":
    active_dialect = "bsd" if sys.platform == "darwin" else "gnu"

def find_ref_du(override: str | None) -> str:
    if override:
        return override
    if active_dialect == "bsd":
        if os.path.exists("/usr/bin/du"):
            return "/usr/bin/du"
        fallback = shutil.which("du")
        if not fallback:
            raise RuntimeError("Reference BSD 'du' binary not found.")
        return fallback

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
    if active_dialect == "bsd":
        return f"BSD du ({du_path})"
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
        ("p3-time-dst-full-iso-utc", "phase3", {"TZ": "UTC", "LC_ALL": "C"}, ["--time", "--time-style=full-iso", "-a", "times_dst"], None),
        ("p3-time-dst-full-iso-ny", "phase3", {"TZ": "America/New_York", "LC_ALL": "en_US.UTF-8"}, ["--time", "--time-style=full-iso", "-a", "times_dst"], None),
        ("p3-time-env-posix-c", "phase3", {"TZ": "America/New_York", "LC_ALL": "C", "TIME_STYLE": "posix-full-iso"}, ["--time", "-s", "times_dst"], None),
        ("p3-time-env-posix-en", "phase3", {"TZ": "America/New_York", "LC_ALL": "en_US.UTF-8", "TIME_STYLE": "posix-iso"}, ["--time", "-s", "times_dst"], None),
        ("p3-time-separate-total", "phase3", {"TZ": "UTC", "LC_ALL": "C"}, ["--time", "--time-style=full-iso", "-S", "-c", "times_dst"], None),
        ("p3-time-invalid-word-en", "phase3", {"LC_ALL": "en_US.UTF-8"}, ["--time=bogus", "d"], None),
        ("p3-time-invalid-style-c", "phase3", {"LC_ALL": "C"}, ["--time", "--time-style=bogus", "d"], None),
        ("p3-locale-grouping-c", "phase3", {"LC_ALL": "C"}, ["-B", "'1", "-s", "d"], None),
        ("p3-locale-grouping-lcnum", "phase3", {"LC_ALL": "", "LC_NUMERIC": "en_US.UTF-8", "LANG": "C"}, ["-B", "'1", "-s", "d"], None),
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

    if active_dialect == "bsd":
        vectors = [
            ("bsd-default-512", "bsd", {}, ["d"], None),
            ("bsd-default-cwd", "bsd", {}, [], None),
            ("bsd-file-operand", "bsd", {}, ["bsd_extra/tenk.bin"], None),
            ("bsd-all-files", "bsd", {}, ["-a", "bsd_extra"], None),
            ("bsd-summarize-s", "bsd", {}, ["-s", "d"], None),
            ("bsd-depth-0", "bsd", {}, ["-d", "0", "d"], None),
            ("bsd-depth-1", "bsd", {}, ["-d", "1", "d"], None),
            ("bsd-depth-2", "bsd", {}, ["-d", "2", "d"], None),
            ("bsd-apparent-A", "bsd", {}, ["-A", "sparse"], None),
            ("bsd-apparent-A-k", "bsd", {}, ["-A", "-k", "bsd_extra"], None),
            ("bsd-apparent-A-B1", "bsd", {}, ["-A", "-B", "1", "bsd_extra"], None),
            ("bsd-apparent-A-h", "bsd", {}, ["-A", "-a", "-h", "bsd_extra"], None),
            ("bsd-blocksize-B-1024", "bsd", {}, ["-B", "1024", "d"], None),
            ("bsd-blocksize-B-roundup", "bsd", {}, ["-B", "1", "d"], None),
            ("bsd-gib-g", "bsd", {}, ["-g", "d"], None),
            ("bsd-apparent-g", "bsd", {}, ["-A", "-g", "sparse"], None),
            ("bsd-kib-k", "bsd", {}, ["-k", "d"], None),
            ("bsd-mib-m", "bsd", {}, ["-m", "d"], None),
            ("bsd-human-h", "bsd", {}, ["-a", "-h", "bsd_extra"], None),
            ("bsd-human-si", "bsd", {}, ["-a", "--si", "bsd_extra"], None),
            ("bsd-total-c-h", "bsd", {}, ["-c", "-h", "d/a", "d/c", "bsd_extra"], None),
            ("bsd-ignore-I-ext", "bsd", {}, ["-I", "*.tmp", "-a", "bsd_extra"], None),
            ("bsd-ignore-I-dir", "bsd", {}, ["-I", "sub_*", "bsd_extra"], None),
            ("bsd-ignore-I-multi", "bsd", {}, ["-I", "*.tmp", "-I", "*.log", "-a", "bsd_extra"], None),
            ("bsd-nodump-n-all", "bsd", {}, ["-n", "-a", "bsd_extra"], None),
            ("bsd-nodump-n-sum", "bsd", {}, ["-n", "-s", "bsd_extra"], None),
            ("bsd-count-links-l", "bsd", {}, ["-l", "-s", "d"], None),
            ("bsd-dup-operands-dir", "bsd", {}, ["d", "d"], None),
            ("bsd-dup-operands-hl", "bsd", {}, ["bsd_extra/fourk.bin", "bsd_extra/fourk_hl.bin"], None),
            ("bsd-symlinks-H", "bsd", {}, ["-H", "-s", "d/sl"], None),
            ("bsd-symlinks-H-broken", "bsd", {}, ["-H", "bsd_extra/broken_sl"], None),
            ("bsd-symlinks-L", "bsd", {}, ["-L", "-a", "bsd_extra"], None),
            ("bsd-symlinks-P", "bsd", {}, ["-P", "-a", "d"], None),
            ("bsd-r-compat", "bsd", {}, ["-r", "err1"], None),
            ("bsd-xdev", "bsd", {}, ["-x", "-s", "mnt"], None),
            ("bsd-threshold-pos", "bsd", {}, ["-t", "1k", "bsd_extra"], None),
            ("bsd-threshold-A", "bsd", {}, ["-A", "-t", "5k", "bsd_extra"], None),
            ("bsd-env-blocksize-1k", "bsd", {"BLOCKSIZE": "1K"}, ["-s", "d"], None),
            ("bsd-env-blocksize-1m", "bsd", {"BLOCKSIZE": "1M"}, ["-s", "d"], None),
            ("bsd-env-blocksize-k-override", "bsd", {"BLOCKSIZE": "1M"}, ["-k", "-s", "d"], None),
            ("bsd-env-blocksize-min-clamp", "bsd", {"BLOCKSIZE": "256"}, ["-s", "d"], None),
            ("bsd-env-blocksize-bad", "bsd", {"BLOCKSIZE": "invalid"}, ["-s", "d"], None),
            ("bsd-getopt-stop-nonopt", "bsd", {}, ["d", "-s"], None),
            ("bsd-unreadable-err", "bsd", {}, ["err1"], None),
            ("bsd-missing-file", "bsd", {}, ["missing_file"], None),
            ("bsd-err-unknown-opt", "bsd", {}, ["-Z"], None),
            ("bsd-err-unknown-opt-posix", "bsd", {"POSIXLY_CORRECT": "1"}, ["-Z"], None),
            ("bsd-err-missing-arg", "bsd", {}, ["-d"], None),
            ("bsd-err-bad-depth", "bsd", {}, ["-d", "-1", "d"], None),
            ("bsd-err-bad-B", "bsd", {}, ["-B", "0", "d"], None),
            ("bsd-err-bad-thresh", "bsd", {}, ["-t", "0", "d"], None),
            ("bsd-err-conflict-sa", "bsd", {}, ["-s", "-a", "d"], None),
            ("bsd-err-conflict-sd0", "bsd", {}, ["-s", "-d", "0", "d"], None),
            ("bsd-err-conflict-ad1", "bsd", {}, ["-a", "-d", "1", "d"], None),
            ("bsd-err-long-version", "bsd", {}, ["--version"], None),
            ("bsd-err-long-help", "bsd", {}, ["--help"], None),
            ("bsd-large-dir-10k", "bsd", {}, ["-a", "large-dir"], None),
            ("bsd-deep", "bsd", {}, ["-s", "deep"], None),
        ]

        bsd_rng = random.Random(args.seed + 1000)
        bsd_scope = [[], ["-a"], ["-s"], ["-d", "0"], ["-d", "1"]]
        bsd_unit = [[], ["-h"], ["--si"], ["-k"], ["-m"], ["-g"], ["-A"], ["-A", "-h"], ["-B", "1024"]]
        bsd_link = [[], ["-P"], ["-H"], ["-L"]]
        bsd_extra = [[], ["-c"], ["-l"], ["-n"], ["-r"], ["-x"], ["-I", "*.tmp"]]
        bsd_ops = [["d"], ["d", "d"], ["bsd_extra"], ["sparse"], ["err1"]]
        for idx in range(args.random):
            combo: list[str] = []
            combo.extend(bsd_rng.choice(bsd_scope))
            combo.extend(bsd_rng.choice(bsd_unit))
            combo.extend(bsd_rng.choice(bsd_link))
            for flag in bsd_rng.sample(bsd_extra, k=2):
                combo.extend(flag)
            operands = bsd_rng.choice(bsd_ops)
            vectors.append((f"bsd-rand-{idx:02d}", "random", {}, combo + operands, None))

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

        thread_modes = [m.strip() for m in args.threads_matrix.split(",") if m.strip()] if args.threads_matrix else [None]
        mode_failures = []
        last_rc = ref_rc
        last_to = False
        stdout_match = True
        stderr_match = True
        exit_match = True

        for mode in thread_modes:
            cand_env = dict(env_ov)
            if mode == "auto":
                cand_env.pop("HYPERDU_THREADS", None)
            elif mode is not None:
                cand_env["HYPERDU_THREADS"] = mode

            cand_rc, cand_out, cand_err, cand_to = run_one(cand_du, cand_env, vec_args, stdin_data)
            last_rc = cand_rc
            if cand_to:
                last_to = True

            s_ok = (cand_out == ref_out)
            e_ok = (cand_err == ref_err)
            x_ok = (cand_rc == ref_rc) and (not cand_to) and (not ref_to)
            stdout_match = stdout_match and s_ok
            stderr_match = stderr_match and e_ok
            exit_match = exit_match and x_ok

            prefix = f"[threads={mode}] " if mode is not None else ""
            if cand_to:
                mode_failures.append(f"{prefix}candidate-timeout")
            if not x_ok and not cand_to and not ref_to:
                mode_failures.append(f"{prefix}exit({cand_rc}!={ref_rc})")
            if not s_ok:
                mode_failures.append(f"{prefix}stdout-diff({len(cand_out)}B!={len(ref_out)}B)")
            if not e_ok:
                mode_failures.append(f"{prefix}stderr-diff({len(cand_err)}B!={len(ref_err)}B)")

        if ref_to:
            mode_failures.insert(0, "reference-timeout")

        passed = stdout_match and stderr_match and exit_match

        return {
            "id": vec_id,
            "category": category,
            "args": vec_args,
            "env": env_ov,
            "threads_matrix": thread_modes if args.threads_matrix else None,
            "passed": passed,
            "stdout_match": stdout_match,
            "stderr_match": stderr_match,
            "exit_match": exit_match,
            "timed_out": last_to,
            "ref_exit": ref_rc,
            "cand_exit": last_rc,
            "reasons": mode_failures,
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
