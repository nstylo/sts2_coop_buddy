#!/usr/bin/env python3
"""Run native engine tests on macOS, Linux, or Windows using in-memory game saves.

Writes to build output and a new temporary copy of the installation. The normal
game scene, Steam, other installed mods, and telemetry are not started.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import struct
import subprocess
import sys
import tempfile

from game_layout import resolve_layout

ROOT = Path(__file__).resolve().parents[1]


def copy_private(source, target):
    """Clone on supported filesystems, otherwise copy. Never hard-link mutable data."""
    command = {"darwin": ["cp", "-c"], "linux": ["cp", "--reflink=auto", "--preserve=mode,timestamps"]}.get(sys.platform)
    if command:
        try:
            if subprocess.run(command + [str(source), str(target)],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
                return
        except OSError:
            pass
    shutil.copy2(source, target)


def stage_install(layout, stage, mod_dir=None):
    mod_dir = mod_dir or ROOT / "dist/CoopBuddy"
    target = stage / layout.root.name
    special = (layout.executable, layout.data_dir, layout.pack)
    def stage_path(source, destination):
        if source in (layout.data_dir, layout.pack):
            return
        if source.is_dir() and source.name in ("mods", "mods_STEAMTEST"):
            return
        if source == layout.executable:
            copy_private(source, destination)
        elif source.is_dir() and any(path.is_relative_to(source) for path in special):
            destination.mkdir()
            for child in source.iterdir():
                stage_path(child, destination / child.name)
        elif sys.platform != "win32":
            destination.symlink_to(source, target_is_directory=source.is_dir())
        elif source.is_dir():
            shutil.copytree(source, destination)
        else:
            shutil.copy2(source, destination)
    stage_path(layout.root, target)
    data_dir = target / layout.data_dir.relative_to(layout.root)
    data_dir.mkdir()
    # Windows needs no symlink privileges or Developer Mode.
    for path in layout.data_dir.iterdir():
        out = data_dir / path.name
        if sys.platform != "win32":
            out.symlink_to(path, target_is_directory=path.is_dir())
        elif path.is_dir():
            shutil.copytree(path, out)
        else:
            shutil.copy2(path, out)
    for path in (ROOT / "tests/bin/Release/net9.0").iterdir():
        if path.name.startswith("CoopBuddy"):
            out = data_dir / path.name
            out.unlink(missing_ok=True)
            shutil.copy2(path, out)
    # Use the shipped self-contained CLR and its dependency graph on every OS.
    shutil.copy2(layout.data_dir / "sts2.runtimeconfig.json", data_dir / "CoopBuddy.RuntimeTests.runtimeconfig.json")
    game_deps = json.loads((layout.data_dir / "sts2.deps.json").read_text())
    test_deps = json.loads((data_dir / "CoopBuddy.RuntimeTests.deps.json").read_text())
    graph = game_deps["targets"][game_deps["runtimeTarget"]["name"]]
    for test_graph in test_deps["targets"].values():
        for name, value in test_graph.items():
            if name.startswith(("CoopBuddy/", "CoopBuddy.RuntimeTests/")):
                graph[name] = value
                game_deps["libraries"][name] = test_deps["libraries"][name]
    (data_dir / "CoopBuddy.RuntimeTests.deps.json").write_text(json.dumps(game_deps))
    alias = data_dir.parent / f"data_CoopBuddy.RuntimeTests_{layout.data_suffix}"
    if sys.platform == "win32":
        shutil.copytree(data_dir, alias)
    else:
        alias.symlink_to(data_dir, target_is_directory=True)
    executable = target / layout.executable.relative_to(layout.root)
    mods = executable.parent / "mods/CoopBuddy"
    mods.mkdir(parents=True)
    for name in ("CoopBuddy.dll", "CoopBuddy.json"):
        shutil.copy2(mod_dir / name, mods / name)
        # The test assembly references CoopBuddy too. Both loader paths must
        # use the pinned artifact, even when tests were built against another game.
        shutil.copy2(mod_dir / name, data_dir / name)
    pack = target / layout.pack.relative_to(layout.root)
    copy_private(layout.pack, pack)
    return executable, pack


def variant_string(value):
    raw = value.encode() + b"\0"
    return struct.pack("<II", 4, len(raw)) + raw + b"\0" * (-len(raw) % 4)


def configure_test_pack(pack):
    # Only the temporary copy is changed. The game's assets stay local.
    with pack.open("r+b") as file:
        header = struct.unpack("<6I2Q", file.read(40))
        if header[:2] != (0x43504447, 3):
            raise ValueError("Expected Godot PCK v3; update the stager for this game build")
        base, directory = header[-2:]
        file.seek(directory)
        count, = struct.unpack("<I", file.read(4))
        entries = {}
        for _ in range(count):
            size, = struct.unpack("<I", file.read(4))
            path = file.read(size).rstrip(b"\0")
            entries[path] = file.read(36)
        offset, size = struct.unpack_from("<QQ", entries[b"project.binary"])
        file.seek(base + offset)
        config = file.read(size)
        settings, position = {}, 8
        for _ in range(struct.unpack_from("<I", config, 4)[0]):
            size, = struct.unpack_from("<I", config, position)
            position += 4
            key = config[position:position + size]
            position += size
            size, = struct.unpack_from("<I", config, position)
            position += 4
            settings[key] = config[position:position + size]
            position += size
        for key in list(settings):
            if key.startswith(b"autoload/"):
                del settings[key]
        settings[b"dotnet/project/assembly_name"] = variant_string("CoopBuddy.RuntimeTests")
        settings[b"application/run/main_scene"] = variant_string("res://coop_buddy_tests.tscn")
        settings[b"sentry/config/dsn"] = variant_string("")
        config = b"ECFG" + struct.pack("<I", len(settings))
        for key, value in settings.items():
            config += struct.pack("<I", len(key)) + key + struct.pack("<I", len(value)) + value
        for path, raw in {
            b"project.binary": config,
            b"coop_buddy_tests.tscn": b'[gd_scene format=3]\n[node name="Tests" type="Node"]\n'
        }.items():
            file.seek(0, 2)
            file.write(b"\0" * (-file.tell() % 16))
            entries[path] = struct.pack("<QQ", file.tell() - base, len(raw)) + hashlib.md5(raw, usedforsecurity=False).digest() + b"\0" * 4
            file.write(raw)
        directory = file.tell()
        file.write(struct.pack("<I", len(entries)))
        for path, record in entries.items():
            padded = path + b"\0" * (-len(path) % 4)
            file.write(struct.pack("<I", len(padded)) + padded + record)
        file.seek(32)
        file.write(struct.pack("<Q", directory))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", type=Path, help="Steam game folder, or the .app on macOS")
    parser.add_argument("--executable", type=Path)
    parser.add_argument("--data-dir", type=Path)
    parser.add_argument("--pack", type=Path)
    parser.add_argument("--mod-dir", type=Path, help="Test this existing DLL/manifest package, while compiling tests against the selected game")
    args = parser.parse_args()
    env = dict(os.environ, DOTNET_CLI_HOME=str(Path(tempfile.gettempdir()) / "coop-buddy-dotnet"),
               DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_GENERATE_ASPNET_CERTIFICATE="false", DOTNET_NOLOGO="1")
    game = args.game
    if game is None:
        output = subprocess.check_output(["dotnet", "msbuild", "CoopBuddy.csproj", "-nologo", "-getProperty:Sts2Dir,Sts2DataDir"],
                                             cwd=ROOT, env=env, text=True, encoding="utf-8")
        properties = json.loads(output)["Properties"]
        if properties["Sts2Dir"]:
            game = Path(properties["Sts2Dir"])
        else:
            data = args.data_dir or Path(properties["Sts2DataDir"])
            game = data.parents[2] if sys.platform == "darwin" else data.parent
            args.data_dir = data
    layout = resolve_layout(game, executable=args.executable, data_dir=args.data_dir, pack=args.pack)
    mod_dir = args.mod_dir.resolve() if args.mod_dir else ROOT / "dist/CoopBuddy"
    if args.mod_dir:
        for name in ("CoopBuddy.dll", "CoopBuddy.json"):
            if not (mod_dir / name).is_file():
                parser.error(f"Missing pinned mod file: {mod_dir / name}")
    print(f"Testing {layout.data_suffix}: {layout.executable}", flush=True)
    subprocess.run(["dotnet", "build", "tests/RuntimeTests.csproj", "-c", "Release", "--disable-build-servers",
                    f"-p:Sts2DataDir={layout.data_dir}"], cwd=ROOT, env=env, check=True)
    stage = Path(tempfile.mkdtemp(prefix="coop-buddy-tests-"))
    print(f"TEST_MOD_SHA256: {hashlib.sha256((mod_dir / 'CoopBuddy.dll').read_bytes()).hexdigest()}", flush=True)
    executable, pack = stage_install(layout, stage, mod_dir)
    configure_test_pack(pack)
    print(f"Test staging directory: {stage}", flush=True)
    command = [str(executable), "--headless", "--force-steam=off", "--log-file", str(stage / "engine.log"),
               "--quit-after", "1800"]
    if sys.platform == "darwin" and platform.machine() == "arm64" and layout.data_suffix == "macos_x86_64":
        command = ["arch", "-x86_64", *command]
    result = subprocess.run(command, cwd=stage, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace", timeout=90)
    (stage / "stdout.log").write_text(result.stdout, encoding="utf-8")
    print(result.stdout)
    if result.returncode or "COOP_BUDDY_TESTS_PASSED:" not in result.stdout:
        return result.returncode if result.returncode > 0 else 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
