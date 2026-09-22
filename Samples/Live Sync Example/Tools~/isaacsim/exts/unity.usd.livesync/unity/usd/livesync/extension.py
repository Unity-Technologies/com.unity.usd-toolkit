"""
Kit extension: drives the Isaac Sim stage from Unity's UsdLiveSyncServer, with a small control panel.

Enable it with::

    isaac-sim.bat --ext-folder "<repo>/Tools/isaacsim/exts" --enable unity.usd.livesync

(or use `Tools/isaacsim/run_isaac_sim_with_livesync.bat`, which does exactly that).

Threading contract: `SyncClient` reads the socket on its own thread; USD is only ever touched from
Kit's update callback, which runs on the main thread. The coalescing pending map in `SyncClient` is
the handoff — so a slow frame costs staleness, never a backlog.
"""

from __future__ import annotations

import os
import threading
import time

import carb
import omni.ext
import omni.kit.app
import omni.ui as ui
import omni.usd

from .core import DEFAULT_HOST, DEFAULT_PORT, SyncClient
from .stage_bridge import StageBridge, mount_unity_stage

WINDOW_TITLE = "Unity USD Live Sync"

# Where the Unity project writes base_stage.usda. Overridable in the UI and via this env var, which
# is what the launcher .bat sets so the field is pre-filled with the right path.
BASE_STAGE_ENV = "UNITY_USD_BASE_STAGE"
DEFAULT_WRAPPER_PATH = "/World/UnityScene"
DEFAULT_ROOT_PRIM = "SyncRoot"


