using System;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEngine;

namespace Unity.USDToolkit.Editor
{
    // The Recorder's side of a USD Animation recording: one UsdAnimationRecorder per session,
    // a capture per recorded frame, the stage written when the session ends.
    internal sealed class UsdRecorder : GenericRecorder<UsdRecorderSettings>
    {
        private UsdAnimationRecorder recording;
        private double lastTime;

        protected override bool BeginRecording(RecordingSession session)
        {
            if (!base.BeginRecording(session))
            {
                return false;
            }

            GameObject target = Settings.Input.GameObject;
            if (target == null)
            {
                ConsoleLogMessage("Unity USD Toolkit: the USD Animation recorder has no GameObject to record.", LogType.Error);
                Recording = false;
                return false;
            }

            recording = new UsdAnimationRecorder(target, Settings.FrameRate);
            return true;
        }

        protected override void RecordFrame(RecordingSession session)
        {
            if (recording == null)
            {
                return;
            }

            // At a constant rate, frame n is at n / rate exactly, so time codes stay whole frames;
            // otherwise each frame is at the time the Recorder reports.
            double time = Settings.FrameRatePlayback == FrameRatePlayback.Constant
                ? recording.SampleCount / (double)Settings.FrameRate
                : session.recorderTime;
            if (recording.SampleCount > 0 && !(time > lastTime))
            {
                return;
            }

            recording.Capture(time);
            lastTime = time;
        }

        protected override void EndRecording(RecordingSession session)
        {
            if (Recording && recording != null && recording.SampleCount > 0)
            {
                try
                {
                    Settings.FileNameGenerator.CreateDirectory(session);
                    string path = FileNameGenerator.SanitizePath(Settings.FileNameGenerator.BuildAbsolutePath(session));
                    recording.Save(path, new UsdExportOptions
                    {
                        ExportTextures = Settings.ExportTextures,
                        Composition = Settings.OverridesOnly ? UsdCompositionMode.Preserve : UsdCompositionMode.Flatten
                    });
                    ImportIfInAssets(path);
                }
                catch (Exception exception)
                {
                    ConsoleLogMessage("Unity USD Toolkit: the USD Animation recording could not be written: " + exception.Message,
                                      LogType.Error);
                }
            }

            else if (Recording && recording != null)
            {
                ConsoleLogMessage("Unity USD Toolkit: the USD Animation recording captured no frame, so nothing was written.",
                                  LogType.Warning);
            }

            recording = null;
            base.EndRecording(session);
        }

        private static void ImportIfInAssets(string path)
        {
            string assets = FileNameGenerator.SanitizePath(Application.dataPath);
            if (path.StartsWith(assets + "/", StringComparison.Ordinal))
            {
                AssetDatabase.ImportAsset("Assets" + path.Substring(assets.Length), ImportAssetOptions.ForceUpdate);
            }
        }
    }
}
