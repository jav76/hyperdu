#!/usr/bin/env python3
"""Fill a package manifest template from a release's SHA256SUMS.

Placeholders: @VERSION@ and @SHA256:<asset-name>@. Fails if any placeholder
is left unresolved, so a renamed release asset can't ship an empty hash.

Usage: render-package-template.py <template> <version> <SHA256SUMS> [-o <output>]
"""
import argparse
import re
import sys


def load_sums(path):
    sums = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            parts = line.split()
            if len(parts) == 2:
                sums[parts[1].lstrip("*")] = parts[0].lower()
    return sums


def render(template, version, sums):
    def sha(match):
        name = match.group(1)
        if name not in sums:
            raise KeyError(f"SHA256SUMS has no entry for {name}")
        return sums[name]

    out = template.replace("@VERSION@", version)
    out = re.sub(r"@SHA256:([^@\s]+)@", sha, out)
    leftover = re.findall(r"@[A-Z0-9_]+(?::[^@\s]*)?@", out)
    if leftover:
        raise ValueError(f"unresolved placeholders: {sorted(set(leftover))}")
    return out


def main():
    p = argparse.ArgumentParser()
    p.add_argument("template")
    p.add_argument("version")
    p.add_argument("sums")
    p.add_argument("-o", "--output")
    a = p.parse_args()

    with open(a.template, encoding="utf-8") as f:
        text = render(f.read(), a.version, load_sums(a.sums))
    if a.output:
        with open(a.output, "w", encoding="utf-8") as f:
            f.write(text)
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
