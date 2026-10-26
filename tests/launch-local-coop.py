#!/usr/bin/env python3
"""Launch a real local STS2 host or client using the installed game's fastmp mode."""
import argparse
from contextlib import contextmanager
import errno
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import tempfile

from game_layout import resolve_layout

ROOT = Path(__file__).resolve().parents[1]


def registered_clients(log):
    """Include stale native ENet peers until the host removes them or exits."""
    players = set()
    if not log.exists():
        return players
    with log.open(encoding="utf-8", errors="replace") as lines:
        for line in lines:
            joined = re.search(r"\[ENetHost\] Acknowledging handshake for peer with ID (\d+)", line)
            left = re.search(r"\[ENetHost\] Peer ID (\d+) disconnected", line)
            if joined:
                players.add(int(joined[1]))
            elif left:
                players.discard(int(left[1]))
            elif "Stopping host. Reason:" in line or "NGame.Quit called" in line:
                players.clear()
    return players


@contextmanager
def reserve_player(role, requested_id, logs):
    """Hold an OS lock while the game runs, so concurrent launches cannot share IDs."""
    if requested_id is not None:
        launch_arguments("game", role, requested_id, "log")  # validate before creating files
    candidates = [requested_id] if requested_id is not None else ([1] if role == "host" else [1000, 2000, 3000])
    logs.mkdir(parents=True, exist_ok=True)
    registered = registered_clients(logs / "host-1.log") if role == "join" else set()
    for player_id in candidates:
        if player_id in registered:
            continue
        lock = (logs / f"player-{player_id}.lock").open("a+b", buffering=0)
        try:
            if os.name == "nt":
                import msvcrt
                if lock.seek(0, os.SEEK_END) == 0:
                    lock.write(b"\0")
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            lock.close()
            if error.errno not in (errno.EACCES, errno.EAGAIN, errno.EDEADLK):
                raise
            continue
        try:
            yield player_id
        finally:
            lock.close()  # releasing the lock also works after an interrupted launcher
        return
    if role == "host":
        raise ValueError("A host launched by this script is already running. Close it before starting another.")
    if requested_id in registered:
        raise ValueError(f"The host still has player {requested_id} registered. Close that client; if it already "
                         "exited, restart the host and clients to clear the stale ENet connection.")
    if requested_id is not None:
        raise ValueError(f"Player {requested_id} is already running. Use another --client-id or omit it for automatic selection.")
    raise ValueError("No free client ID (1000, 2000, 3000). Close extra clients; restart the host if it retains disconnected players.")


def launch_arguments(executable, role, client_id, log):
    if role == "host" and client_id != 1:
        raise ValueError("The native ENet host ID is always 1")
    if role == "join" and (client_id <= 1 or client_id > 2**64 - 1):
        raise ValueError("Choose a client ID between 2 and 18446744073709551615")
    return [str(executable), "--force-steam=off",
            "--fastmp=host_standard" if role == "host" else "--fastmp=join",
            f"--clientId={client_id}", "--log-file", str(log)]


def main():
    parser = argparse.ArgumentParser(description=__doc__, epilog=(
        "Fresh profiles only unlock Ironclad and receive scripted tutorial rewards. "
        "In each game window, open the console with backtick/apostrophe and run 'unlock all'; "
        "then restart both windows and start a new run with different characters. "
        "See docs/local-testing.md."))
    parser.add_argument("role", choices=("host", "join"))
    parser.add_argument("--game", type=Path, help="Steam game folder or macOS .app")
    parser.add_argument("--client-id", type=int, help="Host: 1. Clients: automatically choose a free ID from 1000, 2000, 3000")
    parser.add_argument("--dry-run", action="store_true", help="Print arguments without launching")
    args = parser.parse_args()
    game = args.game
    if game is None:
        properties = json.loads(subprocess.check_output([
            "dotnet", "msbuild", "CoopBuddy.csproj", "-nologo", "-getProperty:Sts2Dir,Sts2DataDir"
        ], cwd=ROOT, text=True, encoding="utf-8", env=dict(os.environ, DOTNET_NOLOGO="1",
            DOTNET_CLI_HOME=str(Path(tempfile.gettempdir()) / "coop-buddy-dotnet"),
            DOTNET_GENERATE_ASPNET_CERTIFICATE="false", DOTNET_CLI_TELEMETRY_OPTOUT="1")))["Properties"]
        if properties["Sts2Dir"]:
            game = Path(properties["Sts2Dir"])
        else:
            data = Path(properties["Sts2DataDir"])
            game = data.parents[2] if data.parent.name == "Resources" else data.parent
    layout = resolve_layout(game)
    try:
        with reserve_player(args.role, args.client_id, ROOT / ".local/fastmp") as client_id:
            return launch(layout, args.role, client_id, args.dry_run, parser)
    except ValueError as error:
        parser.error(str(error))


def launch(layout, role, client_id, dry_run, parser):
    log = ROOT / ".local/fastmp" / f"{role}-{client_id}.log"
    engine_log = log.with_name(log.stem + "-engine.log")
    command = launch_arguments(layout.executable, role, client_id, engine_log)
    if dry_run:
        print(shlex.join(command))
        return 0
    mod_dir = layout.executable.parent / "mods/CoopBuddy"
    for name in ("CoopBuddy.dll", "CoopBuddy.json"):
        if not (mod_dir / name).is_file():
            parser.error(f"Install {name} in {mod_dir} first; see README.md")
    log.parent.mkdir(parents=True, exist_ok=True)
    print(f"Starting {role}, player {client_id}. Log: {log}", flush=True)
    # Native development option verified in DebugSettings; inherited only by this process.
    env = dict(os.environ, STS2_DEV_WINDOWED="1")
    # Capture process diagnostics as well as Godot's own log.
    with log.open("w", encoding="utf-8") as output:
        result = subprocess.call(command, cwd=layout.executable.parent, env=env,
                                 stdout=output, stderr=subprocess.STDOUT)
    if result != 0:
        print(f"Game exited with code {result}; check {log}")
    return result


if __name__ == "__main__":
    raise SystemExit(main())
