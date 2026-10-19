using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.USDToolkit;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// Operator front end of the AGV fleet demo.
    ///
    ///  * Picks the environment (<see cref="IFleetEnvironment"/>: the NVIDIA factory or the generated
    ///    layout, see <see cref="EnvironmentSelector"/>) and instantiates it with the USD Toolkit.
    ///  * Picks who simulates the fleet (<see cref="IFleetBackend"/>: Isaac Sim over the hub, or the
    ///    local fleet) and shows its state stream: AGV poses, nav state, paths, assignments.
    ///  * Writes its own layer (unity_ops.usda) for obstacles and the goal, via <see cref="UnityOpsLayer"/>.
    ///  * Axis/unit conversion only through <see cref="UsdFrame"/>.
    /// </summary>
    public sealed partial class AgvFleetScene : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("Environment, asset pack path, simulation mode, hub address.")]
        [SerializeField] private AgvFleetSettings settings;

        [Header("Stage files")]
        [SerializeField] private string envAnimName = "env_anim.json";
        [SerializeField] private string unityLayerName = "unity_ops.usda";

        [Header("Shaders kept in player builds")]
        [Tooltip("URP/Lit materials covering the keyword sets runtime-created materials need (opaque, normal map, " +
                 "metallic map, transparent). A player build only contains shaders/variants that included materials " +
                 "use -- without these, Shader.Find (ours and the usd-toolkit importer's) returns null.")]
        [SerializeField] private Material[] shaderKeepAlive;
        [SerializeField] private Material lineMaterial;

        [Header("View")]
        [SerializeField] private Camera viewCamera;
        [SerializeField] private bool showLatencyPanel = true;

        // ---- runtime ----------------------------------------------------------------------------
        private IFleetEnvironment environment;
        private IFleetBackend backend;
        private UnityOpsLayer ops;
        private LatencyMonitor latency;
        private Transform stageRoot;      // Y-up wrapper for imported USD
        private Transform dynamicRoot;    // AGVs, paths, obstacles, goal (Unity frame)
        private GameObject agvTemplate;
        private bool agvTemplateFromUsd;  // imported (importer basis) rather than the primitive model
        private bool importing;
        private GeneratedFactoryParams generatorParams;   // runtime copy: Regenerate changes the seed, not the asset
        private ReflectionProbe hallProbe;
        private string importStatus = "not started";
        private readonly Dictionary<string, AgvView> agvs = new Dictionary<string, AgvView>();
        private readonly Dictionary<string, GameObject> obstacleViews = new Dictionary<string, GameObject>();
        private GameObject goalMarker;
        private Bounds floorBounds;       // imported environment extent: clicks outside are ignored
        private Material lineMat;
        private HubMsg lastHello;
        private HubMsg lastFleet;
        private double lastStateRx;
        private int lastStateSeq;
        private int missedStates;
        private readonly List<string> eventLines = new List<string>();
        private AgvView selected;
        private EnvAnimation envAnim;     // environment's own USD animation, driven by Isaac's timeline
        private double animTc;            // last time code from the hub, and when it arrived
        private float animRxTime = -1f;
        private double animRate;          // time codes per second, measured from the stream

        /// <summary>The project folder in the editor; the executable's folder in a player.</summary>
        private static string ProjectDir => Directory.GetParent(Application.dataPath).FullName;

        /// <summary>
        /// AGV_FLEET_STAGE_DIR wins (set it for a player build; Tools~/agv_fleet honours it too); otherwise the
        /// settings' folder relative to the project (editor) or to the player executable's folder.
        /// </summary>
        private string StageDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable(AgvFleetSettings.StageDirVariable);
                return !string.IsNullOrEmpty(env) ? env : Path.Combine(ProjectDir, settings.stageDir);
            }
        }

        // ---- lifecycle --------------------------------------------------------------------------
        private void Awake()
        {
            if (!settings)
            {
                Debug.LogWarning("[AGV] no AGV Fleet Settings assigned; using defaults");
                settings = ScriptableObject.CreateInstance<AgvFleetSettings>();
            }
            if (!viewCamera) viewCamera = Camera.main;
            if (shaderKeepAlive != null && shaderKeepAlive.Length > 0 && shaderKeepAlive[0]) AgvView.LitShader = shaderKeepAlive[0].shader;
            lineMat = lineMaterial ? new Material(lineMaterial) : new Material(Shader.Find("Sprites/Default"));
            stageRoot = new GameObject("USD Stage").transform;
            stageRoot.SetParent(transform, false);
            stageRoot.localRotation = UsdFrame.ImportedSubtreeRotation;
            dynamicRoot = new GameObject("Live (fleet + Unity layer)").transform;
            dynamicRoot.SetParent(transform, false);

            generatorParams = settings.generator.Clone();
            environment = SelectEnvironment();
            WriteEnvironmentFile();
            latency = new LatencyMonitor(Path.Combine(StageDir, "logs"));
            ops = new UnityOpsLayer(Path.Combine(StageDir, unityLayerName));
            ops.LoadOrCreate(environment.Identity);
            foreach (ObstacleRecord o in ops.Obstacles) CreateObstacleView(o);
            backend = CreateBackend();
            Log($"{environment.Name} environment · {backend.DisplayName}");
            SetupCamera();
        }

        private IFleetEnvironment SelectEnvironment()
        {
            string packDir = EnvironmentSelector.ResolvePackDir(settings.assetPackPath,
                Environment.GetEnvironmentVariable(AgvFleetSettings.AssetPackPathVariable), ProjectDir);
            string factoryUsd = EnvironmentSelector.FactoryUsdPath(packDir, settings.factoryUsdInPack);
            EnvironmentChoice choice = EnvironmentSelector.Choose(settings.environment, factoryUsd, File.Exists);
            if (choice.Message != null)
            {
                if (choice.MessageIsError) Debug.LogError(choice.Message);
                else Debug.Log(choice.Message);
            }
            return choice.Kind == EnvironmentKind.Factory
                ? new FactoryEnvironmentProvider(choice.FactoryUsdPath, settings.factoryLayout, StageDir)
                : new GeneratedEnvironmentProvider(generatorParams, StageDir);
        }

        private IFleetBackend CreateBackend()
        {
            switch (settings.simulation)
            {
                case FleetSimulationMode.Local: return new LocalFleetBackend();
                case FleetSimulationMode.IsaacSim: return new IsaacHubBackend(settings.hubHost, settings.hubPort, StageDir);
                default: return new AutoFleetBackend(settings.hubHost, settings.hubPort, StageDir);
            }
        }

        /// <summary>
        /// Tells Isaac Sim what this scene runs (environment.json; the generated hall's USD is already in the
        /// stage folder). Isaac reads it at launch, so a running Isaac keeps its environment until restarted.
        /// </summary>
        private void WriteEnvironmentFile()
        {
            try
            {
                if (environment is GeneratedEnvironmentProvider generated) generated.WriteUsd();
                string path = EnvironmentFile.Write(environment, StageDir);
                Debug.Log($"[AGV] {environment.Identity} -> {path}");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[AGV] could not write the environment description for Isaac Sim: {e.Message}");
            }
        }

        /// <summary>Regenerate needs a generated hall and no Isaac Sim driving it (Isaac loads environments at launch).</summary>
        private bool CanRegenerate =>
            environment is GeneratedEnvironmentProvider && !importing && !IsaacDriving;

        private bool IsaacDriving => backend is IsaacHubBackend isaac ? isaac.Connected
            : backend is AutoFleetBackend auto && auto.IsaacActive;

        /// <summary>
        /// A new generated hall (next seed): replaces the USD file, environment.json and the imported hall,
        /// drops the obstacles (they belonged to the old layout) and restarts the fleet on the new layout.
        /// Isaac Sim picks it up on its next launch.
        /// </summary>
        private async void Regenerate()
        {
            if (!CanRegenerate) return;
            generatorParams.seed++;
            foreach (AgvView v in agvs.Values) Destroy(v.gameObject);
            agvs.Clear();
            Select(null);
            foreach (GameObject go in obstacleViews.Values) Destroy(go);
            obstacleViews.Clear();
            for (int i = stageRoot.childCount - 1; i >= 0; i--) Destroy(stageRoot.GetChild(i).gameObject);
            envAnim = null;
            dedupe.Clear();
            environment = new GeneratedEnvironmentProvider(generatorParams, StageDir);
            WriteEnvironmentFile();
            ops.LoadOrCreate(environment.Identity);
            NotifyLayerChanged();
            UpdateGoalMarker();
            Log($"regenerated: seed {generatorParams.seed} (restart Isaac Sim to load it there)");
            await ImportEnvironment();
            if (this) backend.Start(environment, ops);
        }

        private async void Start()
        {
            await ImportEnvironment();
            if (this) backend.Start(environment, ops);
        }

        private void OnDestroy()
        {
            backend?.Dispose();
            latency?.Dispose();
        }

        private async Task ImportEnvironment()
        {
            importing = true;
            try
            {
                float t0 = Time.realtimeSinceStartup;
                importStatus = $"importing {environment.Name}...";
                Transform envT = await environment.InstantiateAsync(stageRoot, s => importStatus = s);
                if (!this) return;
                // Keyframes Isaac Sim extracted from the factory's own animation (only when it has run).
                envAnim = environment is FactoryEnvironmentProvider
                    ? EnvAnimation.Load(Path.Combine(StageDir, envAnimName), envT)
                    : null;
                if (envAnim != null)
                {
                    envAnim.Evaluate(envAnim.Bake);   // the frozen import frame, incl. visibility
                    animRate = envAnim.TimeCodesPerSecond;
                    Debug.Log($"[AGV] environment animation: {envAnim.TrackCount} tracks ({envAnim.Missing} unbound), " +
                              $"{envAnim.Start:F0}..{envAnim.End:F0} @ {envAnim.TimeCodesPerSecond:F0} fps");
                }
                StyleEnvironment(envT);
                importStatus = $"{environment.Name} {Time.realtimeSinceStartup - t0:F1}s";
                Debug.Log($"[AGV] imported {environment.Name} ({environment.UsdPath}); {importStatus}");
                await CreateAgvTemplate();
            }
            catch (Exception e)
            {
                importStatus = "import failed: " + e.Message;
                Debug.LogException(e);
            }
            finally
            {
                importing = false;
            }
        }

        /// <summary>
        /// The AGV model cloned per AGV: the primitive <see cref="AgvModel"/>, or a USD model when
        /// Agv Model Usd is set in the settings (e.g. the iw_hub from the Isaac Sim assets).
        /// </summary>
        private async Task CreateAgvTemplate()
        {
            string usd = settings.agvModelUsd;
            if (!string.IsNullOrEmpty(usd))
            {
                try
                {
                    UsdImportResult agv = await UsdImporter.ImportAsync(usd, new UsdImportOptions
                    {
                        Parent = transform,
                        RootObjectName = "AGV template",
                        AllowExternalAssetPaths = true,
                        ImportTextures = false,
                        ImportMaterials = false,
                        ApplyStageUpAxisAndUnits = false,
                    });
                    Transform rootT = agv.RootObject.transform;
                    // a robot asset usually has one model prim next to a ground plane and a light: keep the model
                    Transform model = rootT.Find("iw_hub");
                    if (!model) model = rootT;
                    model.SetParent(transform, false);
                    if (model != rootT) Destroy(agv.RootObject);
                    agvTemplate = model.gameObject;
                    agvTemplate.name = "AGV template";
                    agvTemplate.SetActive(false);
                    agvTemplateFromUsd = true;
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AGV] AGV model {usd} failed to import ({e.Message}); using the primitive AGV");
                }
            }
            agvTemplate = AgvModel.CreateTemplate(transform);
            agvTemplateFromUsd = false;
        }

        private void StyleEnvironment(Transform env)
        {
            if (!env) return;
            // Both environments ship MDL (OmniPBR) materials, which the importer cannot read: give the
            // main object families readable colours (matched on the whole prim path, lower case,
            // first rule wins) instead of a uniform grey.
            Color grey = new Color(0.60f, 0.60f, 0.62f);
            var rules = new (string[] keys, Material mat)[]
            {
                (new[] { "paintline", "floordecal", "decal", "stripe" }, AgvView.LitMaterial(new Color(0.95f, 0.78f, 0.15f), 0.2f)),
                (new[] { "floor", "ground" }, AgvView.LitMaterial(new Color(0.42f, 0.43f, 0.45f), 0.25f)),
                (new[] { "kuka", "robot_arm", "robotarm", "welding_robot" }, AgvView.LitMaterial(new Color(0.95f, 0.45f, 0.08f), 0.45f)),
                (new[] { "curtain", "shell", "wall", "column", "framing", "mullion" }, AgvView.LitMaterial(new Color(0.80f, 0.80f, 0.78f), 0.1f)),
                (new[] { "hood", "door", "fender", "body", "car_", "vehicle", "sidepanel", "roofpanel" }, AgvView.LitMaterial(new Color(0.88f, 0.89f, 0.92f), 0.7f)),
                (new[] { "fenc", "locker", "cage", "guard", "walkway", "railing" }, AgvView.LitMaterial(new Color(0.30f, 0.32f, 0.35f), 0.3f)),
                (new[] { "beam" }, AgvView.LitMaterial(new Color(0.93f, 0.48f, 0.12f), 0.35f)),
                (new[] { "rack", "bracket", "shelf", "pillar", "lift" }, AgvView.LitMaterial(new Color(0.17f, 0.33f, 0.62f), 0.35f)),
                (new[] { "box", "crate", "palette", "pallet", "bin" }, AgvView.LitMaterial(new Color(0.70f, 0.55f, 0.36f), 0.15f)),
                (new[] { "conduit", "pipe", "wire", "cable", "connector", "electrical" }, AgvView.LitMaterial(new Color(0.22f, 0.23f, 0.25f), 0.3f)),
            };
            Material other = AgvView.LitMaterial(grey, 0.2f);
            bool first = true;
            int hidden = 0, tiny = 0;
            var sb = new System.Text.StringBuilder(256);
            foreach (Renderer r in env.GetComponentsInChildren<Renderer>(true))
            {
                if (first) { floorBounds = r.bounds; first = false; } else floorBounds.Encapsulate(r.bounds);
                string p0 = PathOf(r.transform, env, sb);
                // parts of an animated asset (welding cell: stacked car roofs, lifted panels) stay
                bool overhead = !InAnimatedAsset(r.transform, env)
                                && (r.bounds.min.y > 4f || p0.Contains("ceiling") || p0.Contains("lamp")
                                    || (p0.Contains("/roof") && !p0.Contains("rack"))); // car-roof racks stand on the floor
                if (overhead)
                {
                    r.enabled = false; // top-down operator view: hide the roof and overhead structure
                    hidden++;
                    continue;
                }
                float size = r.bounds.size.magnitude;
                if (size < 0.15f)
                {
                    r.enabled = false; // bolts, clips, labels: invisible from the operator view, costly in draw calls
                    tiny++;
                    continue;
                }
                if (size < 1.5f) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                string path = sb.ToString();
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null)
                    {
                        // imported from the look layer (UsdPreviewSurface translation of the MDL):
                        // keep it, but share identical ones so static batching can merge them
                        mats[i] = Dedupe(mats[i]);
                        continue;
                    }
                    Material m = other; // nothing bound in USD: colour by object family
                    foreach (var rule in rules)
                    {
                        if (Array.Exists(rule.keys, k => path.Contains(k))) { m = rule.mat; break; }
                    }
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
            }
            Debug.Log($"[AGV] environment materials: {dedupe.Count} distinct after de-duplication");
            // few shared materials now: static batching collapses thousands of draw calls
            // (not the animated parts: a statically batched mesh can no longer move)
            if (envAnim == null)
            {
                StaticBatchingUtility.Combine(env.gameObject);
            }
            else
            {
                var still = new List<GameObject>();
                foreach (MeshRenderer r in env.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r.enabled && !UnderAnimated(r.transform, env)) still.Add(r.gameObject);
                }
                StaticBatchingUtility.Combine(still.ToArray(), env.gameObject);
            }
            Debug.Log($"[AGV] environment styled: {hidden} overhead + {tiny} tiny renderers hidden, static batching on");
            SetupLighting(floorBounds);
            FrameCamera(floorBounds);
        }

        /// <summary>
        /// Indoor lighting for the imported hall: trilight ambient (a flat ambient leaves floors near
        /// black from above) and one reflection probe of the hall itself, rendered once after import,
        /// so the metallic materials translated from MDL have something to reflect.
        /// </summary>
        private void SetupLighting(Bounds hall)
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.78f, 0.80f, 0.84f);
            RenderSettings.ambientEquatorColor = new Color(0.58f, 0.58f, 0.60f);
            RenderSettings.ambientGroundColor = new Color(0.36f, 0.36f, 0.37f);
            RenderSettings.ambientIntensity = 1f;

            if (hallProbe) Destroy(hallProbe.gameObject);
            var probeGo = new GameObject("Hall reflection probe");
            probeGo.transform.SetParent(transform, false);
            probeGo.transform.position = new Vector3(hall.center.x, 3f, hall.center.z);
            var probe = probeGo.AddComponent<ReflectionProbe>();
            hallProbe = probe;
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
            probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            probe.size = new Vector3(hall.size.x + 2f, 14f, hall.size.z + 2f);
            probe.boxProjection = true;
            probe.resolution = 256;
            probe.intensity = 1f;
            probe.backgroundColor = new Color(0.55f, 0.57f, 0.60f);
            probe.clearFlags = UnityEngine.Rendering.ReflectionProbeClearFlags.SolidColor;
            probe.RenderProbe();
        }

        private bool InAnimatedAsset(Transform t, Transform stop)
        {
            if (envAnim == null) return false;
            for (; t != null && t != stop; t = t.parent)
            {
                if (envAnim.AssetRoots.Contains(t)) return true;
            }
            return false;
        }

        private bool UnderAnimated(Transform t, Transform stop)
        {
            for (; t != null && t != stop; t = t.parent)
            {
                if (envAnim.Animated.Contains(t)) return true;
            }
            return false;
        }

        /// <summary>
        /// Environment animation in step with Isaac: the last time code from the hub, advanced at the
        /// measured stream rate between messages (wrapping like Isaac's looping timeline), held when
        /// the stream stops.
        /// </summary>
        private void UpdateEnvAnimation()
        {
            if (envAnim == null || animRxTime < 0f) return;
            float since = Mathf.Min(Time.unscaledTime - animRxTime, 0.5f);
            double tc = animTc + since * animRate;
            double len = envAnim.End - envAnim.Start;
            if (tc > envAnim.End && len > 0) tc = envAnim.Start + (tc - envAnim.Start) % len;
            envAnim.Evaluate(tc);
            animShownTc = tc;
        }

        private double animShownTc;

        /// <summary>One-line status of the environment animation, for the panel.</summary>
        public string EnvAnimStatus =>
            envAnim == null ? "none"
            : animRxTime < 0f ? $"frozen at {envAnim.Bake:F0} (waiting for Isaac's timeline)"
            : $"frame {animShownTc:F0} / {envAnim.End:F0} · {animRate:F1} fps" + (Time.unscaledTime - animRxTime > 1f ? " · held" : "");

        private void OnAnimTimeCode(double tc)
        {
            float now = Time.unscaledTime;
            if (animRxTime >= 0f)
            {
                float dt = now - animRxTime;
                double dTc = tc - animTc;
                // Isaac's timeline speed follows its frame rate; ignore wraps and bursts
                if (dt > 0.02f && dTc > 0 && envAnim != null)
                {
                    double r = Math.Min(dTc / dt, envAnim.TimeCodesPerSecond * 4);
                    animRate += (r - animRate) * 0.2;
                }
            }
            animTc = tc;
            animRxTime = now;
        }

        private readonly Dictionary<string, Material> dedupe = new Dictionary<string, Material>();

        /// <summary>One shared material per distinct look (colour, metal, smoothness, maps, transparency).</summary>
        private Material Dedupe(Material m)
        {
            string Tex(string prop) => m.HasProperty(prop) && m.GetTexture(prop) ? m.GetTexture(prop).name : "";
            Color c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
            string key = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1:F2},{2:F2},{3:F2},{4:F2}|{5:F2}|{6:F2}|{7}|{8}|{9}|{10}",
                m.shader.name, c.r, c.g, c.b, c.a,
                m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f,
                m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : 0f,
                Tex("_BaseMap"), Tex("_BumpMap"), Tex("_MetallicGlossMap"), m.renderQueue);
            if (dedupe.TryGetValue(key, out Material shared)) return shared;
            dedupe[key] = m;
            return m;
        }

        private static string PathOf(Transform t, Transform stop, System.Text.StringBuilder sb)
        {
            sb.Clear();
            for (Transform p = t; p != null && p != stop; p = p.parent) sb.Insert(0, "/" + p.name.ToLowerInvariant());
            return sb.ToString();
        }

        // ---- per frame --------------------------------------------------------------------------
        private void Update()
        {
            backend.Tick(Time.deltaTime);
            DrainHub();
            HandleInput();
            UpdateCamera();
            UpdateEnvAnimation();
            if (Time.frameCount % 60 == 0) latency.Flush();
        }

        private void DrainHub()
        {
            while (backend.TryReceive(out HubMsg m, out double rx))
            {
                switch (m.type)
                {
                    case "hello":
                        lastHello = m;
                        lastStateSeq = 0;   // a new source (Isaac restarted, or a switch to/from the local fleet) counts from 1
                        Log($"hub hello: {string.Join(",", m.agv_ids)} mode={m.mode}");
                        // drop AGVs Isaac no longer has (count changed between runs)
                        var keep = new HashSet<string>(m.agv_ids);
                        foreach (var id in new List<string>(agvs.Keys))
                        {
                            if (!keep.Contains(id)) { Destroy(agvs[id].gameObject); agvs.Remove(id); }
                        }
                        break;
                    case "state":
                        if (lastStateSeq > 0 && m.seq > lastStateSeq + 1) missedStates += m.seq - lastStateSeq - 1;
                        lastStateSeq = m.seq;
                        lastStateRx = rx;
                        var ids = new List<string>();
                        foreach (AgvStateMsg s in m.agvs)
                        {
                            AgvView v = GetOrCreateAgv(s.id);
                            if (v != null) v.ApplyState(s, rx);
                            ids.Add(s.id);
                        }
                        latency.OnState(rx, HubClient.WallMs(), m.wall_ms, ids);
                        if (m.anim_tc > 0) OnAnimTimeCode(m.anim_tc);
                        break;
                    case "path":
                        GetOrCreateAgv(m.agv)?.SetPath(m.ver, m.pts);
                        break;
                    case "fleet":
                        lastFleet = m;
                        foreach (AgvTaskMsg t in m.assign)
                        {
                            if (agvs.TryGetValue(t.agv, out AgvView v)) v.Assignment = t.task;
                        }
                        UpdateGoalMarker();
                        break;
                    case "event":
                        latency.OnIsaacEvent(m);
                        if (m.@event != "plan_ready" && m.@event != "moving_new_path")
                        {
                            Log($"{m.@event} {m.agv} {m.ref_id} {m.detail}".Replace("  ", " "));
                        }
                        break;
                }
            }
        }

        private AgvView GetOrCreateAgv(string id)
        {
            if (agvs.TryGetValue(id, out AgvView v)) return v;
            if (importing || agvTemplate == null) return null;
            Color c = ColorFor(id);
            v = AgvView.Create(id, c, agvTemplate, dynamicRoot, lineMat, agvTemplateFromUsd);
            agvs[id] = v;
            return v;
        }

        private Color ColorFor(string id)
        {
            if (lastHello?.agv_ids != null && lastHello.colors != null)
            {
                int i = Array.IndexOf(lastHello.agv_ids, id);
                if (i >= 0 && lastHello.colors.Length >= 3 * i + 3)
                {
                    return new Color(lastHello.colors[3 * i], lastHello.colors[3 * i + 1], lastHello.colors[3 * i + 2]);
                }
            }
            return Color.gray;
        }

        private void Log(string line)
        {
            eventLines.Insert(0, $"{DateTime.Now:HH:mm:ss} {line}");
            if (eventLines.Count > 12) eventLines.RemoveAt(eventLines.Count - 1);
        }

        private bool HubLive => backend.Connected && HubClient.WallMs() - lastStateRx < 1000;

        // ---- test hooks (scripted acceptance runs drive the real Unity code paths) --------------
        public void TestPlaceObstacle(string preset, float usdX, float usdY)
        {
            ObstaclePreset p = Array.Find(Presets, q => q.Name == preset);
            PlaceObstacle(p.Name != null ? p : Presets[0], UsdFrame.ToUnity(usdX, usdY, 0));
        }

        public void TestDeleteObstacle(string name) => DeleteObstacle(name);

        public void TestSetGoal(float usdX, float usdY, string targetAgv)
        {
            Select(!string.IsNullOrEmpty(targetAgv) && agvs.TryGetValue(targetAgv, out AgvView v) ? v : null);
            SetGoal(UsdFrame.ToUnity(usdX, usdY, 0));
        }

        public void TestReset() => ResetScene();

        public string TestBackendName => backend.DisplayName;

        public string TestBackendNote => backend.Note;

        public void TestRegenerate() => Regenerate();

        public string TestSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"import: {importStatus}; {backend.DisplayName} live={HubLive} rate={latency.StateRateHz:F1}Hz missed={missedStates} reconnects={backend.Reconnects}\n");
            foreach (AgvView v in agvs.Values)
            {
                var (med, p95, max) = latency.LagStats(v.AgvId);
                sb.Append($"{v.AgvId} {v.Last?.nav} {v.Last?.task} pos={v.transform.position:F2} lag med={med:F1} p95={p95:F1} max={max:F1}\n");
            }
            foreach (LatencyMonitor.Chain c in latency.RecentChains)
            {
                sb.Append($"{c.Ref}: seen={(c.SeenMs < 0 ? -1 : c.SeenMs - c.StartMs):F0} replan={(c.ReplanMs < 0 ? -1 : c.ReplanMs - c.StartMs):F0} moving={(c.MovingMs < 0 ? -1 : c.MovingMs - c.StartMs):F0} {c.Agv}\n");
            }
            sb.Append($"obstacles={ops.Obstacles.Count} queue={(lastFleet?.queue == null ? "" : string.Join(",", lastFleet.queue))}");
            return sb.ToString();
        }

        // ---- operator actions -------------------------------------------------------------------
        private void NotifyLayerChanged() => backend.OpsChanged(ops.Revision);

        private void PlaceObstacle(ObstaclePreset p, Vector3 unityPoint)
        {
            Vector3 usd = UsdFrame.ToUsd(unityPoint);
            // Never drop a collider onto an AGV: a static box inside a moving articulation makes
            // PhysX explode. 0.4 m clearance around the AGV pick boxes.
            Vector3 half = UsdFrame.ToUnity(p.Size) * 0.5f + new Vector3(0.4f, 0f, 0.4f);
            foreach (Collider c in Physics.OverlapBox(new Vector3(unityPoint.x, half.y, unityPoint.z), half,
                         Quaternion.Euler(0f, placeYawDeg, 0f)))
            {
                AgvView hitAgv = c.GetComponentInParent<AgvView>();
                if (hitAgv)
                {
                    Log($"not placed: too close to {hitAgv.AgvId}");
                    return;
                }
            }
            ObstacleRecord rec = ops.AddObstacle(p.Name, usd.x, usd.y, UsdFrame.UnityYawToUsdDeg(placeYawDeg), p.Size, p.Color);
            NotifyLayerChanged();
            latency.StartChain(rec.name, rec.createdWallMs, "obstacle_created", $"{p.Name} write {ops.LastWriteDurationMs:F1}ms");
            CreateObstacleView(rec);
            Log($"placed {rec.name} ({p.Name})");
        }

        private void DeleteObstacle(string name)
        {
            double t = HubClient.WallMs();
            if (!ops.RemoveObstacle(name)) return;
            NotifyLayerChanged();
            latency.StartChain(name, t, "obstacle_deleted");
            if (obstacleViews.TryGetValue(name, out GameObject go)) Destroy(go);
            obstacleViews.Remove(name);
            Log($"deleted {name}");
        }

        private void SetGoal(Vector3 unityPoint)
        {
            Vector3 usd = UsdFrame.ToUsd(unityPoint);
            string target = selected ? selected.AgvId : "";
            GoalRecord g = ops.SetGoal(usd.x, usd.y, target);
            NotifyLayerChanged();
            latency.StartChain($"goal:{g.seq}", g.createdWallMs, "goal_set", string.IsNullOrEmpty(target) ? "fleet assigns" : "-> " + target);
            Log($"goal:{g.seq} set" + (string.IsNullOrEmpty(target) ? "" : $" for {target}"));
            Select(null);
            UpdateGoalMarker();
        }

        private void ResetScene()
        {
            ops.Reset();
            NotifyLayerChanged();
            backend.Reset();
            foreach (GameObject go in obstacleViews.Values) Destroy(go);
            obstacleViews.Clear();
            Select(null);
            UpdateGoalMarker();
            latency.Log(HubClient.WallMs(), "reset");
            Log("scene reset");
        }

        private void Select(AgvView v)
        {
            if (selected) selected.Selected = false;
            selected = v;
            if (selected) selected.Selected = true;
        }

        private void CreateObstacleView(ObstacleRecord o)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = o.name;
            go.transform.SetParent(dynamicRoot, false);
            go.transform.SetPositionAndRotation(UsdFrame.ToUnity(o.x, o.y, o.size.z * 0.5f), UsdFrame.YawToUnity(o.yawDeg * Mathf.Deg2Rad));
            go.transform.localScale = UsdFrame.ToUnity(o.size);
            go.GetComponent<Renderer>().sharedMaterial = AgvView.LitMaterial(o.color, 0.2f);
            obstacleViews[o.name] = go;
        }

        private void UpdateGoalMarker()
        {
            GoalRecord g = ops.Goal;
            string key = $"goal:{g.seq}";
            string owner = "";
            if (lastFleet?.assign != null)
            {
                foreach (AgvTaskMsg t in lastFleet.assign) if (t.task == key) owner = t.agv;
            }
            bool queued = lastFleet?.queue != null && Array.Exists(lastFleet.queue, q => q.StartsWith(key));
            bool fresh = HubClient.WallMs() - g.createdWallMs < 3000; // until Isaac has answered
            bool show = g.active && (owner != "" || queued || fresh);
            if (!goalMarker) goalMarker = MakeGoalMarker();
            goalMarker.SetActive(show);
            if (!show) return;
            goalMarker.transform.position = UsdFrame.ToUnity(g.x, g.y, 0);
            Color c = owner != "" && agvs.TryGetValue(owner, out AgvView v) ? v.Color : Color.white;
            foreach (Renderer r in goalMarker.GetComponentsInChildren<Renderer>()) r.sharedMaterial = AgvView.LitMaterial(c, 0.6f);
        }

        private GameObject MakeGoalMarker()
        {
            var root = new GameObject("Goal (/World/Dynamic/Goal)");
            root.transform.SetParent(dynamicRoot, false);
            GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(disc.GetComponent<Collider>());
            disc.transform.SetParent(root.transform, false);
            disc.transform.localScale = new Vector3(0.9f, 0.01f, 0.9f);
            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(pole.GetComponent<Collider>());
            pole.transform.SetParent(root.transform, false);
            pole.transform.localPosition = new Vector3(0, 0.75f, 0);
            pole.transform.localScale = new Vector3(0.06f, 0.75f, 0.06f);
            GameObject flag = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(flag.GetComponent<Collider>());
            flag.transform.SetParent(root.transform, false);
            flag.transform.localPosition = new Vector3(0.25f, 1.35f, 0);
            flag.transform.localScale = new Vector3(0.45f, 0.28f, 0.03f);
            return root;
        }
    }
}
