"""
Unity USD Live Sync — Isaac Sim side.

`core` is stdlib-only and always safe to import. `stage_bridge` needs `pxr`, and `extension` needs
`omni.ui`/`omni.usd`, so neither is a hard import here:

  * Kit loads this package via the [[python.module]] entry in config/extension.toml and scans it for
    an `omni.ext.IExt` subclass, so `UnityLiveSyncExtension` has to be reachable from this namespace
    — hence the import below.
  * The standalone script (`isaac_live_sync_standalone.py`) imports `…livesync.stage_bridge`, which
    executes this file too. In a headless SimulationApp run `omni.ui` may not be loaded at all, and a
    hard import would break a path that has no use for the UI.

So the extension import is guarded — but NOT silently. A swallowed ImportError here is invisible in
the worst possible way: Kit finds no `IExt`, no panel appears, and nothing is logged, which looks
exactly like "the extension did nothing". The guard therefore reports what failed and why.
"""

from .core import (  # noqa: F401
    DEFAULT_HOST,
    DEFAULT_PORT,
    SyncClient,
    unity_to_usd_quat,
    unity_to_usd_translate,
    usd_to_unity_quat,
    usd_to_unity_translate,
)

UnityLiveSyncExtension = None
EXTENSION_IMPORT_ERROR = None

try:
    from .extension import UnityLiveSyncExtension  # noqa: F401
except ImportError as exc:                      # no omni.ui / no pxr -> standalone or headless run
    EXTENSION_IMPORT_ERROR = exc
    import os
    import sys

    # Only shout when we are plainly inside Kit (where the panel IS the point). The standalone
    # script legitimately hits this path and must stay quiet.
    if "omni.kit.app" in sys.modules or os.environ.get("CARB_APP_PATH"):
        message = ("[unity.usd.livesync] UI extension unavailable: {}: {}. The live-sync panel will "
                   "NOT appear. Everything else (transport, stage bridge) is unaffected."
                   .format(type(exc).__name__, exc))
        try:
            import carb

            carb.log_error(message)
        except ImportError:
            pass
        print(message, file=sys.stderr, flush=True)
