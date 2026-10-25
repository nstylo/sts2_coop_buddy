#!/usr/bin/env python3
"""Prepare and upload Co-op Reward Viewer using Mega Crit's official uploader."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shlex
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))
from game_layout import resolve_layout

WORKSPACE = ROOT / "workshop"
FILES = ("RewardViewer.dll", "RewardViewer.json")
UPLOADER_VERSION = "v0.2.0"
# SHA-256 digests published by GitHub for Mega Crit's release assets.
RELEASES = {
    "osx-arm64": "7a21d6f0890485a96f4cc27793ffa48d415158af46eae6fcf751f080bff6151f",
    "osx-x64": "ee15c1da64d713cb54d16bfbb3e8e83462dfbe1ce0b7c515c831a98bdbb8f476",
    "linux-x64": "5b29c94bff029cd308aebd78ee153224ad16b5e9ad969c7739c128dd0d1bc2d9",
    "win-x64": "2b55c19cc5932235ca9dbd663ca07d04e5d5a0018402303199f9b4ec8ca06578",
    "win-arm64": "8407b86c7465a69de573514da62511438f4d32d709e99f413da7d20e73a2eb7a",
}


def uploader_platform(host=None, machine=None):
    host = host or sys.platform
    machine = (machine or platform.machine()).lower()
    system = {"darwin": "osx", "linux": "linux", "win32": "win"}.get(host)
    architecture = {"arm64": "arm64", "aarch64": "arm64", "amd64": "x64", "x86_64": "x64"}.get(machine)
    target = f"{system}-{architecture}"
    if target not in RELEASES:
        raise ValueError(f"No official uploader release for {host}/{machine}; use --uploader with a compatible build.")
    return target


def uploader_path(target):
    return ROOT / ".tools/ModUploader" / UPLOADER_VERSION / target / ("ModUploader.exe" if target.startswith("win-") else "ModUploader")


def install_uploader(archive=None):
    target = uploader_platform()
    executable = uploader_path(target)
    if executable.is_file() and archive is None:
        print(f"Uploader already installed: {executable}")
        return executable
    executable.parent.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="uploader-", dir=executable.parent.parent) as temporary:
        download = Path(temporary) / "uploader.zip"
        if archive is not None:
            shutil.copyfile(archive, download)
        else:
            url = f"https://github.com/megacrit/sts2-mod-uploader/releases/download/{UPLOADER_VERSION}/ModUploader-{target}.zip"
            print(f"Downloading {url}", flush=True)
            with urllib.request.urlopen(url, timeout=60) as response, download.open("wb") as output:
                shutil.copyfileobj(response, output)
        if hashlib.sha256(download.read_bytes()).hexdigest() != RELEASES[target]:
            raise ValueError("Uploader archive checksum mismatch; nothing was installed.")
        with zipfile.ZipFile(download) as package:
            package.extractall(executable.parent)
    if not executable.is_file() or not (executable.parent / "steam_appid.txt").is_file():
        raise ValueError("Official uploader archive is missing its executable or Steam app ID.")
    if os.name != "nt":
        executable.chmod(0o755)
    print(f"Installed official uploader {UPLOADER_VERSION}: {executable}")
    return executable


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def item_id(value):
    if not value.isascii() or not value.isdigit() or not 0 < int(value) < 2**64:
        raise ValueError("Workshop item ID must be an integer between 1 and 18446744073709551615.")
    return value


def validate_workspace():
    config = read_json(WORKSPACE / "workshop.json")
    if not config.get("title") or not config.get("description"):
        raise ValueError("Fill in title and description in workshop/workshop.json.")
    if config.get("visibility") not in ("private", "public", "unlisted", "friends_only"):
        raise ValueError("Workshop visibility must be private, public, unlisted, or friends_only.")
    image = WORKSPACE / "image.png"
    if not image.is_file() or image.stat().st_size >= 1_000_000 or not image.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError("workshop/image.png must be a PNG smaller than 1 MB.")
    source = ROOT / "dist/RewardViewer"
    content = WORKSPACE / "content"
    expected = {Path("RewardViewer") / name for name in FILES}
    actual = {path.relative_to(content) for path in content.rglob("*") if path.is_file()}
    if actual != expected or any(path.is_symlink() for path in content.rglob("*")):
        raise ValueError("Workshop content must contain only content/RewardViewer/RewardViewer.dll and RewardViewer.json. Run prepare.")
    for name in FILES:
        if (content / "RewardViewer" / name).read_bytes() != (source / name).read_bytes():
            raise ValueError("Workshop content differs from dist/RewardViewer. Run prepare.")
    manifest = read_json(source / "RewardViewer.json")
    if manifest != read_json(ROOT / "RewardViewer.json"):
        raise ValueError("The packaged manifest is stale. Run prepare without --no-build.")
    identity = WORKSPACE / "mod_id.txt"
    saved_id = item_id(identity.read_text().strip()) if identity.exists() else None
    print(f"{config['title']} | mod {manifest['version']} | {config['visibility']}")
    print(f"Branches: {config.get('minBranch', 'any')} .. {config.get('maxBranch', 'any')}")
    print(f"Workshop item: {saved_id or 'new item'} | content: DLL + manifest | preview: {image.stat().st_size:,} bytes")
    return saved_id


def prepare(build=True, data_dir=None):
    if build:
        env = dict(os.environ, DOTNET_CLI_HOME=str(Path(tempfile.gettempdir()) / "reward-viewer-dotnet"),
                   DOTNET_GENERATE_ASPNET_CERTIFICATE="false", DOTNET_CLI_TELEMETRY_OPTOUT="1")
        command = ["dotnet", "build", "RewardViewer.csproj", "-t:Rebuild", "-c", "Release", "--disable-build-servers"]
        if data_dir is not None:
            command += [f"-p:Sts2DataDir={data_dir}"]
        subprocess.run(command, cwd=ROOT, env=env, check=True)
    source = ROOT / "dist/RewardViewer"
    # Refuse to overwrite unrelated files or accidentally include them in a release.
    content = WORKSPACE / "content"
    expected = {Path("RewardViewer") / name for name in FILES}
    if any(path.is_symlink() or path.is_file() and path.relative_to(content) not in expected for path in content.rglob("*")):
        raise ValueError("Unexpected files in workshop/content. Move them out before preparing the upload.")
    for name in FILES:
        if not (source / name).is_file():
            raise ValueError(f"Missing {source / name}. Run prepare without --no-build.")
    destination = content / "RewardViewer"
    destination.mkdir(parents=True, exist_ok=True)
    for name in FILES:
        shutil.copyfile(source / name, destination / name)
    validate_workspace()
    print(f"Workspace ready: {WORKSPACE}")


def check_branches(public_game, beta_game):
    public = resolve_layout(public_game)
    beta = resolve_layout(beta_game)
    prepare(data_dir=public.data_dir)
    # Keep the public-built DLL fixed while the test assembly is compiled for each
    # game. The runner installs this same DLL in both loader locations.
    with tempfile.TemporaryDirectory(prefix="reward-viewer-release-") as temporary:
        artifact = Path(temporary)
        distribution = ROOT / "dist/RewardViewer"
        for name in FILES:
            shutil.copyfile(distribution / name, artifact / name)
        try:
            for branch, layout in [("public", public), ("public-beta", beta)]:
                print(f"Checking {branch} with the fixed public-built DLL", flush=True)
                subprocess.run([sys.executable, str(ROOT / "tests/run-runtime-tests.py"), "--game", str(layout.root),
                                "--data-dir", str(layout.data_dir), "--mod-dir", str(artifact)], cwd=ROOT, check=True)
        finally:
            for name in FILES:
                shutil.copyfile(artifact / name, distribution / name)
            version = read_json(distribution / "RewardViewer.json")["version"]
            with zipfile.ZipFile(ROOT / f"dist/RewardViewer-{version}.zip", "w", zipfile.ZIP_DEFLATED) as archive:
                for name in FILES:
                    archive.write(distribution / name, name)
    prepare(build=False)
    print("Both branches passed with the same DLL. Publish with upload --no-build.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    setup = commands.add_parser("setup", help="Download the official uploader for this computer; no Steam login or upload")
    setup.add_argument("--archive", type=Path, help="Install an already downloaded official release ZIP (checksum verified)")
    stage = commands.add_parser("prepare", help="Build and stage only the DLL and manifest; preserve listing metadata and item ID")
    stage.add_argument("--no-build", action="store_true", help="Use an already built dist/RewardViewer")
    check = commands.add_parser("check", help="Build against public and test that exact DLL on public and beta")
    check.add_argument("--public", type=Path, required=True, help="Latest public game's folder or macOS .app")
    check.add_argument("--beta", type=Path, required=True, help="Latest beta game's folder or macOS .app")
    upload = commands.add_parser("upload", help="Build, stage, and submit to Workshop using the logged-in Steam account")
    upload.add_argument("--dry-run", action="store_true", help="Check the prepared workspace and print the command; no Steam initialization or upload")
    upload.add_argument("--uploader", type=Path, help="Path to an existing official ModUploader executable")
    upload.add_argument("--id", help="Update an existing Workshop item; otherwise use workshop/mod_id.txt or create a new item")
    upload.add_argument("--no-build", action="store_true", help="Upload the existing package verified by check, without rebuilding it")
    args = parser.parse_args()
    try:
        if args.command == "setup":
            install_uploader(args.archive)
        elif args.command == "prepare":
            prepare(not args.no_build)
        elif args.command == "check":
            check_branches(args.public, args.beta)
        else:
            identity = item_id(args.id) if args.id is not None else None
            if args.dry_run:
                validate_workspace()
            else:
                prepare(build=False) if args.no_build else prepare()
            executable = args.uploader.expanduser().resolve() if args.uploader else uploader_path(uploader_platform())
            command = [str(executable), "upload", "-w", str(WORKSPACE)]
            if identity is not None:
                command += ["--id", identity]
                print(f"Target item override: {identity}")
            print(subprocess.list2cmdline(command) if os.name == "nt" else shlex.join(command), flush=True)
            if not args.dry_run:
                if not executable.is_file():
                    raise ValueError("Uploader not installed. Run: python3 tools/workshop.py setup")
                # The official tool reads steam_appid.txt and writes logs in its working directory.
                subprocess.run(command, cwd=executable.parent, check=True)
    except (ValueError, OSError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        parser.exit(1, f"Error: {error}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
