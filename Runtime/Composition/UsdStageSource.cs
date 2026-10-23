using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.USDToolkit.Native;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Sits on the root object an import creates and records the stage it came from: the file,
    /// its metadata, the variant and payload choices the import composed with, and what the
    /// stage offers. A <see cref="UsdCompositionMode.Preserve"/> export reads it to write the
    /// object back as overrides on that file instead of copying it.
    ///
    /// The root object stands for the stage's pseudo-root; its children are the stage's root
    /// prims. Each object below carries a <see cref="UsdPrimSource"/>.
    ///
    /// <see cref="SwitchVariant"/>, <see cref="SwitchPayloads"/>, <see cref="LoadPayload"/> and
    /// <see cref="UnloadPayload"/> (and their async forms) change the composition at runtime, in
    /// place: the change is made on the stage this import keeps open (<see cref="Stage"/>), and only
    /// the parts of the import it recomposes are imported again and put where the old parts were.
    /// Everything else stays as it is, edits included. <see cref="Reimport"/> imports the whole
    /// stage again instead.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdStageSource : MonoBehaviour
    {
        [SerializeField] private string sourcePath;
        [SerializeField] private string defaultPrimPath;
        [SerializeField] private double metersPerUnit = 1.0;
        [SerializeField] private int upAxis = 1;
        [SerializeField] private Quaternion stageCorrectionRotation = Quaternion.identity;
        [SerializeField] private float stageCorrectionScale = 1f;
        [SerializeField] private bool payloadsLoaded = true;
        [SerializeField] private List<string> payloadsToLoad = new List<string>();
        [SerializeField] private List<UsdVariantSelection> variantSelections = new List<UsdVariantSelection>();
        [SerializeField] private List<UsdVariantSetInfo> variantSets = new List<UsdVariantSetInfo>();
        [SerializeField] private List<UsdPayloadInfo> payloads = new List<UsdPayloadInfo>();
        [SerializeField] private List<UsdCompositionArcInfo> arcs = new List<UsdCompositionArcInfo>();
        [SerializeField] private List<string> primPaths = new List<string>();

        // For live switching: the files the import read with their write times (a switch after one
        // changed imports the whole stage again), and the light intensity the import scaled its
        // lights against (rebuilt lights keep it).
        [SerializeField] private List<string> layerFiles = new List<string>();
        [SerializeField] private List<long> layerWriteTimes = new List<long>();
        [SerializeField] private float lightReference;

        // The stage this import keeps open; opened on first use. Closed with the component, and
        // re-opened from what is recorded here (the file and the choices) whenever it is gone.
        [NonSerialized] private UsdStage stage;

        // The options the import that made this ran with, for as long as the session lasts: they
        // hold a parent and a callback, which a scene cannot keep. Null on an object loaded from a
        // scene or an asset, whose meshes and materials the import did not make in this session.
        [NonSerialized] private UsdImportOptions importedWith;

        /// <summary>
        /// The imported file on disk. A file inside the Unity project is recorded by its project
        /// path ("Assets/Stages/Shot.usda", "Packages/com.studio.set/Set.usd"), so the component
        /// says the same on every machine and survives the project moving; this resolves it to
        /// the file in the Editor. Any other file is recorded as the absolute path the import read.
        /// </summary>
        public string SourcePath => UsdProjectPaths.Resolve(sourcePath);

        /// <summary>
        /// <see cref="SourcePath"/> as recorded: a project path for a file inside the project,
        /// else an absolute path.
        /// </summary>
        public string RecordedSourcePath => sourcePath;

        /// <summary>The stage's defaultPrim ("/World"), or empty when it has none.</summary>
        public string DefaultPrimPath => defaultPrimPath;

        public double MetersPerUnit => metersPerUnit;

        /// <summary>"Y" or "Z".</summary>
        public string UpAxis => upAxis == 2 ? "Z" : "Y";

        /// <summary>
        /// The up-axis rotation the import put on this root's local rotation
        /// (UsdImportOptions.ApplyStageUpAxisAndUnits). Identity when none was applied.
        /// </summary>
        public Quaternion StageCorrectionRotation => stageCorrectionRotation;

        /// <summary>
        /// The unit scale (metersPerUnit) the import put on this root's local scale. 1 when none
        /// was applied.
        /// </summary>
        public float StageCorrectionScale => stageCorrectionScale;

        /// <summary>False when the import left payloads unloaded (UsdImportOptions.LoadPayloads).</summary>
        public bool PayloadsLoaded => payloadsLoaded;

        /// <summary>Payloads loaded individually while the rest were left unloaded.</summary>
        public IReadOnlyList<string> PayloadsToLoad => payloadsToLoad;

        /// <summary>The selections the import applied (UsdImportOptions.VariantSelections).</summary>
        public IReadOnlyList<UsdVariantSelection> VariantSelections => variantSelections;

        /// <summary>Every variant set on the stage, with the selection composed. Empty on a native plugin older than 0.8.0.</summary>
        public IReadOnlyList<UsdVariantSetInfo> VariantSets => variantSets;

        /// <summary>Every prim with a payload, and whether it was loaded. Empty on a native plugin older than 0.8.0.</summary>
        public IReadOnlyList<UsdPayloadInfo> Payloads => payloads;

        /// <summary>Direct references, payloads, inherits, specializes and relocates. Empty on a native plugin older than 0.8.0.</summary>
        public IReadOnlyList<UsdCompositionArcInfo> Arcs => arcs;

        /// <summary>The prim path of every object the import created, in creation order.</summary>
        public IReadOnlyList<string> PrimPaths => primPaths;

        internal int UpAxisCode => upAxis;

        // The import that made this ran in this session, so the meshes and materials below are its own.
        internal bool ImportedInSession => importedWith != null;

        /// <summary>
        /// The stage this import came from, open and composed as the import composed it -- the same
        /// variant selections and loaded payloads, kept up to date by the switches -- to read with
        /// the <see cref="UsdStage"/> API: prims, attributes at any time code, variant sets. It is
        /// read-only (<see cref="UsdStage.IsReadOnly"/>); the composition changes through this
        /// component, so the scene and a Preserve export keep agreeing with it. Opened on first use,
        /// or kept from the import (<see cref="UsdImportOptions.KeepStageOpen"/>), and closed with
        /// this component. Needs the 0.8.1 native plugin and the file where <see cref="SourcePath"/> says.
        /// </summary>
        public UsdStage Stage
        {
            get
            {
                if (stage != null && stage.IsOpen)
                {
                    return stage;
                }

                string path = SourcePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    throw new UsdStageException($"The stage '{sourcePath}' is not on disk here.");
                }

                stage = UsdStage.OpenForImport(path, payloadsLoaded, payloadsToLoad, variantSelections);
                return stage;
            }
        }

        internal void AdoptStage(UsdStage kept)
        {
            stage?.Dispose();
            stage = kept;
        }

        private void OnDestroy()
        {
            stage?.Dispose();
            stage = null;
        }

        /// <summary>
        /// A copy of the options the import that made this object ran with, or null when it was
        /// not imported in this session (loaded from a scene or an asset). The switch methods start
        /// from them.
        /// </summary>
        public UsdImportOptions ImportOptions => importedWith?.Clone();

        /// <summary>
        /// Import options that reproduce this import with one more variant selection (replacing
        /// any earlier choice for the same prim and set). Re-import with them to switch a variant:
        /// <c>UsdImporter.Import(source.SourcePath, source.CreateOptionsWithVariant(...))</c>.
        /// </summary>
        public UsdImportOptions CreateOptionsWithVariant(string primPath, string variantSet, string variant) =>
            CreateOptionsWithVariant(primPath, variantSet, variant, null);

        /// <summary>
        /// Like <see cref="CreateOptionsWithVariant(string, string, string)"/>, but every other
        /// setting -- parent, materials, textures, up axis and units and the rest -- is copied from
        /// <paramref name="template"/>, typically the options the stage was imported with. Without
        /// one they are the defaults.
        /// </summary>
        public UsdImportOptions CreateOptionsWithVariant(string primPath, string variantSet, string variant,
                                                         UsdImportOptions template)
        {
            UsdImportOptions options = template != null ? template.Clone() : new UsdImportOptions();
            options.LoadPayloads = payloadsLoaded;
            options.PayloadsToLoad = new List<string>(payloadsToLoad);
            options.VariantSelections = new List<UsdVariantSelection>();

            foreach (UsdVariantSelection selection in variantSelections)
            {
                if (selection.PrimPath != primPath || selection.VariantSet != variantSet)
                {
                    options.VariantSelections.Add(new UsdVariantSelection(selection.PrimPath, selection.VariantSet, selection.Variant));
                }
            }

            options.VariantSelections.Add(new UsdVariantSelection(primPath, variantSet, variant));
            return options;
        }

        /// <summary>
        /// Like <see cref="CreateOptionsWithVariant(string, string, string, UsdImportOptions)"/>, for
        /// payloads: options that reproduce this import's variant selections with
        /// <paramref name="loadPayloads"/> and <paramref name="payloadsToLoad"/> instead of its own
        /// payload choice. Every other setting comes from <paramref name="template"/>, or the defaults.
        /// </summary>
        public UsdImportOptions CreateOptionsWithPayloads(bool loadPayloads, IEnumerable<string> payloadsToLoad,
                                                          UsdImportOptions template = null)
        {
            UsdImportOptions options = template != null ? template.Clone() : new UsdImportOptions();
            options.LoadPayloads = loadPayloads;
            options.PayloadsToLoad = payloadsToLoad != null ? new List<string>(payloadsToLoad) : new List<string>();
            options.VariantSelections = new List<UsdVariantSelection>();
            foreach (UsdVariantSelection selection in variantSelections)
            {
                options.VariantSelections.Add(new UsdVariantSelection(selection.PrimPath, selection.VariantSet, selection.Variant));
            }

            return options;
        }

        /// <summary>
        /// Switches a variant, in place: selects <paramref name="variant"/> in
        /// <paramref name="variantSet"/> on <paramref name="primPath"/> on the kept stage (in its
        /// session layer, no file is edited) and imports again only the parts that recompose,
        /// putting each where the old one was. What was done by hand to those parts' own objects
        /// (moved, renamed, switched off) and objects added below them carry over; edits inside them
        /// are the stage's again. When the change cannot be confined to parts -- see
        /// <see cref="UsdSwitchResult.ReimportReason"/> -- the whole stage is imported again in this
        /// import's place, as <see cref="Reimport"/> does. Settings come from
        /// <paramref name="template"/>, else from <see cref="ImportOptions"/>, else the defaults.
        /// Throws for a prim, set or variant the stage does not have.
        /// </summary>
        public UsdSwitchResult SwitchVariant(string primPath, string variantSet, string variant, UsdImportOptions template = null) =>
            Switch(new VariantChange(primPath, variantSet, variant), template);

        /// <summary>
        /// <see cref="SwitchVariant"/> with the stage read off the main thread and the parts built
        /// across frames; the old parts stay in place, and playing, until the new ones are built.
        /// </summary>
        public Task<UsdSwitchResult> SwitchVariantAsync(string primPath, string variantSet, string variant,
                                                        UsdImportOptions template = null) =>
            SwitchAsync(new VariantChange(primPath, variantSet, variant), template);

        /// <summary>
        /// Switches which payloads are loaded, in place as <see cref="SwitchVariant"/> does: all of
        /// them with <paramref name="loadPayloads"/>, else only those at or below the prims in
        /// <paramref name="payloadsToLoad"/>.
        /// </summary>
        public UsdSwitchResult SwitchPayloads(bool loadPayloads, IEnumerable<string> payloadsToLoad = null,
                                              UsdImportOptions template = null) =>
            Switch(new PayloadChange(loadPayloads, payloadsToLoad, null, false), template);

        /// <summary><see cref="SwitchPayloads"/>, asynchronously, as <see cref="SwitchVariantAsync"/>.</summary>
        public Task<UsdSwitchResult> SwitchPayloadsAsync(bool loadPayloads, IEnumerable<string> payloadsToLoad = null,
                                                         UsdImportOptions template = null) =>
            SwitchAsync(new PayloadChange(loadPayloads, payloadsToLoad, null, false), template);

        /// <summary>Loads the payloads at and below <paramref name="primPath"/>, keeping the others as they are.</summary>
        public UsdSwitchResult LoadPayload(string primPath, UsdImportOptions template = null) =>
            Switch(new PayloadChange(false, null, primPath, true), template);

        /// <summary>Unloads the payloads at and below <paramref name="primPath"/>, keeping the others as they are.</summary>
        public UsdSwitchResult UnloadPayload(string primPath, UsdImportOptions template = null) =>
            Switch(new PayloadChange(false, null, primPath, false), template);

        private UsdSwitchResult Switch(Change change, UsdImportOptions template)
        {
            CheckSwitchable();
            UsdImportOptions options = SwitchOptions(template);
            string reason = LiveBlocker(out UsdStage live);
            if (reason != null)
            {
                return Reimported(Reimport(change.OptionsWithoutStage(this, options)), reason);
            }

            List<string> roots = Prepare(change, live, out bool confined);
            if (!confined)
            {
                return Reimported(Reimport(OptionsFromRecord(options)), "the change reaches past one part of the stage");
            }

            return roots.Count == 0
                ? new UsdSwitchResult(gameObject, roots, null, null)
                : UsdImporter.RebuildSubtrees(this, live, roots, options, RemainingLimits(options.Limits, roots), lightReference);
        }

        private async Task<UsdSwitchResult> SwitchAsync(Change change, UsdImportOptions template)
        {
            CheckSwitchable();
            UsdImportOptions options = SwitchOptions(template);
            string reason = LiveBlocker(out UsdStage live);
            if (reason != null)
            {
                return Reimported(await ReimportAsync(change.OptionsWithoutStage(this, options)), reason);
            }

            List<string> roots = Prepare(change, live, out bool confined);
            if (!confined)
            {
                return Reimported(await ReimportAsync(OptionsFromRecord(options)), "the change reaches past one part of the stage");
            }

            if (roots.Count == 0)
            {
                return new UsdSwitchResult(gameObject, roots, null, null);
            }

            return await UsdImporter.RebuildSubtreesAsync(this, live, roots, options, RemainingLimits(options.Limits, roots),
                                                          lightReference);
        }

        // Why this switch has to import the whole stage again, or null with the stage to switch on.
        private string LiveBlocker(out UsdStage live)
        {
            live = null;
            UsdExporter.EnsureRuntimeConfigured(); // before anything asks the plugin its version
            if (!UsdNative.SupportsStageApi)
            {
                return $"{UsdNative.NeedsPlugin(UsdNative.StageApiVersion)} is needed; {UsdNative.LoadedPluginDescription}";
            }

#if UNITY_EDITOR
            if (!Application.isPlaying && UnityEditor.PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                return "a prefab instance cannot lose part of itself in Edit Mode";
            }
#endif
            if (SourceChanged())
            {
                // The kept stage, and OpenUSD's cache of the layers it holds, show the old content.
                stage?.Dispose();
                stage = null;
                return "the stage's files changed on disk since the import";
            }

            try
            {
                live = Stage;
                return null;
            }
            catch (Exception exception) when (exception is UsdStageException || exception is UsdExportException ||
                                              exception is NotSupportedException)
            {
                return "the stage could not be opened: " + exception.Message;
            }
        }

        // Makes the change on the stage and returns the prims to reimport from: what the stage
        // recomposed, widened to whole skeletons and instancers, outermost only. `confined` is false
        // when a change reaches past one subtree.
        private List<string> Prepare(Change change, UsdStage live, out bool confined)
        {
            lock (live.Gate)
            {
                IntPtr context = live.Context;
                change.Validate(this, context);
                UsdNative.QueryText(context, UsdNative.QueryChanges, null); // what came before is not this switch's
                var changed = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string prim in change.Apply(context))
                {
                    changed.Add(prim);
                }

                foreach (string path in Lines(UsdNative.QueryText(context, UsdNative.QueryChanges, null)))
                {
                    changed.Add(path);
                }

                change.Record(this, context);
                var roots = new List<string>();
                foreach (string path in changed)
                {
                    string root = UsdNative.QueryText(context, UsdNative.QueryRebuildRoot, path);
                    if (string.IsNullOrEmpty(root))
                    {
                        confined = false;
                        return null;
                    }

                    roots.Add(root);
                }

                confined = true;
                return Outermost(roots);
            }
        }

        private static List<string> Outermost(List<string> roots)
        {
            roots.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b));
            var outermost = new List<string>();
            foreach (string root in roots)
            {
                if (!outermost.Exists(kept => UsdImporter.IsAtOrBelow(root, kept)))
                {
                    outermost.Add(root);
                }
            }

            return outermost;
        }

        private static IEnumerable<string> Lines(string text) => text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // The import's limits less what the rest of the import already holds, so switching back and
        // forth never grows it past them.
        private UsdImportLimits RemainingLimits(UsdImportLimits limits, List<string> roots)
        {
            UsdImportLimits remaining = (limits ?? new UsdImportLimits()).Clone();
            long nodes = 0;
            long meshes = 0;
            foreach (UsdPrimSource prim in GetComponentsInChildren<UsdPrimSource>(true))
            {
                if (prim.Stage != this || roots.Exists(root => UsdImporter.IsAtOrBelow(prim.PrimPath, root)))
                {
                    continue;
                }

                nodes++;
                if (prim.GetComponent<MeshFilter>() != null || prim.GetComponent<SkinnedMeshRenderer>() != null)
                {
                    meshes++;
                }
            }

            long Left(long value, long fallback, long used) => Math.Max(1, (value > 0 ? value : fallback) - used);
            remaining.MaxNodes = Left(remaining.MaxNodes, UsdImportLimits.DefaultMaxNodes, nodes);
            remaining.MaxMeshes = Left(remaining.MaxMeshes, UsdImportLimits.DefaultMaxMeshes, meshes);
            return remaining;
        }

        private UsdImportOptions SwitchOptions(UsdImportOptions template)
        {
            UsdImportOptions options = (template ?? importedWith)?.Clone() ?? new UsdImportOptions();
            options.RecordSource = true;
            options.ProgressCallback = null;
            return options;
        }

        // The options that reproduce this import as recorded now, settings from `template`.
        private UsdImportOptions OptionsFromRecord(UsdImportOptions template)
        {
            UsdImportOptions options = template.Clone();
            options.LoadPayloads = payloadsLoaded;
            options.PayloadsToLoad = new List<string>(payloadsToLoad);
            options.VariantSelections = new List<UsdVariantSelection>();
            foreach (UsdVariantSelection selection in variantSelections)
            {
                options.VariantSelections.Add(new UsdVariantSelection(selection.PrimPath, selection.VariantSet, selection.Variant));
            }

            return options;
        }

        private UsdSwitchResult Reimported(UsdImportResult result, string reason) =>
            new UsdSwitchResult(result.RootObject, null, reason, result);

        // Whether a file the import read changed on disk since.
        private bool SourceChanged()
        {
            for (int i = 0; i < layerFiles.Count && i < layerWriteTimes.Count; i++)
            {
                if (WriteTime(UsdProjectPaths.Resolve(layerFiles[i])) != layerWriteTimes[i])
                {
                    return true;
                }
            }

            string path = SourcePath;
            return string.IsNullOrEmpty(path) || !File.Exists(path);
        }

        private static long WriteTime(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return -1;
            }
        }

        private void RecordSelection(string primPath, string variantSet, string variant)
        {
            variantSelections.RemoveAll(selection => selection.PrimPath == primPath && selection.VariantSet == variantSet);
            variantSelections.Add(new UsdVariantSelection(primPath, variantSet, variant));
        }

        // The payload state as the stage now composes it: all loaded, or the loaded ones listed.
        private void RecordPayloads(IntPtr context)
        {
            UsdNative.ReleaseImportData(context); // the composition report is built afresh
            var sets = new List<UsdVariantSetInfo>();
            var infos = new List<UsdPayloadInfo>();
            var arcInfos = new List<UsdCompositionArcInfo>();
            ParseCompositionReport(UsdNative.GetImportComposition(context), sets, infos, arcInfos);
            payloadsLoaded = infos.TrueForAll(info => info.Loaded);
            payloadsToLoad = new List<string>();
            if (!payloadsLoaded)
            {
                foreach (UsdPayloadInfo info in infos)
                {
                    if (info.Loaded)
                    {
                        payloadsToLoad.Add(info.PrimPath);
                    }
                }
            }

            payloads = infos;
        }

        // One switch: checked and made on the kept stage, recorded here, or turned into the
        // options a whole reimport needs when there is no stage to make it on.
        private abstract class Change
        {
            // Throws for a prim, set or variant the stage does not have.
            public abstract void Validate(UsdStageSource source, IntPtr context);

            // Makes the change; the prims it edited.
            public abstract IEnumerable<string> Apply(IntPtr context);

            public abstract void Record(UsdStageSource source, IntPtr context);

            public abstract UsdImportOptions OptionsWithoutStage(UsdStageSource source, UsdImportOptions template);
        }

        private sealed class VariantChange : Change
        {
            private readonly string primPath;
            private readonly string variantSet;
            private readonly string variant;
            private bool unchanged;

            public VariantChange(string primPath, string variantSet, string variant)
            {
                this.primPath = string.IsNullOrEmpty(primPath) ? throw new ArgumentException("The prim path is empty.", nameof(primPath)) : primPath;
                this.variantSet = string.IsNullOrEmpty(variantSet) ? throw new ArgumentException("The variant set is empty.", nameof(variantSet)) : variantSet;
                this.variant = variant ?? string.Empty;
            }

            public override void Validate(UsdStageSource source, IntPtr context)
            {
                foreach (string line in Lines(UsdNative.QueryText(context, UsdNative.QueryVariantSets, primPath)))
                {
                    string[] fields = line.Split('\t');
                    if (fields[0] != variantSet)
                    {
                        continue;
                    }

                    if (variant.Length > 0 && Array.IndexOf(fields, variant, 2) < 0)
                    {
                        throw new ArgumentException($"{primPath} has no variant '{variant}' in '{variantSet}'.");
                    }

                    unchanged = fields.Length > 1 && fields[1] == variant;
                    return;
                }

                throw new ArgumentException($"{primPath} has no variant set '{variantSet}' on the stage.");
            }

            public override IEnumerable<string> Apply(IntPtr context)
            {
                if (unchanged)
                {
                    return Array.Empty<string>();
                }

                UsdNative.EditPrim(context, primPath, UsdNative.EditSelectVariant, variantSet, variant);
                return new[] { primPath };
            }

            public override void Record(UsdStageSource source, IntPtr context) => source.RecordSelection(primPath, variantSet, variant);

            public override UsdImportOptions OptionsWithoutStage(UsdStageSource source, UsdImportOptions template) =>
                source.CreateOptionsWithVariant(primPath, variantSet, variant, template);
        }

        private sealed class PayloadChange : Change
        {
            private readonly bool loadAll;
            private readonly List<string> toLoad;
            private readonly string single;
            private readonly bool singleLoad;

            public PayloadChange(bool loadAll, IEnumerable<string> toLoad, string single, bool singleLoad)
            {
                this.loadAll = loadAll;
                this.toLoad = toLoad != null ? new List<string>(toLoad) : new List<string>();
                this.single = single;
                this.singleLoad = singleLoad;
            }

            public override void Validate(UsdStageSource source, IntPtr context)
            {
                if (single != null && (UsdNative.GetPrimInfo(context, UsdStage.CheckPath(single), out _) & UsdNative.PrimValid) == 0)
                {
                    throw new ArgumentException($"The stage has no prim at {single}.");
                }
            }

            public override IEnumerable<string> Apply(IntPtr context)
            {
                if (single != null)
                {
                    UsdNative.EditPrim(context, single, singleLoad ? UsdNative.EditLoad : UsdNative.EditUnload);
                    return new[] { single };
                }

                // Every payload prim the stage composes now, set as asked: unloads first, so a load
                // below an unloaded one is not undone by it.
                UsdNative.ReleaseImportData(context);
                var infos = new List<UsdPayloadInfo>();
                ParseCompositionReport(UsdNative.GetImportComposition(context), new List<UsdVariantSetInfo>(), infos,
                                       new List<UsdCompositionArcInfo>());
                var edited = new List<string>();
                foreach (bool loading in new[] { false, true })
                {
                    foreach (UsdPayloadInfo info in infos)
                    {
                        bool wanted = loadAll || toLoad.Exists(path => UsdImporter.IsAtOrBelow(info.PrimPath, path));
                        if (wanted != info.Loaded && wanted == loading)
                        {
                            UsdNative.EditPrim(context, info.PrimPath, loading ? UsdNative.EditLoad : UsdNative.EditUnload);
                            edited.Add(info.PrimPath);
                        }
                    }
                }

                return edited;
            }

            public override void Record(UsdStageSource source, IntPtr context) => source.RecordPayloads(context);

            public override UsdImportOptions OptionsWithoutStage(UsdStageSource source, UsdImportOptions template)
            {
                if (single == null)
                {
                    return source.CreateOptionsWithPayloads(loadAll, toLoad, template);
                }

                // From the payloads the import recorded: the loaded ones, with this one changed.
                var loaded = new List<string>();
                foreach (UsdPayloadInfo info in source.payloads)
                {
                    bool below = UsdImporter.IsAtOrBelow(info.PrimPath, single);
                    if (below ? singleLoad : info.Loaded)
                    {
                        loaded.Add(info.PrimPath);
                    }
                }

                if (singleLoad && !loaded.Contains(single))
                {
                    loaded.Add(single);
                }

                bool all = source.payloads.TrueForAll(info => loaded.Contains(info.PrimPath));
                return source.CreateOptionsWithPayloads(all, all ? null : loaded, template);
            }
        }

        /// <summary>
        /// Imports the stage again with <paramref name="options"/> and puts the result in this
        /// import's place: under the same parent, at the same sibling index, with this object's
        /// name, local transform, layer and active state, and its <see cref="UsdAnimationPlayer"/>
        /// at the same time and play state. This import is then destroyed, with the meshes,
        /// materials, textures and animation data it made when it was imported in this session
        /// (an asset's are left alone). Components added to the root by hand do not carry over.
        /// Needs the source file where <see cref="SourcePath"/> says: a runtime import from disk, or
        /// any import in the Editor; a player has no file behind an asset.
        /// </summary>
        public UsdImportResult Reimport(UsdImportOptions options)
        {
            UsdImportOptions placed = PrepareReimport(options, out string path);
            UsdImportResult result = UsdImporter.Import(path, placed);
            ReplaceWith(result);
            return result;
        }

        /// <summary>
        /// <see cref="Reimport"/> through <see cref="UsdImporter.ImportAsync"/>: this import stays in
        /// place, and playing, until the new one is built. If this object is destroyed meanwhile,
        /// the new import is returned where it was built.
        /// </summary>
        public async Task<UsdImportResult> ReimportAsync(UsdImportOptions options)
        {
            UsdImportOptions placed = PrepareReimport(options, out string path);
            UsdImportResult result = await UsdImporter.ImportAsync(path, placed);
            if (this != null)
            {
                ReplaceWith(result);
            }

            return result;
        }

        private UsdImportOptions PrepareReimport(UsdImportOptions options, out string path)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            CheckSwitchable();
            path = SourcePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new UsdImportException(
                    $"The stage '{sourcePath}' is not on disk here, so it cannot be imported again with another choice.");
            }

            stage?.Dispose(); // the new import keeps its own
            stage = null;
            UsdImportOptions placed = options.Clone();
            placed.Parent = transform.parent;
            placed.RecordSource = true; // so the new import can be switched again
            return placed;
        }

        // An asset changes through its import settings, and a prefab instance cannot lose a part of
        // itself (the outermost root may be replaced whole).
        private void CheckSwitchable()
        {
#if UNITY_EDITOR
            if (UnityEditor.EditorUtility.IsPersistent(this))
            {
                throw new InvalidOperationException(
                    $"'{name}' is an asset; change its import settings (or instantiate it and switch the instance).");
            }

            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(gameObject) &&
                !UnityEditor.PrefabUtility.IsOutermostPrefabInstanceRoot(gameObject))
            {
                throw new InvalidOperationException(
                    $"'{name}' is inside a prefab instance, which cannot lose part of itself; unpack the prefab first.");
            }
