#!/usr/bin/env python3
import argparse
import os
import statistics
import subprocess
import sys
import time


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Measure Native AOT du startup latency on a single file.")
    parser.add_argument("--binary", required=True, help="Path to du symlink or hyperdu binary")
    parser.add_argument("--target", required=True, help="Target file operand passed to du")
    parser.add_argument("--warmup", type=int, default=5, help="Warmup invocation count")
    parser.add_argument("--runs", type=int, default=30, help="Measured invocation count")
    parser.add_argument("--max-median-ms", type=float, default=5.0, help="Maximum allowed median latency in ms")
    parser.add_argument("--markdown", help="Optional Markdown summary output path")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    binary = os.path.abspath(args.binary)
    target = os.path.abspath(args.target)

    if not os.path.exists(binary):
        print(f"Error: binary not found: {binary}", file=sys.stderr)
        return 2
    if not os.path.exists(target):
        print(f"Error: target file not found: {target}", file=sys.stderr)
        return 2

    cmd = [binary, target]
    for _ in range(args.warmup):
        res = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        if res.returncode != 0:
            print(f"Error during warmup ({res.returncode}): {res.stderr.decode(errors='replace')}", file=sys.stderr)
            return 1

    samples_ms: list[float] = []
    for _ in range(args.runs):
        t0 = time.perf_counter_ns()
        res = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        elapsed_ms = (time.perf_counter_ns() - t0) / 1_000_000.0
        if res.returncode != 0:
            print(f"Error during startup run ({res.returncode}): {res.stderr.decode(errors='replace')}", file=sys.stderr)
            return 1
        samples_ms.append(elapsed_ms)

    samples_ms.sort()
    min_ms = samples_ms[0]
    median_ms = statistics.median(samples_ms)
    p95_idx = min(len(samples_ms) - 1, int(0.95 * (len(samples_ms) - 1)))
    p95_ms = samples_ms[p95_idx]
    max_ms = samples_ms[-1]

    print(
        f"AOT startup (`du FILE`, {args.runs} runs): "
        f"min={min_ms:.2f} ms, median={median_ms:.2f} ms, p95={p95_ms:.2f} ms, max={max_ms:.2f} ms "
        f"(limit <= {args.max_median_ms:.2f} ms)"
    )

    if args.markdown:
        os.makedirs(os.path.dirname(os.path.abspath(args.markdown)), exist_ok=True)
        with open(args.markdown, "w", encoding="utf-8") as f:
            f.write("## AOT Startup Latency Check (`du FILE`)\n\n")
            f.write(f"- **Binary**: `{binary}`\n")
            f.write(f"- **Measured runs**: {args.runs} (after {args.warmup} warmup)\n")
            f.write(f"- **Threshold**: median <= {args.max_median_ms:.2f} ms\n\n")
            f.write("| Metric | Latency (ms) |\n")
            f.write("| :--- | ---: |\n")
            f.write(f"| Min | {min_ms:.2f} |\n")
            f.write(f"| Median | {median_ms:.2f} |\n")
            f.write(f"| P95 | {p95_ms:.2f} |\n")
            f.write(f"| Max | {max_ms:.2f} |\n")

    if median_ms > args.max_median_ms:
        print(
            f"Fail: AOT du FILE median startup {median_ms:.2f} ms exceeded limit {args.max_median_ms:.2f} ms.",
            file=sys.stderr,
        )
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
