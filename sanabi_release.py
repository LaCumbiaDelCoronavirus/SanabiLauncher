#!/usr/bin/env python3
"""
Publishes a new launcher version.

    python sanabi_release.py              # next patch version
    python sanabi_release.py 2.22.0       # a specific version
    python sanabi_release.py --dry-run    # show what would happen, change nothing

Replaces the current version (read from Launcher.props) everywhere it appears, commits that, tags it v<version>, then
pushes master and the tag together. The tag is what starts the Publish Release workflow, and that release is public,
so nothing is touched unless every check passes first. Commit your work before running this.
"""

import argparse
import fnmatch
import json
import re
import subprocess
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent
BRANCH = "master"
REMOTE = "origin"
PROPS_FILE = "Launcher.props"
CONSTANTS_FILE = "SS14.Launcher/ConfigConstants.cs"

# Third-party versions live here, and one matching ours would be a coincidence.
NOT_OURS = ("Directory.Packages.props", "*.lock", "nix/deps.json", Path(__file__).name)


class ReleaseError(Exception):
    pass


def git(*args: str, check: bool = True) -> str:
    result = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if check and result.returncode != 0:
        raise ReleaseError(f"git {' '.join(args)} failed:\n{result.stderr.strip()}")

    return result.stdout.strip()


def read_current_version() -> str:
    props = (ROOT / PROPS_FILE).read_text(encoding="utf-8-sig")
    match = re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", props)
    if match is None:
        raise ReleaseError(f"No <Version> found in {PROPS_FILE}.")

    return match.group(1)


def version_tuple(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split("."))


def next_patch_version(version: str) -> str:
    major, minor, patch = version_tuple(version)
    return f"{major}.{minor}.{patch + 1}"


def version_pattern(version: str) -> re.Pattern[bytes]:
    # A leading "v" is fine, since that's how ConfigConstants and tags spell it. Anything that extends the number
    # (12.21.16, 2.21.160, 2.21.16.0, 2.21.16a) is a different version.
    return re.compile(rb"(?<![\d.])" + re.escape(version.encode()) + rb"(?![0-9A-Za-z_]|\.\d)")


