#!/usr/bin/env python3
"""Reproduce the vendored backend from the pinned upstream Git object and patch.

Normal dotnet builds use checked-in source and do not need Python or Git/network.
Maintainers: python verify-source.py --patch PATH [--source EXISTING_CLONE]
Without --source, a temporary shallow clone is downloaded and discarded.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def git(source, *args):
    return subprocess.run(["git", "-C", str(source), *args], check=True, capture_output=True).stdout


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--patch", required=True, type=Path)
    parser.add_argument("--source", type=Path, help="Existing official Avalonia clone containing the pinned revision")
    args = parser.parse_args()
    bundle = Path(__file__).resolve().parent
    manifest = json.loads((bundle / "source-manifest.json").read_text(encoding="utf-8"))
    patch = args.patch.resolve()
    if digest(patch.read_bytes()) != manifest["patchSha256"]:
        raise SystemExit("Patch SHA256 differs from source-manifest.json")
    for name, expected in manifest["files"].items():
        if digest((bundle / "upstream" / name).read_bytes()) != expected:
            raise SystemExit(f"Vendored source differs from manifest: {name}")
    with tempfile.TemporaryDirectory(prefix="wattledger-wayland-verify-") as scratch:
        scratch = Path(scratch)
        source = args.source.resolve() if args.source else scratch / "checkout"
        if not args.source:
            subprocess.run(["git", "init", "--quiet", str(source)], check=True)
            subprocess.run(["git", "-C", str(source), "fetch", "--depth", "1",
                            manifest["repository"], manifest["revision"]], check=True)
        revision = manifest["revision"]
        if git(source, "rev-parse", revision + "^{commit}").decode().strip() != revision:
            raise SystemExit("Upstream Git revision verification failed")
        files = set(git(source, "ls-tree", "-r", "--name-only", revision).decode().splitlines())
        staging = scratch / "reproduced"
        staging.mkdir()
        for name in manifest["files"]:
            if name in files:
                target = staging / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(git(source, "show", f"{revision}:{name}"))
        subprocess.run(["git", "apply", "--check", str(patch)], cwd=staging, check=True)
        subprocess.run(["git", "apply", str(patch)], cwd=staging, check=True)
        for name, expected in manifest["files"].items():
            if digest((staging / name).read_bytes()) != expected:
                raise SystemExit(f"Reproduced source differs from manifest: {name}")
        if git(source, "show", revision + ":licence.md") != (bundle / "LICENSE-Avalonia.md").read_bytes():
            raise SystemExit("Upstream license differs")
        key_props = ET.fromstring(git(source, "show", revision + ":build/AvaloniaPublicKey.props"))
        public_key = next(node.text for node in key_props.iter() if node.tag.endswith("AvaloniaPublicKey"))
        if bytes.fromhex(public_key) != (bundle / "avalonia-public.snk").read_bytes():
            raise SystemExit("Avalonia public signing key differs")
        print(f"Verified {len(manifest['files'])} source files from {revision}, with clean patch application.")


if __name__ == "__main__":
    main()