#endif
        }

        private void ReplaceWith(UsdImportResult result)
        {
            GameObject next = result.RootObject;
            Transform t = next.transform;
            t.SetParent(transform.parent, false);
            t.SetSiblingIndex(transform.GetSiblingIndex());
            next.name = gameObject.name;
            next.layer = gameObject.layer;
            t.localPosition = transform.localPosition;
            t.localRotation = transform.localRotation;
            t.localScale = transform.localScale;

            UsdAnimationPlayer before = GetComponent<UsdAnimationPlayer>();
            UsdAnimationPlayer after = result.Animation;
            if (before != null && after != null)
            {
                after.Loop = before.Loop;
                after.Speed = before.Speed;
                after.PlayOnStart = before.PlayOnStart;
                after.Evaluate(Math.Min(before.CurrentTime, after.Duration));
                if (before.IsPlaying)
                {
                    after.Play();
                }
            }

            if (!gameObject.activeSelf)
            {
                next.SetActive(false);
            }

            ReleaseImport(result);
        }

        // Destroys this import: its objects, and what it made when it was imported in this session.
        private void ReleaseImport(UsdImportResult keep)
        {
            var made = new HashSet<Object>();
            if (importedWith != null)
            {
                UsdAnimationPlayer player = GetComponent<UsdAnimationPlayer>();
                made.Add(player != null ? player.Data : null);
                foreach (MeshFilter filter in GetComponentsInChildren<MeshFilter>(true))
                {
                    made.Add(filter.sharedMesh);
                }

                foreach (SkinnedMeshRenderer skinned in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    made.Add(skinned.sharedMesh);
                }

                foreach (UsdPointInstancer instancer in GetComponentsInChildren<UsdPointInstancer>(true))
                {
                    made.Add(instancer.Mesh);
                }

                foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null)
                        {
                            continue;
                        }

                        made.Add(material);
                        foreach (int property in material.GetTexturePropertyNameIDs())
                        {
                            made.Add(material.GetTexture(property));
                        }
                    }
                }

                // Nothing the new import holds, should the two ever share an object.
                foreach (Renderer renderer in keep.RootObject.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        made.Remove(material);
                    }
                }
            }

            DestroyMade(gameObject);
            foreach (Object item in made)
            {
                if (item == null)
                {
                    continue;
                }

#if UNITY_EDITOR
                if (UnityEditor.EditorUtility.IsPersistent(item))
                {
                    continue;
                }
#endif
                DestroyMade(item);
            }
        }

        private static void DestroyMade(Object item)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(item);
            }
            else
            {
                Object.DestroyImmediate(item);
            }
        }

        internal void Initialize(
            string path,
            string defaultPrim,
            double unit,
            int axis,
            UsdImportOptions options)
        {
            importedWith = options.Clone();
            importedWith.ProgressCallback = null;
            importedWith.StageWarningCallback = null;
            sourcePath = UsdProjectPaths.ToRecorded(path);
            defaultPrimPath = defaultPrim ?? string.Empty;
            metersPerUnit = unit;
            upAxis = axis;
            payloadsLoaded = options.LoadPayloads;
            payloadsToLoad = new List<string>(options.PayloadsToLoad ?? new List<string>());
            variantSelections = new List<UsdVariantSelection>();
            foreach (UsdVariantSelection selection in options.VariantSelections ?? new List<UsdVariantSelection>())
            {
                if (selection != null && !string.IsNullOrEmpty(selection.PrimPath) && !string.IsNullOrEmpty(selection.VariantSet))
                {
                    variantSelections.Add(new UsdVariantSelection(selection.PrimPath, selection.VariantSet, selection.Variant));
                }
            }
        }

        internal void SetStageCorrection(Quaternion rotation, float scale)
        {
            stageCorrectionRotation = rotation;
            stageCorrectionScale = scale;
        }

        internal void SetComposition(
            List<UsdVariantSetInfo> sets,
            List<UsdPayloadInfo> payloadInfos,
            List<UsdCompositionArcInfo> arcInfos)
        {
            variantSets = sets;
            payloads = payloadInfos;
            arcs = arcInfos;
            List<KeyValuePair<string, string>> roots = arcs.Count > 0 ? UsdProjectPaths.ProjectRoots() : null;
            foreach (UsdCompositionArcInfo arc in arcs)
            {
                arc.Layer = UsdProjectPaths.ToRecorded(arc.Layer, roots);
            }
        }

        internal void AddPrimPath(string primPath)
        {
            primPaths.Add(primPath);
        }

        internal void RemovePrimPathsUnder(string root) => primPaths.RemoveAll(path => UsdImporter.IsAtOrBelow(path, root));

        internal void SetLiveState(string[] layers, float reference)
        {
            lightReference = reference;
            layerFiles = new List<string>();
            layerWriteTimes = new List<long>();
            List<KeyValuePair<string, string>> roots = layers != null && layers.Length > 0 ? UsdProjectPaths.ProjectRoots() : null;
            foreach (string layer in layers ?? Array.Empty<string>())
            {
                layerFiles.Add(UsdProjectPaths.ToRecorded(layer, roots));
                layerWriteTimes.Add(WriteTime(layer));
            }
        }

        // After a switch rebuilt parts: the variant sets, payloads and arcs the whole stage composes now.
        internal void RefreshComposition(string report)
        {
            if (string.IsNullOrEmpty(report))
            {
                return;
            }

            var sets = new List<UsdVariantSetInfo>();
            var infos = new List<UsdPayloadInfo>();
            var arcInfos = new List<UsdCompositionArcInfo>();
            ParseCompositionReport(report, sets, infos, arcInfos);
            SetComposition(sets, infos, arcInfos);
        }

        // The instance a prim below one belongs to, from the objects recorded above it.
        internal string FindRecordedInstanceRoot(string primPath)
        {
            var flags = new Dictionary<string, UsdPrimCompositionFlags>(StringComparer.Ordinal);
            foreach (UsdPrimSource prim in GetComponentsInChildren<UsdPrimSource>(true))
            {
                if (prim.Stage == this)
                {
                    flags[prim.PrimPath] = prim.CompositionFlags;
                }
            }

            for (int slash = primPath.LastIndexOf('/'); slash > 0; slash = primPath.LastIndexOf('/', slash - 1))
            {
                string ancestor = primPath.Substring(0, slash);
                if (flags.TryGetValue(ancestor, out UsdPrimCompositionFlags found) && (found & UsdPrimCompositionFlags.Instance) != 0)
                {
                    return ancestor;
                }
            }

            return null;
        }

        // The selection the import composed for a set, or null.
        internal string GetComposedSelection(string primPath, string variantSet)
        {
            foreach (UsdVariantSetInfo info in variantSets)
            {
                if (info.PrimPath == primPath && info.Name == variantSet)
                {
                    return info.Selection;
                }
            }

            return null;
        }

        // Parses RUsd_GetImportComposition's report (see the native header).
        internal static void ParseCompositionReport(
            string report,
            List<UsdVariantSetInfo> sets,
            List<UsdPayloadInfo> payloadInfos,
            List<UsdCompositionArcInfo> arcInfos)
        {
            if (string.IsNullOrEmpty(report))
            {
                return;
            }

            foreach (string line in report.Split('\n'))
            {
                string[] fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length < 2)
                {
                    continue;
                }

                switch (fields[0])
                {
                    case "V" when fields.Length >= 4:
                        var set = new UsdVariantSetInfo { PrimPath = fields[1], Name = fields[2], Selection = fields[3] };
                        for (int i = 4; i < fields.Length; i++)
                        {
                            set.Variants.Add(fields[i]);
                        }

                        sets.Add(set);
                        break;
                    case "P" when fields.Length >= 3:
                        payloadInfos.Add(new UsdPayloadInfo { PrimPath = fields[1], Loaded = fields[2] == "1" });
                        break;
                    case "A" when fields.Length >= 5:
                        arcInfos.Add(new UsdCompositionArcInfo
                        {
                            PrimPath = fields[1],
                            Kind = fields[2],
                            Layer = fields[3],
                            TargetPrimPath = fields[4]
                        });
                        break;
                }
            }
        }
    }
}
