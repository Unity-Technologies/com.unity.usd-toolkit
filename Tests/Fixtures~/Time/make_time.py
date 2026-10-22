"""Write the time-metadata fixtures, each as a text .usda and as a binary .usdc of the same layer.

    python3 -I make_time.py      (usd-core; writes next to this script)

  range_24     startTimeCode 0, endTimeCode 48, timeCodesPerSecond 24 and framesPerSecond 24
  fps12_only   startTimeCode -6 and endTimeCode 30, and only framesPerSecond 12: the time codes per
               second fall back to 12
  start_only   startTimeCode 10 and timeCodesPerSecond 30 with no endTimeCode: no time range
  no_time      no time metadata: no time range, and both rates the default 24

A fixture with a range has one Xform whose translate is sampled at its start and end.
"""

import os

from pxr import Gf, Usd, UsdGeom

FIXTURES = {
    "range_24": {"startTimeCode": 0.0, "endTimeCode": 48.0, "timeCodesPerSecond": 24.0, "framesPerSecond": 24.0},
    "fps12_only": {"startTimeCode": -6.0, "endTimeCode": 30.0, "framesPerSecond": 12.0},
    "start_only": {"startTimeCode": 10.0, "timeCodesPerSecond": 30.0},
    "no_time": {},
}


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    for name, metadata in FIXTURES.items():
        text = os.path.join(here, name + ".usda")
        stage = Usd.Stage.CreateNew(text)
        layer = stage.GetRootLayer()
        layer.documentation = "Time-metadata fixture %s, written by make_time.py." % name
        stage.SetMetadata("metersPerUnit", 1.0)
        stage.SetMetadata("upAxis", "Y")
        root = UsdGeom.Xform.Define(stage, "/Root")
        stage.SetDefaultPrim(root.GetPrim())
        if metadata.get("endTimeCode") is not None:
            translate = root.AddTranslateOp()
            translate.Set(Gf.Vec3d(0, 0, 0), metadata["startTimeCode"])
            translate.Set(Gf.Vec3d(1, 0, 0), metadata["endTimeCode"])
        for key, value in metadata.items():
            stage.SetMetadata(key, value)
        layer.Save()
        layer.Export(os.path.join(here, name + ".usdc"), args={"format": "usdc"})


if __name__ == "__main__":
    main()
