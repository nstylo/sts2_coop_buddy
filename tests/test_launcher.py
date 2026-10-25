"""Prevent concurrent fastmp identities and retrying stale native ENet peers."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("launch-local-coop.py")
spec = importlib.util.spec_from_file_location("coop_launcher", SCRIPT)
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


class LauncherTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="reward-viewer-launcher-")
        self.addCleanup(temporary.cleanup)
        self.logs = Path(temporary.name)

    def test_three_concurrent_clients_get_distinct_profiles(self):
        with launcher.reserve_player("join", None, self.logs) as first:
            with launcher.reserve_player("join", None, self.logs) as second:
                with launcher.reserve_player("join", None, self.logs) as third:
                    self.assertEqual([first, second, third], [1000, 2000, 3000])
                    with self.assertRaisesRegex(ValueError, "No free client ID"):
                        with launcher.reserve_player("join", None, self.logs):
                            self.fail("a fourth client must not reuse an occupied identity")

    def test_duplicate_explicit_id_and_host_are_rejected(self):
        for role, player_id in [("join", 2000), ("host", 1)]:
            with self.subTest(role=role), launcher.reserve_player(role, player_id, self.logs):
                with self.assertRaisesRegex(ValueError, "already running"):
                    with launcher.reserve_player(role, player_id, self.logs):
                        self.fail("a duplicate process must not launch")

    def test_reservation_is_visible_to_another_process_and_releases(self):
        probe = """
import importlib.util, sys
from pathlib import Path
sys.path.insert(0, str(Path(sys.argv[1]).parent))
spec = importlib.util.spec_from_file_location('launcher', sys.argv[1])
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)
with launcher.reserve_player('join', None, Path(sys.argv[2])) as player_id:
    print(player_id)
"""
        with launcher.reserve_player("join", 1000, self.logs):
            result = subprocess.check_output([sys.executable, "-c", probe, str(SCRIPT), str(self.logs)], text=True)
            self.assertEqual(result.strip(), "2000")
        with launcher.reserve_player("join", None, self.logs) as player_id:
            self.assertEqual(player_id, 1000)

    def test_stale_native_connection_is_not_retried(self):
        (self.logs / "host-1.log").write_text(
            "[DEBUG] [ENetHost] Acknowledging handshake for peer with ID 1000\n"
            "[DEBUG] [ENetHost] Acknowledging handshake for peer with ID 2000\n"
            "[INFO] [ENetHost] Second client attempted to connect with peer ID 2000, disconnecting them\n")
        with launcher.reserve_player("join", None, self.logs) as player_id:
            self.assertEqual(player_id, 3000)
        with self.assertRaisesRegex(ValueError, "restart the host"):
            with launcher.reserve_player("join", 2000, self.logs):
                self.fail("the native host still reserves this ID even if no launcher holds a lock")

    def test_native_disconnect_or_host_exit_allows_reuse(self):
        log = self.logs / "host-1.log"
        log.write_text("[DEBUG] [ENetHost] Acknowledging handshake for peer with ID 1000\n"
                       "[DEBUG] [ENetHost] Peer ID 1000 disconnected, reason: Quit\n")
        self.assertEqual(launcher.registered_clients(log), set())
        with log.open("a") as output:
            output.write("[DEBUG] [ENetHost] Acknowledging handshake for peer with ID 2000\n"
                         "[INFO] NGame.Quit called\n")
        self.assertEqual(launcher.registered_clients(log), set())

    def test_invalid_ids_do_not_reserve_a_slot(self):
        for role, player_id in [("host", 2), ("join", 1), ("join", -1), ("join", 2**64)]:
            with self.subTest(role=role, player_id=player_id), self.assertRaises(ValueError):
                with launcher.reserve_player(role, player_id, self.logs):
                    self.fail("invalid identity was reserved")
        self.assertEqual(list(self.logs.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
