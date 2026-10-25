"""Locate a native STS2 export without assuming its executable's filename."""
from dataclasses import dataclass
from pathlib import Path
import platform
import plistlib
import sys


@dataclass(frozen=True)
class GameLayout:
    root: Path
    executable: Path
    data_dir: Path
    pack: Path
    game_platform: str

    @property
    def data_suffix(self):
        return self.data_dir.name.removeprefix("data_sts2_")


def resolve_layout(game, *, host=None, machine=None, executable=None, data_dir=None, pack=None):
    host = host or sys.platform
    game_platform = {"darwin": "macos", "linux": "linuxbsd", "win32": "windows"}.get(host)
    if game_platform is None:
        raise ValueError(f"Unsupported host: {host}")
    root = Path(game).expanduser().resolve()
    if game_platform == "macos" and root.suffix != ".app":
        root /= "SlayTheSpire2.app"
    resources = root / "Contents/Resources" if game_platform == "macos" else root
    packs = sorted(resources.glob("*.pck"))
    if pack is None and len(packs) != 1:
        raise ValueError(f"Expected one game PCK in {resources}; specify --pack")
    pack = Path(pack).resolve() if pack else packs[0]

    if executable:
        executable = Path(executable).resolve()
    elif game_platform == "macos":
        with (root / "Contents/Info.plist").open("rb") as file:
            executable = root / "Contents/MacOS" / plistlib.load(file)["CFBundleExecutable"]
    else:
        candidates = []
        for path in root.iterdir():
            if not path.is_file():
                continue
            if game_platform == "windows":
                if path.suffix.lower() == ".exe":
                    candidates.append(path)
            else:
                with path.open("rb") as file:
                    if file.read(4) == b"\x7fELF":
                        candidates.append(path)
        def normalized(path):
            return "".join(char for char in path.stem.lower() if char.isalnum())
        matches = [path for path in candidates if normalized(path) == normalized(pack)]
        if len(matches) == 1:
            executable = matches[0]
        elif len(candidates) == 1:
            executable = candidates[0]
        else:
            raise ValueError(f"Cannot identify the game executable in {root}; specify --executable")

    if data_dir:
        data_dir = Path(data_dir).resolve()
    else:
        architecture = "arm64" if (machine or platform.machine()).lower() in ("arm64", "aarch64") else "x86_64"
        candidates = sorted(path for path in resources.glob(f"data_sts2_{game_platform}_*")
                            if (path / "sts2.dll").is_file())
        preferred = resources / f"data_sts2_{game_platform}_{architecture}"
        if preferred in candidates:
            data_dir = preferred
        elif len(candidates) == 1:
            data_dir = candidates[0]
        else:
            raise ValueError(f"Cannot identify the game assembly directory in {resources}; specify --data-dir")

    for path in (executable, pack, data_dir):
        if not path.exists() or not path.is_relative_to(root):
            raise ValueError(f"Game files must exist inside the installation: {path}")
    for name in ("sts2.dll", "GodotSharp.dll", "0Harmony.dll", "sts2.deps.json", "sts2.runtimeconfig.json"):
        if not (data_dir / name).is_file():
            raise ValueError(f"Missing game dependency: {data_dir / name}")
    return GameLayout(root, executable, data_dir, pack, game_platform)
