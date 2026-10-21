using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>A stage's up axis.</summary>
    public enum UsdUpAxis
    {
        Y = 1,
        Z = 2,
    }

    /// <summary>How a prim is specified: defined, only overridden, or a class.</summary>
    public enum UsdSpecifier
    {
        Def = 0,
        Over = 1,
        Class = 2,
    }

    /// <summary>
    /// A USD stage kept open to read and edit: a file opened with <see cref="Open"/>, a new one made
    /// with <see cref="CreateNew"/> or <see cref="CreateInMemory"/>, or the stage an import keeps
    /// (<see cref="UsdStageSource.Stage"/>, read-only). Prims are reached by path
    /// (<see cref="GetPrimAtPath"/>, <see cref="Traverse"/>); their attributes read and write typed
    /// values at a time code. Edits go to the <see cref="EditTarget"/>, the root layer unless it is
    /// changed, and reach the file on <see cref="Save"/>.
    ///
    /// Values are USD's own: right-handed and in the stage's units, as authored. <see cref="UsdCoordinates"/>
    /// converts to Unity's basis the way the importer does.
    ///
    /// OpenUSD shares a file's layers between every stage in the process that uses them, imports
    /// included: an unsaved edit is seen by any other stage, or import, of the same file until it is
    /// saved or the stage is <see cref="Reload">reloaded</see>.
    ///
    /// One stage may be used from any thread, one call at a time (calls are serialized). Dispose it
    /// when done; the native stage is also released when the Editor reloads scripts or the player quits.
    /// Needs the 0.8.1 native plugin.
    /// </summary>
    public sealed class UsdStage : IDisposable
    {
        private static readonly List<WeakReference<UsdStage>> OpenStages = new List<WeakReference<UsdStage>>();

        private readonly StageHandle handle;
        private UsdEditTarget editTarget = UsdEditTarget.RootLayer;

        internal readonly object Gate = new object();

        private UsdStage(IntPtr context, bool readOnly, string path)
        {
            handle = new StageHandle(context);
            IsReadOnly = readOnly;
            Path = path;
            lock (OpenStages)
            {
                OpenStages.RemoveAll(reference => !reference.TryGetTarget(out _));
                OpenStages.Add(new WeakReference<UsdStage>(this));
            }
        }

        /// <summary>
        /// Opens the stage at <paramref name="path"/>, composing every payload unless
        /// <paramref name="loadPayloads"/> is false (then <see cref="UsdPrim.Load"/> loads them one by one).
        /// </summary>
        public static UsdStage Open(string path, bool loadPayloads = true)
        {
            string fullPath = CheckedFullPath(path);
            RequireStageApi();
            IntPtr context = UsdNative.OpenStageEx(fullPath, false, !loadPayloads, null, null, noRead: true);
            return new UsdStage(context, false, fullPath);
        }

        /// <summary>
        /// A new, empty stage that <see cref="Save"/> writes to <paramref name="path"/>, in the format
        /// its extension names (.usda, .usdc, .usd, and .abc where the payload reads Alembic).
        /// </summary>
        public static UsdStage CreateNew(string path)
        {
            string fullPath = string.IsNullOrEmpty(path)
                ? throw new ArgumentException("The stage path is empty.", nameof(path))
                : System.IO.Path.GetFullPath(path);
            RequireStageApi();
            return new UsdStage(UsdNative.CreateStage(fullPath, false), false, fullPath);
        }

        /// <summary>A new stage in memory only; <see cref="Export"/> writes it out.</summary>
        public static UsdStage CreateInMemory()
        {
            RequireStageApi();
            return new UsdStage(UsdNative.CreateStage(null, true), false, null);
        }

        // The stage an import keeps: opened with the choices the import composed with, walked by
        // nothing until a switch rereads part of it. Read-only to callers.
        internal static UsdStage OpenForImport(string fullPath, bool loadPayloads, IEnumerable<string> payloadsToLoad,
                                               IEnumerable<UsdVariantSelection> selections)
        {
            RequireStageApi();
            IntPtr context = UsdNative.OpenStageEx(fullPath, false, !loadPayloads, FormatSelections(selections),
                                                   payloadsToLoad != null ? string.Join("\n", payloadsToLoad) : null,
                                                   noRead: true);
            var stage = new UsdStage(context, true, fullPath);
            stage.SetEditTargetUnchecked(UsdEditTarget.SessionLayer);
            return stage;
        }

        // An import's own context, kept open as its stage (UsdImportOptions.KeepStageOpen).
        internal static UsdStage Adopt(IntPtr context, string fullPath)
        {
            var stage = new UsdStage(context, true, fullPath);
            stage.SetEditTargetUnchecked(UsdEditTarget.SessionLayer);
            return stage;
        }

        internal static string FormatSelections(IEnumerable<UsdVariantSelection> selections)
        {
            var text = new System.Text.StringBuilder();
            foreach (UsdVariantSelection selection in selections ?? Array.Empty<UsdVariantSelection>())
            {
                if (selection == null || string.IsNullOrEmpty(selection.PrimPath) || string.IsNullOrEmpty(selection.VariantSet))
                {
                    continue;
                }

                text.Append(selection.PrimPath).Append('\t').Append(selection.VariantSet).Append('\t')
                    .Append(selection.Variant ?? string.Empty).Append('\n');
            }

            return text.ToString();
        }

        /// <summary>The file the stage was opened from or is saved to; null for an in-memory stage.</summary>
        public string Path { get; }

        /// <summary>
        /// True for the stage an import keeps (<see cref="UsdStageSource.Stage"/>): it reads like any
        /// other, and changes only through the UsdStageSource switch methods, so the scene, the stage
        /// and a Preserve export of them keep agreeing.
        /// </summary>
        public bool IsReadOnly { get; }

        /// <summary>False once disposed.</summary>
        public bool IsOpen => !handle.IsClosed;

        /// <summary>The pseudo-root ("/"), parent of the root prims.</summary>
        public UsdPrim PseudoRoot => new UsdPrim(this, "/");

        /// <summary>The stage's defaultPrim; an invalid prim when it names none. Setting it needs a root prim.</summary>
        public UsdPrim DefaultPrim
        {
            get
            {
                string path = Run(context => UsdNative.QueryText(context, UsdNative.QueryDefaultPrim, null));
                return new UsdPrim(this, string.IsNullOrEmpty(path) ? null : path);
            }
            set => Edit(context => UsdNative.EditPrim(context, value.Path, UsdNative.EditDefaultPrim));
        }

        /// <summary>The layers the stage composes from, strongest first: the session layer, the root layer, its sublayers.</summary>
        public IReadOnlyList<string> LayerStack =>
            Run(context => UsdNative.QueryText(context, UsdNative.QueryLayers, null))
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        public double StartTimeCode
        {
            get => Info.StartTimeCode;
            set => SetMetadata(UsdNative.StageStartTimeCode, value);
        }

        public double EndTimeCode
        {
            get => Info.EndTimeCode;
            set => SetMetadata(UsdNative.StageEndTimeCode, value);
        }

        /// <summary>Whether startTimeCode and endTimeCode are authored.</summary>
        public bool HasTimeRange => Info.HasTimeRange != 0;

        public double TimeCodesPerSecond
        {
            get => Info.TimeCodesPerSecond;
            set => SetMetadata(UsdNative.StageTimeCodesPerSecond, value);
        }

        public double FramesPerSecond
        {
            get => Info.FramesPerSecond;
            set => SetMetadata(UsdNative.StageFramesPerSecond, value);
        }

        public double MetersPerUnit
        {
            get => Info.MetersPerUnit;
            set => SetMetadata(UsdNative.StageMetersPerUnit, value);
        }

        public UsdUpAxis UpAxis
        {
            get => Info.UpAxis == 2 ? UsdUpAxis.Z : UsdUpAxis.Y;
            set => SetMetadata(UsdNative.StageUpAxis, (double)value);
        }

        /// <summary>Whether a layer the stage uses has edits not yet saved.</summary>
        public bool HasUnsavedChanges => Info.Dirty != 0;

        /// <summary>Where edits go; <see cref="UsdEditTarget.RootLayer"/> until changed.</summary>
        public UsdEditTarget EditTarget
        {
            get => editTarget;
            set
            {
                lock (Gate)
                {
                    EnsureWritable();
                    SetEditTargetUnchecked(value ?? throw new ArgumentNullException(nameof(value)));
                }
            }
        }

        /// <summary>The prim at <paramref name="path"/>; check <see cref="UsdPrim.IsValid"/>, as there may be none.</summary>
        public UsdPrim GetPrimAtPath(string path) => new UsdPrim(this, CheckPath(path));

        /// <summary>Defines a prim (and any missing ancestors), of <paramref name="typeName"/> ("Xform", "Mesh") or typeless.</summary>
        public UsdPrim DefinePrim(string path, string typeName = null)
        {
            Edit(context => UsdNative.EditPrim(context, CheckPath(path), UsdNative.EditDefine, null, typeName ?? string.Empty));
            return new UsdPrim(this, path);
        }

        /// <summary>Authors an `over` for the prim when the stage composes none there.</summary>
        public UsdPrim OverridePrim(string path)
        {
            Edit(context => UsdNative.EditPrim(context, CheckPath(path), UsdNative.EditOverride));
            return new UsdPrim(this, path);
        }

        /// <summary>Authors a class at a root prim path.</summary>
        public UsdPrim CreateClassPrim(string path)
        {
            Edit(context => UsdNative.EditPrim(context, CheckPath(path), UsdNative.EditClass));
            return new UsdPrim(this, path);
        }

        /// <summary>
        /// Removes the prim's spec, and everything below it, from the edit target. What weaker layers
        /// or references say about the prim still composes; deactivate it to hide it from them all.
        /// </summary>
        public void RemovePrim(string path) => Edit(context => UsdNative.EditPrim(context, CheckPath(path), UsdNative.EditRemove));

        /// <summary>
        /// Every prim below the pseudo-root, depth first: active, defined and not abstract, instance
        /// proxies included (as the importer walks), or with <paramref name="allPrims"/> every prim.
        /// </summary>
        public IEnumerable<UsdPrim> Traverse(bool allPrims = false)
        {
            var pending = new Stack<UsdPrim>();
            PushChildren(pending, PseudoRoot, allPrims);
            while (pending.Count > 0)
            {
                UsdPrim prim = pending.Pop();
                yield return prim;
                PushChildren(pending, prim, allPrims);
            }
        }

        private static void PushChildren(Stack<UsdPrim> pending, UsdPrim prim, bool allPrims)
        {
            IReadOnlyList<UsdPrim> children = allPrims ? prim.AllChildren : prim.Children;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }

        /// <summary>Saves every edited layer the stage uses (the session layer excepted).</summary>
        public void Save() => Edit(UsdNative.SaveStage);

        /// <summary>
        /// Writes the root layer -- or with <paramref name="flatten"/> the whole composed stage, as one
        /// layer with no composition left -- to <paramref name="path"/>. The stage is unchanged.
        /// </summary>
        public void Export(string path, bool flatten = false)
        {
            string fullPath = string.IsNullOrEmpty(path)
                ? throw new ArgumentException("The export path is empty.", nameof(path))
                : System.IO.Path.GetFullPath(path);
            Run(context =>
            {
                UsdNative.ExportStage(context, fullPath, flatten);
                return 0;
            });
        }

        /// <summary>
        /// Rereads every layer from disk, dropping unsaved edits -- for every stage and import in the
        /// process that shares them.
        /// </summary>
        public void Reload() => Edit(UsdNative.ReloadStage);

        public void Dispose()
        {
            lock (Gate)
            {
                handle.Dispose();
            }
        }

        // Releases every stage still open: the Editor is about to reload scripts (and with them
        // every managed reference to a stage), or the player is quitting.
        internal static void CloseAll()
        {
            List<WeakReference<UsdStage>> stages;
            lock (OpenStages)
            {
                stages = new List<WeakReference<UsdStage>>(OpenStages);
                OpenStages.Clear();
            }

            foreach (WeakReference<UsdStage> reference in stages)
            {
                if (reference.TryGetTarget(out UsdStage stage))
                {
                    stage.Dispose();
                }
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorReload()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += CloseAll;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += UsdImporter.ReleasePreviewStage;
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void HookQuit()
        {
            Application.quitting -= CloseAll;
            Application.quitting += CloseAll;
        }

        // ---- Plumbing shared with UsdPrim and UsdAttribute ----

        private RUsdStageInfo Info => Run(UsdNative.GetStageInfo);

        // Runs a native call on the open stage, one at a time.
        internal T Run<T>(Func<IntPtr, T> call)
        {
            lock (Gate)
            {
                return call(Context);
            }
        }

        // An edit: refused on a read-only stage.
        internal void Edit(Action<IntPtr> call)
        {
            lock (Gate)
            {
                EnsureWritable();
                call(Context);
            }
        }

        // The native context; only under Gate.
        internal IntPtr Context
        {
            get
            {
                if (handle.IsClosed)
                {
                    throw new ObjectDisposedException(nameof(UsdStage));
                }

                return handle.DangerousGetHandle();
            }
        }

        internal void EnsureWritable()
        {
            if (IsReadOnly)
            {
                throw new InvalidOperationException(
                    "This stage is the one an import keeps; it changes through its UsdStageSource (SwitchVariant, " +
                    "SwitchPayloads, LoadPayload), so the scene and a Preserve export keep agreeing with it. Open the file " +
                    "with UsdStage.Open to edit it.");
            }
        }

        // Under Gate.
        internal void SetEditTargetUnchecked(UsdEditTarget target)
        {
            UsdNative.SetEditTarget(Context, target.Kind, target.LayerIdentifier, target.VariantPrimPath, target.VariantSet,
                                    target.Variant);
            editTarget = target;
        }

        private void SetMetadata(int field, double value) => Edit(context => UsdNative.SetStageMetadata(context, field, value));

        private static void RequireStageApi()
        {
            if (!UsdNative.IsSupportedPlatform)
            {
                throw new PlatformNotSupportedException("UsdStage is available on Windows x64, macOS and Linux x64.");
            }

            UsdExporter.EnsureRuntimeConfigured();
            if (!UsdNative.SupportsStageApi)
            {
                throw new NotSupportedException(
                    $"UsdStage needs {UsdNative.NeedsPlugin(UsdNative.StageApiVersion)}; {UsdNative.LoadedPluginDescription}. Rebuild the native plugin for this platform (see Native~/REBUILD_WINDOWS_LINUX.md).");
            }
        }

        private static string CheckedFullPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("The stage path is empty.", nameof(path));
            }

            string fullPath = System.IO.Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("No USD file at the path.", fullPath);
            }

            return fullPath;
        }

        internal static string CheckPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/')
            {
                throw new ArgumentException($"'{path}' is not an absolute prim path.", nameof(path));
            }

            return path;
        }

        // Releases the native stage whenever the managed one goes away, whichever thread that is on.
        private sealed class StageHandle : SafeHandle
        {
            public StageHandle(IntPtr context)
                : base(IntPtr.Zero, true)
            {
                SetHandle(context);
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle()
            {
                UsdNative.Destroy(handle);
                return true;
            }
        }
    }

    /// <summary>
    /// Where a <see cref="UsdStage"/>'s edits go: its root layer, its session layer (edits that change
    /// what this stage composes without touching any file), a sublayer of the root, and optionally
    /// inside one variant of a prim there.
    /// </summary>
    public sealed class UsdEditTarget
    {
        public static readonly UsdEditTarget RootLayer = new UsdEditTarget(UsdNative.EditTargetRoot, null, null, null, null);

        public static readonly UsdEditTarget SessionLayer = new UsdEditTarget(UsdNative.EditTargetSession, null, null, null, null);

        private UsdEditTarget(int kind, string layer, string variantPrimPath, string variantSet, string variant)
        {
            Kind = kind;
            LayerIdentifier = layer;
            VariantPrimPath = variantPrimPath;
            VariantSet = variantSet;
            Variant = variant;
        }

        /// <summary>A layer of the root layer stack, by identifier or file path (see <see cref="UsdStage.LayerStack"/>).</summary>
        public static UsdEditTarget Layer(string identifier) =>
            new UsdEditTarget(UsdNative.EditTargetLayer,
                              string.IsNullOrEmpty(identifier) ? throw new ArgumentException("The layer is empty.", nameof(identifier)) : identifier,
                              null, null, null);

        /// <summary>
        /// This layer, inside <paramref name="variant"/> of <paramref name="variantSet"/> on the prim:
        /// what is authored there composes only while the variant is selected. The variant must
        /// exist and be the selected one when the target is set.
        /// </summary>
        public UsdEditTarget InVariant(string primPath, string variantSet, string variant)
        {
            if (string.IsNullOrEmpty(primPath) || string.IsNullOrEmpty(variantSet) || string.IsNullOrEmpty(variant))
            {
                throw new ArgumentException("A variant edit target names a prim, a variant set and a variant.");
            }

            return new UsdEditTarget(Kind, LayerIdentifier, primPath, variantSet, variant);
        }

        internal int Kind { get; }

        public string LayerIdentifier { get; }

        public string VariantPrimPath { get; }

        public string VariantSet { get; }

        public string Variant { get; }

        public override string ToString()
        {
            string layer = Kind == UsdNative.EditTargetRoot ? "root layer" : Kind == UsdNative.EditTargetSession ? "session layer" : LayerIdentifier;
            return VariantSet == null ? layer : $"{layer}, {VariantPrimPath}{{{VariantSet}={Variant}}}";
        }
    }

    /// <summary>
    /// When a value is read or written: the default (the value outside time, what <c>default</c>
    /// gives), a time code, or (reading) the earliest time, which gives an attribute's first sample
    /// when it has samples and its default when it has none. A double converts to a time code.
    /// </summary>
    public readonly struct UsdTimeCode : IEquatable<UsdTimeCode>
    {
        private readonly double value;
        private readonly int kind;

        private UsdTimeCode(int kind, double value)
        {
            this.kind = kind;
            this.value = value;
        }

        public UsdTimeCode(double timeCode)
            : this(UsdNative.TimeCode, timeCode)
        {
        }

        public static UsdTimeCode Default => default;

        public static UsdTimeCode EarliestTime => new UsdTimeCode(UsdNative.TimeEarliest, 0.0);

        public static implicit operator UsdTimeCode(double timeCode) => new UsdTimeCode(timeCode);

        public bool IsDefault => kind == UsdNative.TimeDefault;

        public bool IsEarliestTime => kind == UsdNative.TimeEarliest;

        /// <summary>The time code; 0 for the default and the earliest time.</summary>
        public double Value => value;

        internal int Kind => kind;

        public bool Equals(UsdTimeCode other) => kind == other.kind && value.Equals(other.value);

        public override bool Equals(object obj) => obj is UsdTimeCode other && Equals(other);

        public override int GetHashCode() => (kind * 397) ^ value.GetHashCode();

        public override string ToString() => IsDefault ? "Default" : IsEarliestTime ? "EarliestTime" : value.ToString("R");
    }
}
