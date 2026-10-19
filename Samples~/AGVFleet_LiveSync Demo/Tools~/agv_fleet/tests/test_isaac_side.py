"""Isaac-side checks that need neither Isaac Sim nor numpy (standard library only).

    python -m unittest discover -s tests -v        (from Tools~/agv_fleet)

Covers the contract with the Unity sample: environment.json loading (config.load_unity_environment),
the AGV colour palette (Runtime/Backends/IFleetBackend.cs AgvPalette), and hub authentication
(agv_fleet/transport.py).
"""

from __future__ import annotations

import colorsys
import json
import os
import socket
import sys
import tempfile
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
sys.path.insert(0, str(TOOLS))

# config reads environment.json from the stage folder at import: point it at an empty folder
_STAGE = tempfile.mkdtemp(prefix="agv_fleet_test_stage_")
os.environ["AGV_FLEET_STAGE_DIR"] = _STAGE
os.environ.pop("AGV_FLEET_HUB_TOKEN", None)

import config as cfg  # noqa: E402
from agv_fleet import transport  # noqa: E402

UNITY_JSON = {
    "version": 1,
    "identity": "generated:48x32 m, 4 rows x 3 columns, aisle 3.2 m, 3 stations, 2 AGVs, seed 7",
    "name": "generated",
    "usd": "generated_factory.usda",
    "scale": 1.0,
    "grid_source": "boxes",
    "colliders": False,
    "grid_bounds": [-24.0, -16.0, 24.0, 16.0],
    "free_seed": [-20.0, 11.6],
    "spawns": [{"id": "AGV_02", "x": -22.3, "y": 3.0, "yaw": 0.0}, {"id": "AGV_01", "x": -22.3, "y": 0.8, "yaw": 0.0}],
    "loops": [{"id": "AGV_01", "points": [-22.3, 0.8, -20.0, 0.8, -20.0, -1.3]},
              {"id": "AGV_02", "points": [-22.3, 3.0, -20.0, 3.0]}],
    "boxes": [{"cx": 1.0, "cy": 2.0, "sx": 8.1, "sy": 1.1, "yaw": 0.0}],
}


class EnvironmentJsonTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="agv_env_"))
        self.path = self.dir / "environment.json"
        self.path.write_text(json.dumps(UNITY_JSON), encoding="utf-8")

    def test_preset_from_unity(self):
        e = cfg.load_unity_environment(self.path)
        self.assertIsNotNone(e)
        self.assertEqual(e["identity"], UNITY_JSON["identity"])
        self.assertEqual(e["grid_source"], "boxes")
        self.assertFalse(e["colliders"])
        self.assertEqual(e["grid_bounds"], (-24.0, -16.0, 24.0, 16.0))
        self.assertEqual(e["omap_origin"], (-20.0, 11.6, 0.0))
        self.assertEqual(e["spawn"]["AGV_01"], (-22.3, 0.8, 0.0))
        self.assertEqual(e["loops"]["AGV_01"], [(-22.3, 0.8), (-20.0, 0.8), (-20.0, -1.3)])
        self.assertEqual(e["boxes"], [(1.0, 2.0, 8.1, 1.1, 0.0)])

    def test_relative_usd_resolves_against_the_json_folder(self):
        e = cfg.load_unity_environment(self.path)
        self.assertEqual(Path(e["usd"][0]), (self.dir / "generated_factory.usda").resolve())

    def test_missing_or_broken_file_means_no_preset(self):
        self.assertIsNone(cfg.load_unity_environment(self.dir / "nope.json"))
        self.path.write_text("{ not json", encoding="utf-8")
        self.assertIsNone(cfg.load_unity_environment(self.path))

    def test_apply_env_takes_ids_identity_and_boxes_from_unity(self):
        cfg.ENVIRONMENTS["test_unity"] = cfg.load_unity_environment(self.path)
        try:
            cfg.apply_env("test_unity", "")
            self.assertEqual(cfg.AGV_IDS, ["AGV_01", "AGV_02"], "one AGV per spawn, in id order (= priority)")
            self.assertEqual(cfg.AGV_COUNT, 2)
            self.assertEqual(cfg.ENV_IDENTITY, UNITY_JSON["identity"])
            self.assertEqual(len(cfg.ENV_BOXES), 1)
            self.assertEqual(cfg.GRID_SOURCE, "boxes")
        finally:
            del cfg.ENVIRONMENTS["test_unity"]
            cfg.apply_env("factory", "")

    def test_without_a_json_the_factory_is_the_default(self):
        self.assertNotIn("from_unity", cfg.ENVIRONMENTS)   # the test stage folder is empty
        self.assertEqual(cfg.ENV, "factory")
        self.assertTrue(cfg.ENV_IDENTITY.startswith("factory:"))


