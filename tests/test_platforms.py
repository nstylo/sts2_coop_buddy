"""Build-path and test-staging checks; these do not substitute for native OS runs."""
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from game_layout import resolve_layout

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("runtime_runner", ROOT / "tests/run-runtime-tests.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class PlatformTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="reward-viewer-platforms-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name).resolve()

    def game(self, host, architectures=None):
        root = self.directory / "Steam library with spaces/Slay the Spire 2"
        game_platform = {"darwin": "macos", "linux": "linuxbsd", "win32": "windows"}[host]
        if host == "darwin":
            root /= "SlayTheSpire2.app"
            resources = root / "Contents/Resources"
            executable = root / "Contents/MacOS/Slay the Spire 2"
            executable.parent.mkdir(parents=True)
            (root / "Contents/Info.plist").write_bytes(plistlib.dumps({"CFBundleExecutable": executable.name}))
        else:
            resources = root
            executable = root / ("SlayTheSpire2.exe" if host == "win32" else "SlayTheSpire2.x86_64")
        resources.mkdir(parents=True)
        executable.write_bytes(b"MZ" if host == "win32" else b"\x7fELF")
        (resources / "Slay the Spire 2.pck").write_bytes(b"original game pack")
        for architecture in architectures or ["x86_64"]:
            data = resources / f"data_sts2_{game_platform}_{architecture}"
            data.mkdir()
            for name in ("sts2.dll", "GodotSharp.dll", "0Harmony.dll"):
                (data / name).write_bytes(b"fixture")
            (data / "sts2.runtimeconfig.json").write_text('{"runtimeOptions":{}}')
            (data / "sts2.deps.json").write_text(json.dumps({
                "runtimeTarget": {"name": "fixture"}, "targets": {"fixture": {}}, "libraries": {}}))
        return root

    def properties(self, *values):
        env = dict(os.environ, DOTNET_CLI_HOME=str(Path(tempfile.gettempdir()) / "reward-viewer-dotnet"),
                   DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_GENERATE_ASPNET_CERTIFICATE="false")
        output = subprocess.check_output(["dotnet", "msbuild", str(ROOT / "RewardViewer.csproj"), "-nologo",
            "-getProperty:Sts2Dir,Sts2DataDir,PlatformTarget,RuntimeIdentifier", *values],
            cwd=ROOT, env=env, text=True, encoding="utf-8")
        return json.loads(output)["Properties"]

    def test_windows_layout_and_msbuild_paths(self):
        root = self.game("win32")
        layout = resolve_layout(root, host="win32", machine="AMD64")
        properties = self.properties("-p:Sts2Platform=windows", f"-p:Sts2Dir={root}")
        self.assertEqual(Path(properties["Sts2DataDir"]), layout.data_dir)
        self.assertEqual(properties["PlatformTarget"], "AnyCPU")
        self.assertEqual(properties["RuntimeIdentifier"], "")

    def test_linux_layout_and_msbuild_paths(self):
        root = self.game("linux")
        layout = resolve_layout(root, host="linux", machine="x86_64")
        properties = self.properties("-p:Sts2Platform=linuxbsd", f"-p:Sts2Dir={root}")
        self.assertEqual(Path(properties["Sts2DataDir"]), layout.data_dir)
        self.assertEqual(layout.executable.name, "SlayTheSpire2.x86_64")

    def test_mac_arm64_layout_and_msbuild_paths(self):
        root = self.game("darwin", ["arm64", "x86_64"])
        layout = resolve_layout(root, host="darwin", machine="arm64")
        properties = self.properties("-p:Sts2Platform=macos", f"-p:Sts2Dir={root.parent}")
        self.assertEqual(Path(properties["Sts2DataDir"]), layout.data_dir)
        self.assertEqual(layout.data_suffix, "macos_arm64")

    def test_mac_intel_layout_and_msbuild_paths(self):
        root = self.game("darwin")
        layout = resolve_layout(root, host="darwin", machine="x86_64")
        properties = self.properties("-p:Sts2Platform=macos", f"-p:Sts2Dir={root.parent}")
        self.assertEqual(Path(properties["Sts2DataDir"]), layout.data_dir)
        self.assertEqual(layout.data_suffix, "macos_x86_64")

    def test_mac_steam_folder_is_accepted(self):
        root = self.game("darwin", ["arm64"])
        self.assertEqual(resolve_layout(root.parent, host="darwin", machine="arm64").root, root)

    def test_explicit_assembly_directory_takes_precedence(self):
        root = self.game("win32")
        data = root / "data_sts2_windows_x86_64"
        properties = self.properties("-p:Sts2Platform=macos", "-p:Sts2Dir=/unused", f"-p:Sts2DataDir={data}")
        self.assertEqual(Path(properties["Sts2DataDir"]), data)

    def test_custom_steam_library(self):
        library = self.directory / "custom steamapps"
        properties = self.properties("-p:Sts2Platform=linuxbsd", f"-p:SteamLibraryPath={library}")
        self.assertEqual(Path(properties["Sts2Dir"]), library / "common/Slay the Spire 2")

    def test_missing_game_dependency_fails_early(self):
        root = self.game("linux")
        (root / "data_sts2_linuxbsd_x86_64/0Harmony.dll").unlink()
        with self.assertRaisesRegex(ValueError, "Missing game dependency"):
            resolve_layout(root, host="linux")

    def test_ambiguous_executable_requires_explicit_choice(self):
        root = self.game("win32")
        original = root / "SlayTheSpire2.exe"
        renamed = root / "game.exe"
        original.rename(renamed)
        (root / "helper.exe").write_bytes(b"MZ")
        with self.assertRaisesRegex(ValueError, "specify --executable"):
            resolve_layout(root, host="win32")
        self.assertEqual(resolve_layout(root, host="win32", executable=renamed).executable, renamed)

    def test_ambiguous_pack_requires_explicit_choice(self):
        root = self.game("linux")
        (root / "other.pck").write_bytes(b"fixture")
        with self.assertRaisesRegex(ValueError, "specify --pack"):
            resolve_layout(root, host="linux")

    def test_windows_staging_needs_no_symlinks_and_keeps_pack_private(self):
        root = self.game("win32")
        (root / "mods/OtherMod").mkdir(parents=True)
        (root / "mods/OtherMod/OtherMod.dll").write_bytes(b"do not load")
        layout = resolve_layout(root, host="win32")
        project = self.directory / "project"
        output = project / "tests/bin/Release/net9.0"
        output.mkdir(parents=True)
        (output / "RewardViewer.RuntimeTests.deps.json").write_text('{"targets":{"fixture":{}},"libraries":{}}')
        (output / "RewardViewer.RuntimeTests.dll").write_bytes(b"test")
        distribution = project / "dist/RewardViewer"
        distribution.mkdir(parents=True)
        for name in ("RewardViewer.dll", "RewardViewer.json"):
            (distribution / name).write_bytes(b"test")
        stage = self.directory / "stage"
        stage.mkdir()
        with patch.object(runner, "ROOT", project), patch.object(runner.sys, "platform", "win32"):
            executable, pack = runner.stage_install(layout, stage)
        self.assertTrue(executable.is_file())
        self.assertFalse(any(path.is_symlink() for path in stage.rglob("*")))
        self.assertFalse((executable.parent / "mods/OtherMod").exists())
        pack.write_bytes(b"modified test pack")
        self.assertEqual(layout.pack.read_bytes(), b"original game pack")
        pinned = self.directory / "public-built-mod"
        pinned.mkdir()
        for name in ("RewardViewer.dll", "RewardViewer.json"):
            (pinned / name).write_bytes(b"pinned public artifact")
        second_stage = self.directory / "beta-stage"
        second_stage.mkdir()
        with patch.object(runner, "ROOT", project), patch.object(runner.sys, "platform", "win32"):
            pinned_executable, _ = runner.stage_install(layout, second_stage, pinned)
        staged_data = second_stage / layout.root.name / layout.data_dir.relative_to(layout.root)
        self.assertEqual((staged_data / "RewardViewer.dll").read_bytes(), b"pinned public artifact")
        self.assertEqual((pinned_executable.parent / "mods/RewardViewer/RewardViewer.dll").read_bytes(), b"pinned public artifact")

    def test_filesystem_clone_failure_falls_back_to_private_copy(self):
        source = self.directory / "source.pck"
        target = self.directory / "target.pck"
        source.write_bytes(b"original")
        with patch.object(runner.sys, "platform", "linux"), patch.object(runner.subprocess, "run", side_effect=FileNotFoundError):
            runner.copy_private(source, target)
        target.write_bytes(b"modified")
        self.assertEqual(source.read_bytes(), b"original")


if __name__ == "__main__":
    unittest.main()
