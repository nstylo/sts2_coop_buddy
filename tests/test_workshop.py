"""Check release contents and uploader dispatch without contacting Steam."""
import importlib.util
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("workshop", ROOT / "tools/workshop.py")
workshop = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workshop)


class WorkshopTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="coop-buddy-workshop-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        output = redirect_stdout(io.StringIO())
        output.__enter__()
        self.addCleanup(output.__exit__, None, None, None)
        self.workspace = self.root / "workshop"
        self.workspace.mkdir()
        self.manifest = {"id": "CoopBuddy", "version": "0.5.2", "min_game_version": "0.111.0"}
        (self.root / "CoopBuddy.json").write_text(json.dumps(self.manifest))
        self.source = self.root / "dist/CoopBuddy"
        self.source.mkdir(parents=True)
        (self.source / "CoopBuddy.json").write_text(json.dumps(self.manifest))
        (self.source / "CoopBuddy.dll").write_bytes(b"fixture mod assembly")
        (self.source / "GodotSharp.dll").write_bytes(b"must not be uploaded")
        (self.source / "CoopBuddy.pdb").write_bytes(b"must not be uploaded")
        for name in ["workshop.json", "image.png"]:
            (self.workspace / name).write_bytes((ROOT / "workshop" / name).read_bytes())
        for name, value in [("ROOT", self.root), ("WORKSPACE", self.workspace)]:
            patcher = patch.object(workshop, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_prepare_excludes_build_extras_and_preserves_listing_and_item_id(self):
        identity = self.workspace / "mod_id.txt"
        identity.write_text("1234567890\n")
        metadata = (self.workspace / "workshop.json").read_bytes()
        workshop.prepare(build=False)
        workshop.prepare(build=False)
        files = {path.relative_to(self.workspace / "content").as_posix()
                 for path in (self.workspace / "content").rglob("*") if path.is_file()}
        self.assertEqual(files, {"CoopBuddy/CoopBuddy.dll", "CoopBuddy/CoopBuddy.json"})
        self.assertEqual(identity.read_text(), "1234567890\n")
        self.assertEqual((self.workspace / "workshop.json").read_bytes(), metadata)

    def test_unexpected_content_is_rejected_before_release(self):
        workshop.prepare(build=False)
        extra = self.workspace / "content/engine.log"
        extra.write_text("private test diagnostics")
        with self.assertRaisesRegex(ValueError, "Unexpected files"):
            workshop.prepare(build=False)
        with self.assertRaisesRegex(ValueError, "must contain only"):
            workshop.validate_workspace()
        self.assertTrue(extra.exists())

    def test_dry_run_never_builds_installs_or_invokes_steam(self):
        workshop.prepare(build=False)
        with patch.object(workshop, "prepare") as prepare, patch.object(workshop, "install_uploader") as install, \
                patch.object(workshop.subprocess, "run") as run, \
                patch.object(workshop.sys, "argv", ["workshop.py", "upload", "--dry-run", "--id", "1234567890"]):
            self.assertEqual(workshop.main(), 0)
            prepare.assert_not_called()
            install.assert_not_called()
            run.assert_not_called()
        self.assertFalse((self.workspace / "mod_id.txt").exists())

    def test_upload_uses_official_command_and_steam_appid_working_directory(self):
        executable = self.root / "uploader with spaces/ModUploader"
        executable.parent.mkdir()
        executable.touch()
        with patch.object(workshop, "prepare") as prepare, patch.object(workshop.subprocess, "run") as run, \
                patch.object(workshop.sys, "argv", ["workshop.py", "upload", "--uploader", str(executable), "--id", "1234567890"]):
            self.assertEqual(workshop.main(), 0)
            prepare.assert_called_once_with()
            run.assert_called_once_with([str(executable), "upload", "-w", str(self.workspace), "--id", "1234567890"],
                                        cwd=executable.parent, check=True)

    def test_stale_content_oversized_preview_and_invalid_identity_are_rejected(self):
        workshop.prepare(build=False)
        staged = self.workspace / "content/CoopBuddy/CoopBuddy.dll"
        staged.write_bytes(b"old build")
        with self.assertRaisesRegex(ValueError, "differs from dist"):
            workshop.validate_workspace()
        workshop.prepare(build=False)
        image = self.workspace / "image.png"
        original = image.read_bytes()
        image.write_bytes(b"\x89PNG\r\n\x1a\n" + b"0" * 1_000_000)
        with self.assertRaisesRegex(ValueError, "smaller than 1 MB"):
            workshop.validate_workspace()
        image.write_bytes(original)
        (self.workspace / "mod_id.txt").write_text("0")
        with self.assertRaisesRegex(ValueError, "Workshop item ID"):
            workshop.validate_workspace()

    def test_release_platforms_and_unsupported_architecture(self):
        for host, machine, expected in [("darwin", "arm64", "osx-arm64"), ("darwin", "x86_64", "osx-x64"),
                                        ("linux", "x86_64", "linux-x64"), ("win32", "AMD64", "win-x64"),
                                        ("win32", "ARM64", "win-arm64")]:
            with self.subTest(host=host, machine=machine):
                self.assertEqual(workshop.uploader_platform(host, machine), expected)
        with self.assertRaisesRegex(ValueError, "No official uploader release"):
            workshop.uploader_platform("linux", "aarch64")

    def test_corrupt_download_does_not_install_an_executable(self):
        archive = self.root / "corrupt.zip"
        archive.write_bytes(b"not the official release")
        with patch.object(workshop, "uploader_platform", return_value="osx-arm64"):
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                workshop.install_uploader(archive)
        self.assertFalse(workshop.uploader_path("osx-arm64").exists())

    def test_branch_check_restores_public_artifact_even_if_beta_fails(self):
        import subprocess
        original = (self.source / "CoopBuddy.dll").read_bytes()
        public = SimpleNamespace(root=self.root / "public", data_dir=self.root / "public/data")
        beta = SimpleNamespace(root=self.root / "beta", data_dir=self.root / "beta/data")
        calls = []
        def run(command, **kwargs):
            calls.append(command)
            pinned = Path(command[command.index("--mod-dir") + 1])
            self.assertEqual((pinned / "CoopBuddy.dll").read_bytes(), original)
            (self.source / "CoopBuddy.dll").write_bytes(b"test runner rebuilt a different artifact")
            if len(calls) == 2:
                raise subprocess.CalledProcessError(1, command)
        with patch.object(workshop, "prepare"), patch.object(workshop, "resolve_layout", side_effect=[public, beta]), \
                patch.object(workshop.subprocess, "run", side_effect=run):
            with self.assertRaises(subprocess.CalledProcessError):
                workshop.check_branches(public.root, beta.root)
        self.assertEqual((self.source / "CoopBuddy.dll").read_bytes(), original)
        self.assertEqual(calls[0][calls[0].index("--mod-dir") + 1], calls[1][calls[1].index("--mod-dir") + 1])


if __name__ == "__main__":
    unittest.main()
