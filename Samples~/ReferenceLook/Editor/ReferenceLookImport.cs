// Reference Look sample: imports a USD stage into the sample scene, waits for the importer's
// reflection captures, and saves the result as a new scene. The look (URP pipeline, renderer
// features, volume profile and tone LUT) comes from the assets next to this script; the
// importer runs with its default options.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Unity.USDToolkit.Samples.ReferenceLook.Editor
{
    public static class ReferenceLookImport
    {
        const string SceneFile = "ReferenceLook.unity";
        const string PipelineFile = "ReferenceLook_Pipeline.asset";
        const string LutFile = "ReferenceLook_LUT.exr";
        const string CurveFile = "IsaacToneCurve.txt";
        const string ImportedKey = "Unity.USDToolkit.ReferenceLook.ImportedThisSession";
        const string LastSavedKey = "Unity.USDToolkit.ReferenceLook.LastSavedScene";

        // The LUT folds Isaac Sim's tone curve over a scene range of 2^LutRangeEv; the volume's
        // post exposure (-4.2) is the bench exposure (-0.2) minus this range.
        const float LutRangeEv = 4f;
        const int LutSize = 64;

        // Frames to let pass after the import before saving. The importer renders the dome's
        // reflection and three interior reflection passes one frame apart, so a handful of frames
        // covers the chain with margin.
        const int SettleFrames = 12;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The scene path written by the last import in this editor session, or null.</summary>
        public static string LastSavedScene => SessionState.GetString(LastSavedKey, null);

        [MenuItem("USD Toolkit/Import into Reference Look scene...", false, 10)]
        static void ImportMenu()
        {
            string usdPath = EditorUtility.OpenFilePanel("Import USD into the Reference Look scene", "", "usd,usda,usdc,usdz");
            if (string.IsNullOrEmpty(usdPath))
            {
                return;
            }

            bool allowExternal = EditorUtility.DisplayDialog("Reference Look import",
                "Allow the stage to reference textures and layers outside its own folder (UsdImportOptions.AllowExternalAssetPaths)?\n\n" +
                "Choose No for stages from untrusted sources.", "Allow", "No");

            string folder = Path.GetDirectoryName(SamplePath(SceneFile)).Replace('\\', '/');
            string suggested = "ReferenceLook_" + Path.GetFileNameWithoutExtension(usdPath);
            string savePath = EditorUtility.SaveFilePanelInProject("Save the imported scene as", suggested, "unity",
                "The sample scene stays as it is; the import is saved to this new scene.", folder);
            if (string.IsNullOrEmpty(savePath))
            {
                return;
            }

            Import(usdPath, allowExternal, savePath, interactive: true);
        }

        [MenuItem("USD Toolkit/Reference Look/Assign render pipeline", false, 30)]
        static void AssignPipelineMenu() => AssignPipeline(LoadSample<RenderPipelineAsset>(PipelineFile));

        [MenuItem("USD Toolkit/Reference Look/Refresh reflection", false, 31)]
        static void RefreshReflectionMenu()
        {
            GameObject root = FindImportRoot();
            if (root == null)
            {
                EditorUtility.DisplayDialog("Reference Look", "No imported USD stage in the open scene (no UsdStageSource found).", "OK");
                return;
            }

            RefreshReflection(root);
        }

        [MenuItem("USD Toolkit/Reference Look/Rebuild LUT from tone curve", false, 32)]
        static void RebuildLutMenu()
        {
            string lutPath = SamplePath(LutFile);
            string curvePath = SamplePath(CurveFile);
            if (curvePath == null)
            {
                Debug.LogError("[ReferenceLook] " + CurveFile + " not found in the sample folder.");
                return;
            }

            if (lutPath == null)
            {
                lutPath = Path.Combine(Path.GetDirectoryName(curvePath), LutFile).Replace('\\', '/');
            }

            WriteLut(curvePath, lutPath);
            Debug.Log("[ReferenceLook] LUT written to " + lutPath);
        }

        /// <summary>
        /// Opens the sample scene, imports <paramref name="usdPath"/> with the importer's defaults,
        /// lets the reflection captures finish and saves the scene to <paramref name="savePath"/>
        /// (project-relative, under Assets). With <paramref name="interactive"/> the pipeline
        /// assignment and the second-import limitation are confirmed in dialogs; without it they
        /// are applied and logged.
        /// </summary>
        public static void Import(string usdPath, bool allowExternalAssetPaths, string savePath, bool interactive)
        {
            if (!File.Exists(usdPath))
            {
                throw new FileNotFoundException("USD file not found", usdPath);
            }

            string scenePath = SamplePath(SceneFile);
            if (scenePath == null)
            {
                throw new InvalidOperationException("The Reference Look sample scene was not found. Import the sample from the Package Manager first.");
            }

            if (string.IsNullOrEmpty(savePath))
            {
                savePath = Path.Combine(Path.GetDirectoryName(scenePath), "ReferenceLook_" + Path.GetFileNameWithoutExtension(usdPath) + ".unity").Replace('\\', '/');
            }

            var pipeline = LoadSample<RenderPipelineAsset>(PipelineFile);
            if (pipeline != null && GraphicsSettings.defaultRenderPipeline != pipeline)
            {
                bool assign = true;
                if (interactive)
                {
                    int choice = EditorUtility.DisplayDialogComplex("Reference Look import",
                        "The project does not use the Reference Look render pipeline asset. The look needs it " +
                        "(Deferred, Surface Cache GI, screen-space reflections, 4096 shadow maps).\n\n" +
                        "Assign it to Graphics and every Quality level? This changes the project settings.",
                        "Assign", "Cancel", "Keep current pipeline");
                    if (choice == 1)
                    {
                        return;
                    }

                    assign = choice == 0;
                }

                if (assign)
                {
                    AssignPipeline(pipeline);
                }
            }

            if (SessionState.GetBool(ImportedKey, false))
            {
                const string message = "A USD stage was already imported in this editor session. Surface Cache GI can " +
                    "leave the second import's geometry out of its acceleration structure (\"An item with the same key has " +
                    "already been added\"), so indirect light and sky occlusion may be missing. Restart the editor for a clean import.";
                if (interactive && !EditorUtility.DisplayDialog("Reference Look import", message + "\n\nContinue anyway?", "Continue", "Cancel"))
                {
                    return;
                }

                Debug.LogWarning("[ReferenceLook] " + message);
            }

            if (interactive && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            var options = new UsdImportOptions
            {
                AllowExternalAssetPaths = allowExternalAssetPaths,
                ProgressCallback = (progress, message) => EditorUtility.DisplayProgressBar("Reference Look import", message, progress),
            };

            UsdImportResult result;
            try
            {
                result = UsdImporter.Import(usdPath, options);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            string fit = FitToStage(result.RootObject);
            SessionState.SetBool(ImportedKey, true);
            SessionState.EraseString(LastSavedKey);
            Debug.Log("[ReferenceLook] fitted to the stage: " + fit);
            Debug.Log("[ReferenceLook] imported " + usdPath + ": " + result);

            // Wait for the importer's deferred captures (dome reflection, interior reflection) and
            // then save. Each editor tick asks for a player-loop update and a scene-view repaint,
            // which is what the importer's own deferral keys on; if nothing is drawn (no visible
            // Scene or Game view) the scene camera is rendered to an offscreen target instead.
            int frames = 0, ticks = 0, lastFrames = 0, stalledTicks = 0;
            Camera camera = Camera.main;
            RenderTexture target = null;
            void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras) => frames++;
            void Tick()
            {
                ticks++;
                stalledTicks = frames == lastFrames ? stalledTicks + 1 : 0;
                lastFrames = frames;
                if (frames >= SettleFrames && ticks >= SettleFrames)
                {
                    EditorApplication.update -= Tick;
                    RenderPipelineManager.endContextRendering -= OnEndContextRendering;
                    if (target != null)
                    {
                        camera.targetTexture = null;
                        target.Release();
                        UnityEngine.Object.DestroyImmediate(target);
                    }

                    EditorSceneManager.MarkSceneDirty(scene);
                    PersistFitProfile(scene, savePath);
                    bool saved = EditorSceneManager.SaveScene(scene, savePath);
                    if (saved)
                    {
                        SessionState.SetString(LastSavedKey, savePath);
                    }

                    string reflection = RenderSettings.customReflectionTexture != null ? RenderSettings.customReflectionTexture.name : "none";
                    Debug.Log("[ReferenceLook] " + (saved ? "saved " + savePath : "save failed: " + savePath) +
                        " (frames=" + frames + ", reflection=" + reflection + ")");
                    return;
                }

                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
                // Nothing drew a frame in the last ten ticks (unfocused editor, no visible view):
                // drive one ourselves.
                if (stalledTicks >= 10 && camera != null)
                {
                    if (target == null)
                    {
                        target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                        camera.targetTexture = target;
                    }

                    camera.Render();
                }
            }

            RenderPipelineManager.endContextRendering += OnEndContextRendering;
            EditorApplication.update += Tick;
        }

        /// <summary>
        /// Fits the shadow distance and the Surface Cache GI volume to the imported stage: the
        /// shadow distance is 2.1 x the stage's bounding diagonal (at least 10 m) and the GI volume
        /// the next power of two above 1.2 x the diagonal (at least 32 m). On NVIDIA's Factory
        /// (72 m) that is the 150 m and 128 m it was matched with; a table-top stage gets 32 m,
        /// fine enough that the light bounced off the floor fills the shadows, and a stage larger
        /// than the defaults is not left unshadowed or unlit at its far end.
        /// The GI value goes on its own global volume (<see cref="FitVolumeName"/>, one priority
        /// above the sample's) whose profile overrides only the volume size, so the sample's
        /// profile asset is not edited and later changes to it still apply. <see cref="Import"/>
        /// saves that profile beside the scene, so the value survives reopening the scene. The
        /// shadow distance is a pipeline-asset setting; it is applied again from the stage's
        /// bounds whenever a scene with the fit volume is opened.
        /// </summary>
        public static string FitToStage(GameObject root)
        {
            if (!TryStageDiagonal(root, out float diagonal))
            {
                return root == null ? "no stage" : "no renderers";
            }

            float shadowDistance = ShadowDistanceFor(diagonal);
            float volumeSize = GiVolumeFor(diagonal);
            TrackStage(root);

            if (GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp)
            {
                urp.shadowDistance = shadowDistance;
            }

            string gi = FitGiVolume(root.scene, volumeSize) ? "" : " (no Surface Cache GI volume to fit)";
            return "diagonal " + diagonal.ToString("F1", CultureInfo.InvariantCulture) + " m, shadow distance " +
                   shadowDistance.ToString("F0", CultureInfo.InvariantCulture) + " m, GI volume " +
                   volumeSize.ToString("F0", CultureInfo.InvariantCulture) + " m" + gi;
        }

        /// <summary>The global volume FitToStage adds for the stage-fitted GI volume size.</summary>
        public const string FitVolumeName = "ReferenceLook Stage Fit";

        static float ShadowDistanceFor(float diagonal) => Mathf.Max(10f, Mathf.Ceil(diagonal * 2.1f));

        static float GiVolumeFor(float diagonal) => Mathf.Max(32f, Mathf.NextPowerOfTwo(Mathf.CeilToInt(diagonal * 1.2f)));

        static bool TryStageDiagonal(GameObject root, out float diagonal)
        {
            bool any = TryStageBounds(root, out Bounds bounds);
            diagonal = any ? bounds.size.magnitude : 0f;
            return any;
        }

        static bool TryStageBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            if (root == null)
            {
                return false;
            }

            bool any = false;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled)
                {
                    continue;
                }

                if (any) bounds.Encapsulate(renderer.bounds); else { bounds = renderer.bounds; any = true; }
            }

            return any;
        }

        // The Surface Cache GI volume and the shadow distance are both centred on the camera, so a
        // stage-sized fit only holds while the camera stands near the stage. Framed from further
        // out (a vehicle authored in centimetres and seen from 1.2 km), the stage fell outside the
        // GI volume and its diffuse lighting went to black, and its far end out of the shadows.
        // While a fitted scene is open and the Game camera (Camera.main) stands outside the stage,
        // both are grown so they reach the stage's farthest corner from it, and fall back to the
        // stage fit when the camera comes closer or goes inside. The Scene view camera is left out: the volume is one global value, and following
        // wherever the Scene view was left would change the Game view's lighting with it. The GI
        // volume moves in powers of two, so it changes only when the camera crosses a step.
        static Scene s_stageScene;
        static Bounds s_stageBounds;
        static float s_stageDiagonal;
        static Volume s_fitVolume;

        static void TrackStage(GameObject root)
        {
            s_fitVolume = null;
            if (TryStageBounds(root, out Bounds bounds))
            {
                s_stageScene = root.scene;
                s_stageBounds = bounds;
                s_stageDiagonal = bounds.size.magnitude;
            }
            else
            {
                s_stageScene = default;
            }
        }

        static float FarthestCorner(Bounds bounds, Vector3 from)
        {
            Vector3 d = Vector3.Max(bounds.max - from, from - bounds.min);
            return d.magnitude;
        }

        [InitializeOnLoadMethod]
        static void KeepStageInsideCameraRanges()
        {
            EditorApplication.update += () =>
            {
                if (!s_stageScene.IsValid() || !s_stageScene.isLoaded)
                {
                    return;
                }

                Camera game = Camera.main;
                if (game == null || game.gameObject.scene != s_stageScene)
                {
                    return;
                }

                // Only a camera outside the stage is grown for. Inside one (an interior), the stage
                // fit already covers what the camera sees, and growing the volume coarsened the GI
                // and restarted it mid-view: the warehouse aisle came out 5 x too bright (10-20).
                Vector3 eye = game.transform.position;
                float reach = s_stageBounds.Contains(eye) ? 0f : FarthestCorner(s_stageBounds, eye);

                if (s_fitVolume == null)
                {
                    s_fitVolume = FindFitVolume(s_stageScene);
                }

                float volumeSize = Mathf.Max(GiVolumeFor(s_stageDiagonal), Mathf.NextPowerOfTwo(Mathf.CeilToInt(2f * reach)));
                VolumeProfile profile = s_fitVolume != null ? s_fitVolume.sharedProfile : null;
                if (profile != null)
                {
                    foreach (VolumeComponent component in profile.components)
                    {
                        if (component != null && component.GetType().GetField("volumeSize")?.GetValue(component) is MinFloatParameter size &&
                            size.overrideState && !Mathf.Approximately(size.value, volumeSize))
                        {
                            size.value = volumeSize;
                        }
                    }
                }

                float shadowDistance = Mathf.Max(ShadowDistanceFor(s_stageDiagonal), Mathf.Ceil(reach));
                if (GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp &&
                    !Mathf.Approximately(urp.shadowDistance, shadowDistance))
                {
                    urp.shadowDistance = shadowDistance;
                }
            };
        }

        static Volume FindFitVolume(Scene scene)
        {
            foreach (Volume volume in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (volume.gameObject.scene == scene && volume.gameObject.name == FitVolumeName)
                {
                    return volume;
                }
            }

            return null;
        }

        // Puts the GI volume size on the fit volume's own profile: a Surface Cache GI component of
        // the sample profile's type with only volumeSize overridden. False when the scene has no
        // global volume with Surface Cache GI (another pipeline, an older URP).
        static bool FitGiVolume(Scene scene, float volumeSize)
        {
            Volume main = null;
            VolumeComponent source = null;
            foreach (Volume volume in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (volume.gameObject.scene != scene || !volume.isGlobal || volume.sharedProfile == null || volume.gameObject.name == FitVolumeName)
                {
                    continue;
                }

                source = volume.sharedProfile.components.FirstOrDefault(c => c != null && c.GetType().Name == "SurfaceCacheGIVolumeOverride");
                if (source != null)
                {
                    main = volume;
                    break;
                }
            }

            if (main == null)
            {
                return false;
            }

            Volume fit = FindFitVolume(scene);
            if (fit == null)
            {
                var fitObject = new GameObject(FitVolumeName);
                SceneManager.MoveGameObjectToScene(fitObject, scene);
                fit = fitObject.AddComponent<Volume>();
            }

            fit.isGlobal = true;
            fit.priority = main.priority + 1f;
            VolumeProfile profile = fit.sharedProfile;
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = FitVolumeName;
                fit.sharedProfile = profile;
            }

            VolumeComponent component = profile.components.FirstOrDefault(c => c != null && c.GetType() == source.GetType())
                ?? profile.Add(source.GetType(), false);
            component.active = true;
            if (component.GetType().GetField("volumeSize")?.GetValue(component) is MinFloatParameter size)
            {
                size.overrideState = true;
                size.value = volumeSize;
            }

            EditorUtility.SetDirty(profile);
            EditorUtility.SetDirty(fit);
            return true;
        }

        // Saves the fit volume's profile as an asset beside the scene, before the scene itself is
        // saved: a profile that is not an asset is not written into the scene, and the volume
        // would come back empty (and the GI volume at the sample asset's size) on reopening.
        static void PersistFitProfile(Scene scene, string scenePath)
        {
            Volume fit = FindFitVolume(scene);
            if (fit == null || fit.sharedProfile == null || AssetDatabase.Contains(fit.sharedProfile))
            {
                return;
            }

            VolumeProfile profile = fit.sharedProfile;
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(Path.GetDirectoryName(scenePath), Path.GetFileNameWithoutExtension(scenePath) + "_StageFit.asset").Replace('\\', '/'));
            AssetDatabase.CreateAsset(profile, assetPath);
            foreach (VolumeComponent component in profile.components)
            {
                if (component != null && !AssetDatabase.Contains(component))
                {
                    component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                    AssetDatabase.AddObjectToAsset(component, profile);
                }
            }

            AssetDatabase.SaveAssets();
        }

        // The shadow distance lives on the pipeline asset, which every scene shares, so it is
        // applied again from the stage's bounds when a scene FitToStage set up is opened.
        [InitializeOnLoadMethod]
        static void ReapplyShadowDistanceOnOpen()
        {
            EditorSceneManager.sceneOpened += (scene, mode) =>
            {
                if (FindFitVolume(scene) == null ||
                    !(GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp))
                {
                    return;
                }

                GameObject root = FindImportRoot();
                if (root != null && root.scene == scene && TryStageDiagonal(root, out float diagonal))
                {
                    urp.shadowDistance = ShadowDistanceFor(diagonal);
                    TrackStage(root);
                }
            };
        }

        /// <summary>Makes the sample pipeline asset the default for Graphics and every Quality level.</summary>
        public static void AssignPipeline(RenderPipelineAsset pipeline)
        {
            if (pipeline == null)
            {
                Debug.LogError("[ReferenceLook] " + PipelineFile + " not found in the sample folder.");
                return;
            }

            GraphicsSettings.defaultRenderPipeline = pipeline;
            int level = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.count; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
            }

            QualitySettings.SetQualityLevel(level, false);
            KeepBatchRendererGroupVariants();
            AssetDatabase.SaveAssets();
            Debug.Log("[ReferenceLook] render pipeline assigned: " + pipeline.name);
        }

        // The pipeline turns on the GPU Resident Drawer, which URP only starts when the project keeps
        // the BatchRendererGroup shader variants (Project Settings > Graphics > BatchRendererGroup
        // Variants > Keep All). The default strips them unless Entities Graphics is installed, and
        // the drawer then stays off without a message. EditorGraphicsSettings exposes the mode
        // read-only, so it is set through the settings asset.
        static void KeepBatchRendererGroupVariants()
        {
            if (EditorGraphicsSettings.batchRendererGroupShaderStrippingMode == BatchRendererGroupStrippingMode.KeepAll)
            {
                return;
            }

            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            SerializedProperty mode = settings.FindProperty("m_BrgStripping");
            if (mode == null)
            {
                Debug.LogWarning("[ReferenceLook] set Project Settings > Graphics > BatchRendererGroup Variants to Keep All for the GPU Resident Drawer.");
                return;
            }

            mode.enumValueIndex = Array.FindIndex(mode.enumNames, n => n.Replace(" ", "") == "KeepAll");
            settings.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[ReferenceLook] BatchRendererGroup variants set to Keep All (needed by the GPU Resident Drawer).");
        }

        /// <summary>
        /// Re-captures the interior reflection on three consecutive frames (the capture is a render
        /// texture, so a reopened scene has none until this runs).
        /// </summary>
        public static void RefreshReflection(GameObject root)
        {
            int passes = 0;
            void Tick()
            {
                if (root == null || passes >= 3)
                {
                    EditorApplication.update -= Tick;
                    return;
                }

                UsdImporter.RefreshReflection(root);
                passes++;
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }

            EditorApplication.update += Tick;
        }

        /// <summary>The root object of the first imported stage in the open scenes, or null.</summary>
        public static GameObject FindImportRoot()
        {
            var source = UnityEngine.Object.FindAnyObjectByType<UsdStageSource>();
            return source != null ? source.gameObject : null;
        }

        // ------------------------------------------------------------------ LUT

        /// <summary>
        /// Writes the colour lookup texture from the tone curve: a 64-step LUT (width 64 x 64,
        /// RGBA half, linear) that maps scene values in [0, 2^LutRangeEv] through the curve and
        /// encodes them in sRGB, the form URP's Color Lookup applies after its own grading.
        /// </summary>
        public static void WriteLut(string curvePath, string lutAssetPath)
        {
            Texture2D lut = BuildCurveLut(curvePath, Mathf.Pow(2f, LutRangeEv));
            try
            {
                File.WriteAllBytes(lutAssetPath, lut.EncodeToEXR(Texture2D.EXRFlags.CompressZIP));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(lut);
            }

            AssetDatabase.ImportAsset(lutAssetPath, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(lutAssetPath) is TextureImporter importer)
            {
                importer.sRGBTexture = false;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.maxTextureSize = 8192;
                importer.SaveAndReimport();
            }
        }

        // curve file: "E out" per line; out is the linear display value Isaac Sim produced for
        // scene value E. Interpolated in log E, linear out; the LUT stores sRGB-encoded output.
        static Texture2D BuildCurveLut(string curvePath, float range)
        {
            double[][] pts = File.ReadAllLines(curvePath)
                .Where(l => l.Trim().Length > 0 && !l.StartsWith("#"))
                .Select(l => l.Split(' ', '\t').Where(x => x.Length > 0).Select(x => double.Parse(x, Inv)).ToArray())
                .OrderBy(a => a[0]).ToArray();

            double Curve(double e)
            {
                if (e <= pts[0][0])
                {
                    return pts[0][1] * e / pts[0][0];
                }

                for (int i = 1; i < pts.Length; i++)
                {
                    if (e <= pts[i][0])
                    {
                        double t = (Math.Log(e) - Math.Log(pts[i - 1][0])) / (Math.Log(pts[i][0]) - Math.Log(pts[i - 1][0]));
                        return pts[i - 1][1] + t * (pts[i][1] - pts[i - 1][1]);
                    }
                }

                return pts[pts.Length - 1][1];
            }

            var table = new float[LutSize];
            for (int i = 0; i < LutSize; i++)
            {
                float u = i / (float)(LutSize - 1);
                double x = Mathf.GammaToLinearSpace(u) * range;
                table[i] = Mathf.LinearToGammaSpace((float)Math.Min(1.0, Math.Max(0.0, Curve(x))));
            }

            var tex = new Texture2D(LutSize * LutSize, LutSize, TextureFormat.RGBAHalf, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color[LutSize * LutSize * LutSize];
            for (int b = 0; b < LutSize; b++)
            {
                for (int g = 0; g < LutSize; g++)
                {
                    for (int r = 0; r < LutSize; r++)
                    {
                        px[g * LutSize * LutSize + b * LutSize + r] = new Color(table[r], table[g], table[b], 1f);
                    }
                }
            }

            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        // ------------------------------------------------------------------ sample assets

        // The sample's files, found by name wherever the Package Manager copied the sample.
        static string SamplePath(string fileName)
        {
            string filter = Path.GetFileNameWithoutExtension(fileName);
            foreach (string guid in AssetDatabase.FindAssets(filter))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return null;
        }

        static T LoadSample<T>(string fileName) where T : UnityEngine.Object
        {
            string path = SamplePath(fileName);
            return path != null ? AssetDatabase.LoadAssetAtPath<T>(path) : null;
        }
    }
}