class UnityLiveSyncExtension(omni.ext.IExt):
    """Control panel + per-frame pump for the Unity -> Isaac transform stream."""

    # ---- lifecycle -------------------------------------------------------------------------------

    def on_startup(self, ext_id: str) -> None:
        self._ext_id = ext_id
        self._client = None
        self._bridge = None
        self._update_sub = None
        self._log_lines = []
        self._log_lock = threading.Lock()   # _log is called from the socket thread too
        self._log_dirty = False
        self._last_ui_refresh = 0.0
        self._push_back_last = 0.0

        # UI models
        self._m_host = ui.SimpleStringModel(DEFAULT_HOST)
        self._m_port = ui.SimpleIntModel(DEFAULT_PORT)
        self._m_base_stage = ui.SimpleStringModel(os.environ.get(BASE_STAGE_ENV, ""))
        self._m_wrapper = ui.SimpleStringModel(DEFAULT_WRAPPER_PATH)
        self._m_root_prim = ui.SimpleStringModel(DEFAULT_ROOT_PRIM)
        self._m_prefix = ui.SimpleStringModel(DEFAULT_WRAPPER_PATH)
        self._m_push_back = ui.SimpleStringModel("")
        self._m_push_hz = ui.SimpleFloatModel(20.0)

        self._push_back_enabled = False

        self._build_window()
        self._subscribe_updates()
        self._log("extension started — set the base_stage.usda path, Mount, then Connect.")

    def on_shutdown(self) -> None:
        self._unsubscribe_updates()
        self._disconnect()
        self._bridge = None
        if getattr(self, "_window", None) is not None:
            self._window.destroy()
            self._window = None

    # ---- UI --------------------------------------------------------------------------------------

    def _build_window(self) -> None:
        self._window = ui.Window(WINDOW_TITLE, width=560, height=520)
        with self._window.frame:
            with ui.ScrollingFrame():
                with ui.VStack(spacing=8, height=0):
                    self._section_label("1 — Unity baseline geometry")
                    self._labeled_field("base_stage.usda", self._m_base_stage, tooltip=(
                        "The file UsdLiveSyncServer writes on Play. Usually "
                        "<unity project>/UsdSync/base_stage.usda"))
                    self._labeled_field("Mount under", self._m_wrapper, tooltip=(
                        "Wrapper Xform that carries the up-axis / units fix-up. The Unity hierarchy "
                        "is referenced at <this>/<root prim>."))
                    self._labeled_field("Unity root prim", self._m_root_prim, tooltip=(
                        "Must match base_stage.usda's defaultPrim (the name of Unity's syncRoot). A "
                        "reference maps the referenced defaultPrim onto the referencing prim, so this "
                        "name is what makes the streamed prim paths line up."))
                    with ui.HStack(spacing=6, height=24):
                        ui.Button("Mount Unity stage", clicked_fn=self._on_mount, height=24)
                        ui.Button("Clear prim cache", clicked_fn=self._on_invalidate, height=24,
                                  tooltip="Re-resolve prim handles, e.g. after editing the stage by hand.")

                    ui.Separator(height=4)
                    self._section_label("2 — Live connection")
                    with ui.HStack(spacing=6, height=22):
                        ui.Label("Host / port", width=120)
                        ui.StringField(model=self._m_host, height=22)
                        ui.IntField(model=self._m_port, width=80, height=22)
                    self._labeled_field("Prim prefix", self._m_prefix, tooltip=(
                        "Prepended to every wire prim path. Normally identical to 'Mount under'; set "
                        "it empty if the stage IS base_stage.usda opened directly."))
                    with ui.HStack(spacing=6, height=24):
                        ui.Button("Connect", clicked_fn=self._on_connect, height=24)
                        ui.Button("Disconnect", clicked_fn=self._on_disconnect, height=24)

                    ui.Separator(height=4)
                    self._section_label("3 — Commands to Unity")
                    with ui.HStack(spacing=6, height=24):
                        ui.Button("Request snapshot", clicked_fn=self._on_get_snapshot, height=24)
                        ui.Button("Reset Unity scene", clicked_fn=self._on_reset, height=24,
                                  tooltip="Restores every tracked Unity transform to its Play-time baseline.")

                    ui.Separator(height=4)
                    self._section_label("4 — Push back (Isaac -> Unity)")
                    ui.Label(
                        "Comma-separated wire prim paths whose Isaac-side pose is sent back to Unity.\n"
                        "Unity only applies these where UsdSyncNode.AcceptsRemoteWrites is true.",
                        word_wrap=True, height=0)
                    self._labeled_field("Prim paths", self._m_push_back,
                                        tooltip="e.g. /SyncRoot/Box_100x100x100_Prefab_18_")
                    with ui.HStack(spacing=6, height=22):
                        ui.Label("Rate (Hz)", width=120)
                        ui.FloatField(model=self._m_push_hz, width=80, height=22)
                        self._push_button = ui.Button("Enable push back", clicked_fn=self._on_toggle_push,
                                                      height=22)

                    ui.Separator(height=4)
                    self._section_label("Status")
                    self._status_label = ui.Label("idle", word_wrap=True, height=0)
                    self._stats_label = ui.Label("", word_wrap=True, height=0)
                    self._log_label = ui.Label("", word_wrap=True, height=0)

    @staticmethod
    def _section_label(text: str) -> None:
        ui.Label(text, style={"font_size": 15, "color": 0xFF9ED0FF}, height=20)

    @staticmethod
    def _labeled_field(label: str, model, tooltip: str = "") -> None:
        with ui.HStack(spacing=6, height=22):
            ui.Label(label, width=120, tooltip=tooltip)
            ui.StringField(model=model, height=22, tooltip=tooltip)

    # ---- button handlers -------------------------------------------------------------------------

    def _on_mount(self) -> None:
        base = self._m_base_stage.get_value_as_string().strip().strip('"')
        if not base:
            self._log("set the base_stage.usda path first.")
            return
        if not os.path.isfile(base):
            self._log("no such file: {}".format(base))
            return

        stage = omni.usd.get_context().get_stage()
        if stage is None:
            self._log("no stage open — create or open a stage first.")
            return

        wrapper = self._m_wrapper.get_value_as_string().strip() or DEFAULT_WRAPPER_PATH
        root_prim = self._m_root_prim.get_value_as_string().strip() or DEFAULT_ROOT_PRIM
        try:
            prefix = mount_unity_stage(stage, base, root_prim_name=root_prim,
                                       wrapper_path=wrapper, log=self._log)
        except Exception as e:  # noqa: BLE001 - surface the reason in the panel, do not kill the ext
            self._log("mount failed: {}".format(e))
            carb.log_error("[unity.usd.livesync] mount failed: {}".format(e))
            return

        self._m_prefix.set_value(prefix)
        self._ensure_bridge(stage, prefix)
        self._log("mounted. Now press Connect (Unity must be in Play mode).")

    def _on_invalidate(self) -> None:
        if self._bridge is not None:
            self._bridge.invalidate()
            self._log("prim handle cache cleared.")

    def _on_connect(self) -> None:
        if self._client is not None and self._client.running:
            self._log("already connected.")
            return

        stage = omni.usd.get_context().get_stage()
        if stage is None:
            self._log("no stage open.")
            return
        self._ensure_bridge(stage, self._m_prefix.get_value_as_string().strip())

        self._client = SyncClient(host=self._m_host.get_value_as_string().strip() or DEFAULT_HOST,
                                  port=self._m_port.get_value_as_int() or DEFAULT_PORT,
                                  reconnect=True, on_log=self._log)
        self._client.start()
        self._log("connecting…")

    def _on_disconnect(self) -> None:
        self._disconnect()
        self._log("disconnected.")

    def _disconnect(self) -> None:
        if self._client is not None:
            self._client.stop()
            self._client = None
        self._push_back_enabled = False

    def _on_reset(self) -> None:
        if self._client is None or not self._client.send_reset():
            self._log("reset not sent (not connected).")

    def _on_get_snapshot(self) -> None:
        if self._client is None or not self._client.send_get_snapshot():
            self._log("snapshot request not sent (not connected).")

    def _on_toggle_push(self) -> None:
        self._push_back_enabled = not self._push_back_enabled
        self._push_button.text = "Disable push back" if self._push_back_enabled else "Enable push back"
        self._log("push back {}".format("enabled" if self._push_back_enabled else "disabled"))

    def _ensure_bridge(self, stage, prefix: str) -> None:
        if self._bridge is None:
            self._bridge = StageBridge(stage, prim_prefix=prefix, log=self._log)
            return
        if self._bridge.stage is not stage:
            self._bridge.set_stage(stage)
        if self._bridge.prim_prefix != (prefix or "").rstrip("/"):
            self._bridge.set_prim_prefix(prefix)

    # ---- per-frame pump --------------------------------------------------------------------------

    def _subscribe_updates(self) -> None:
        self._update_sub = (omni.kit.app.get_app()
                            .get_update_event_stream()
                            .create_subscription_to_pop(self._on_update, name="unity.usd.livesync.update"))

    def _unsubscribe_updates(self) -> None:
        self._update_sub = None

    def _on_update(self, _event) -> None:
        client = self._client
        if client is not None:
            # Unity -> Isaac: apply whatever arrived since the last frame.
            pending = client.drain()
            if pending and self._bridge is not None:
                self._bridge.apply(pending)

            if client.take_reset_flag():
                self._log("Unity reset — baseline re-broadcast, applied.")

            # Isaac -> Unity: rate-limited pose push for the explicitly listed prims.
            if self._push_back_enabled:
                self._pump_push_back(client)

        # Cheap UI refresh (4 Hz) — the labels do not need to change every frame. Deliberately
        # OUTSIDE the client guard: this is the only place log lines reach the widget, so mount-time
        # and connection-failure messages must still render when no client exists yet.
        now = time.time()
        if now - self._last_ui_refresh >= 0.25:
            self._last_ui_refresh = now
            self._refresh_status()

    def _pump_push_back(self, client) -> None:
        hz = max(0.5, self._m_push_hz.get_value_as_float())
        now = time.time()
        if now - self._push_back_last < 1.0 / hz:
            return
        self._push_back_last = now

        if self._bridge is None:
            return
        paths = [p.strip() for p in self._m_push_back.get_value_as_string().split(",") if p.strip()]
        if not paths:
            return

        payload = {}
        for wire_path in paths:
            trs = self._bridge.read_unity_trs(wire_path)
            if trs is not None:
                payload[wire_path] = trs
        if payload:
            client.send_set_transform(payload)

    # ---- status / logging ------------------------------------------------------------------------

    def _refresh_status(self) -> None:
        """Main thread only (driven from _on_update) — the single place that writes to the widgets."""
        client = self._client
        self._status_label.text = client.status_line() if client is not None else "idle"
        self._stats_label.text = self._bridge.stats_line() if self._bridge is not None else ""

        if self._log_dirty:
            with self._log_lock:
                self._log_dirty = False
                text = "\n".join(self._log_lines)
            self._log_label.text = text

    def _log(self, message: str) -> None:
        """
        Callable from ANY thread — `SyncClient` logs connect/disconnect from its socket thread.

        Kit's UI is not thread-safe, so this only buffers the line and raises a dirty flag; the
        actual `ui.Label` write happens in `_refresh_status` on the update thread. Touching the
        widget directly from here is a crash waiting for a reconnect to happen.
        """
        carb.log_info("[unity.usd.livesync] {}".format(message))
        with self._log_lock:
            self._log_lines.append(message)
            del self._log_lines[:-8]    # keep the last 8 lines only
            self._log_dirty = True