def find_replacements(version: str) -> dict[str, list[str]]:
    """Maps each tracked text file containing the version to its matching lines."""
    listing = subprocess.run(
        ["git", "grep", "-lIzF", "-e", version],
        cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")

    pattern = version_pattern(version)
    found = {}
    for path in filter(None, listing.stdout.split("\0")):
        if any(fnmatch.fnmatch(path, ignored) for ignored in NOT_OURS):
            continue

        lines = (ROOT / path).read_bytes().splitlines()
        matches = [line.decode("utf-8", "replace").strip() for line in lines if pattern.search(line)]
        if matches:
            found[path] = matches

    return found


def fetch_allowed_versions() -> list[str] | None:
    """The versions the live launcher info allows, or None if that couldn't be read."""
    try:
        constants = (ROOT / CONSTANTS_FILE).read_text(encoding="utf-8-sig")
        url = re.search(r'UrlLauncherInfo\s*=\s*new\(\["([^"]+)"', constants).group(1)
        request = urllib.request.Request(url, headers={"User-Agent": "sanabi-release"})
        with urllib.request.urlopen(request, timeout=15) as response:
            return json.load(response)["allowedVersions"]
    except (AttributeError, KeyError, OSError, ValueError):
        return None


def check_ready(current: str, new: str, replacements: dict[str, list[str]]) -> None:
    tag = f"v{new}"

    if version_tuple(new) <= version_tuple(current):
        raise ReleaseError(f"{new} isn't newer than the current version, {current}.")

    branch = git("symbolic-ref", "--quiet", "--short", "HEAD", check=False)
    if branch != BRANCH:
        raise ReleaseError(f"Releases are cut from {BRANCH}, but this is {branch or 'a detached HEAD'}.")

    status = git("status", "--porcelain")
    if status:
        raise ReleaseError(f"The working tree has uncommitted changes, which wouldn't be in the release:\n{status}")

    git("fetch", "--quiet", REMOTE, BRANCH)
    remote_ref = f"{REMOTE}/{BRANCH}"
    if subprocess.run(["git", "merge-base", "--is-ancestor", remote_ref, "HEAD"], cwd=ROOT).returncode != 0:
        raise ReleaseError(f"{remote_ref} has commits this branch doesn't. Pull first.")

    if git("rev-parse", "--quiet", "--verify", f"refs/tags/{tag}", check=False):
        raise ReleaseError(f"Tag {tag} already exists locally.")

    if git("ls-remote", "--tags", REMOTE, f"refs/tags/{tag}"):
        raise ReleaseError(f"Tag {tag} already exists on {REMOTE}.")

    # These two are what the build and the out-of-date check actually use, so missing either ships a broken release.
    for required in (PROPS_FILE, CONSTANTS_FILE):
        if required not in replacements:
            raise ReleaseError(f"{required} doesn't contain {current}, so the files have drifted apart.")


def apply_replacements(old: str, new: str, paths: list[str]) -> None:
    pattern = version_pattern(old)
    for path in paths:
        file = ROOT / path
        # Bytes, so BOMs and line endings survive untouched.
        file.write_bytes(pattern.sub(new.encode(), file.read_bytes()))


def publish(old: str, new: str, paths: list[str], trailers: list[str]) -> None:
    """Commits, tags and pushes. On failure the repository goes back to how it started, so a retry just works."""
    tag = f"v{new}"
    start = git("rev-parse", "HEAD")
    tagged = False

    try:
        apply_replacements(old, new, paths)
        git("add", "--", *paths)
        git("commit", "-m", tag, *[f"--trailer={trailer}" for trailer in trailers])
        git("tag", tag)
        tagged = True
        # Atomic, or a rejected branch could still leave a tag on the remote and start a release from it.
        git("push", "--atomic", REMOTE, BRANCH, f"refs/tags/{tag}")
    except (ReleaseError, KeyboardInterrupt):
        if tagged:
            git("tag", "-d", tag, check=False)

        # Safe to discard: check_ready proved the tree was clean, so this only drops what publish() just did.
        git("reset", "--hard", start, check=False)
        raise


def confirm(prompt: str) -> bool:
    try:
        return input(prompt).strip().lower() == "y"
    except (EOFError, KeyboardInterrupt):
        return False


def main() -> int:
    parser = argparse.ArgumentParser(description="Publishes a new launcher version.")
    parser.add_argument("version", nargs="?", help="new version, e.g. 2.22.0 (default: next patch version)")
    parser.add_argument("--dry-run", action="store_true", help="show what would happen and change nothing")
    parser.add_argument("--yes", action="store_true", help="don't ask before publishing")
    parser.add_argument("--trailer", action="append", default=[], metavar="TEXT", help="add a trailer to the commit")
    args = parser.parse_args()

    # Windows consoles can't always print what's in these files, and a preview isn't worth crashing over.
    sys.stdout.reconfigure(errors="replace")

    try:
        current = read_current_version()
        new = (args.version or next_patch_version(current)).removeprefix("v")
        if re.fullmatch(r"\d+\.\d+\.\d+", new) is None:
            raise ReleaseError(f"{new} isn't a version like 2.22.0.")

        replacements = find_replacements(current)
        check_ready(current, new, replacements)

        tag = f"v{new}"
        print(f"{current} -> {new}\n")
        for path, lines in replacements.items():
            print(path)
            for line in lines:
                print(f"    {line}")

        unpushed = git("log", "--oneline", f"{REMOTE}/{BRANCH}..HEAD")
        if unpushed:
            print(f"\nAlso going out with this push:\n{unpushed}")

        allowed = fetch_allowed_versions()
        if allowed is None:
            print("\nWARNING: couldn't read allowedVersions. Check it before people update.")
        elif tag not in allowed:
            print(f"\nWARNING: {tag} isn't in allowedVersions ({', '.join(allowed)}).")
            print("Until it's added to the launcher info paste, this release will call itself out of date.")

        if args.dry_run:
            print("\nDry run: nothing was changed.")
            return 0

        if not args.yes and not confirm(f"\nPublish {tag} publicly? [y/N] "):
            print("Cancelled: nothing was changed.")
            return 1

        publish(current, new, list(replacements), args.trailer)
    except ReleaseError as error:
        print(f"\nNot released: {error}", file=sys.stderr)
        return 1

    url = git("remote", "get-url", REMOTE).removesuffix(".git")
    print(f"\nPushed {tag}. The release is building: {url}/actions")
    return 0


if __name__ == "__main__":
    sys.exit(main())