class PaletteTests(unittest.TestCase):
    def test_matches_unity(self):
        self.assertEqual(cfg.color_for("AGV_01"), (0.10, 0.55, 1.00))
        self.assertEqual(cfg.color_for("AGV_02"), (1.00, 0.55, 0.05))
        self.assertEqual(cfg.color_for("AGV_03"), (0.20, 0.80, 0.30))
        self.assertEqual(cfg.color_for("AGV_04"), colorsys.hsv_to_rgb((0.13 + 3 * 0.31) % 1.0, 0.75, 0.95))
        self.assertEqual(cfg.color_for("not_an_agv"), (0.8, 0.8, 0.8))


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class HubAuthTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.stage = Path(tempfile.mkdtemp(prefix="agv_hub_"))
        cls.token = transport.server_token(cls.stage)
        cls.port = _free_port()
        cls.hub = transport.HubServer("127.0.0.1", cls.port, cls.token)
        cls.hub.start()

    @classmethod
    def tearDownClass(cls):
        cls.hub.stop()

    def connect(self, first: bytes | None):
        s = socket.create_connection(("127.0.0.1", self.port), timeout=6)
        if first is not None:
            s.sendall(first)
        return s

    def test_token_file_is_written_and_readable_by_clients(self):
        self.assertTrue((self.stage / transport.TOKEN_FILE_NAME).exists())
        self.assertEqual(transport.client_token(self.stage), self.token)
        self.assertGreaterEqual(len(self.token), 40)

    def test_valid_token_gets_the_stream(self):
        s = self.connect(transport.auth_line(self.token))
        try:
            deadline = time.time() + 2
            while self.hub.client_count < 1 and time.time() < deadline:
                time.sleep(0.02)
            kinds = [k for k, _, _ in self.hub.poll()]
            self.assertIn("connect", kinds)
            self.hub.broadcast({"type": "state", "seq": 1})
            self.assertEqual(json.loads(s.recv(1024).decode().strip()), {"type": "state", "seq": 1})
        finally:
            s.close()

    def test_wrong_token_is_refused_and_closed(self):
        s = self.connect(transport.auth_line("wrong"))
        try:
            self.assertEqual(json.loads(s.recv(1024).decode().strip()), {"type": "auth", "ok": False})
            self.assertEqual(s.recv(1024), b"")
        finally:
            s.close()

    def test_commands_before_auth_are_refused(self):
        s = self.connect(b'{"cmd":"reset"}\n')
        try:
            self.assertEqual(json.loads(s.recv(1024).decode().strip()), {"type": "auth", "ok": False})
        finally:
            s.close()

    def test_silent_client_is_dropped(self):
        s = self.connect(None)
        try:
            t0 = time.time()
            self.assertEqual(s.recv(1024), b"")
            self.assertLess(time.time() - t0, transport.AUTH_TIMEOUT_S + 2)
        finally:
            s.close()

    def test_unauthenticated_clients_never_receive_broadcasts(self):
        s = self.connect(None)
        try:
            self.hub.broadcast({"type": "state", "seq": 2})
            s.settimeout(0.5)
            with self.assertRaises(socket.timeout):
                s.recv(1024)
        finally:
            s.close()

    def test_env_variable_token_wins(self):
        os.environ["AGV_FLEET_HUB_TOKEN"] = "from-env"
        try:
            self.assertEqual(transport.server_token(Path(tempfile.mkdtemp())), "from-env")
            self.assertEqual(transport.client_token(Path(tempfile.mkdtemp())), "from-env")
        finally:
            del os.environ["AGV_FLEET_HUB_TOKEN"]


if __name__ == "__main__":
    unittest.main()
