using System;
using System.Collections.Generic;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// A prim of a <see cref="UsdStage"/>, by path. Holding one costs nothing and keeps nothing alive:
    /// every property asks the stage as it is now, so a prim removed since reads as invalid
    /// (<see cref="IsValid"/>) rather than failing.
    /// </summary>
    public readonly struct UsdPrim : IEquatable<UsdPrim>
    {
        internal UsdPrim(UsdStage stage, string path)
        {
            Stage = stage;
            Path = path;
        }

        public UsdStage Stage { get; }

        /// <summary>The prim's absolute path ("/World/Chair"); "/" for the pseudo-root.</summary>
        public string Path { get; }

        /// <summary>The last element of the path; empty for the pseudo-root.</summary>
        public string Name => Path == null || Path == "/" ? string.Empty : Path.Substring(Path.LastIndexOf('/') + 1);

        public bool IsPseudoRoot => Path == "/";

        /// <summary>Whether the stage composes a prim at <see cref="Path"/>.</summary>
        public bool IsValid => Stage != null && Path != null && Stage.IsOpen && (IsPseudoRoot || (Flags & UsdNative.PrimValid) != 0);

        public bool IsActive => (Flags & UsdNative.PrimActive) != 0;

        /// <summary>A def (or class), not only overs.</summary>
        public bool IsDefined => (Flags & UsdNative.PrimDefined) != 0;

        /// <summary>A class, or below one.</summary>
        public bool IsAbstract => (Flags & UsdNative.PrimAbstract) != 0;

        public bool IsLoaded => (Flags & UsdNative.PrimLoaded) != 0;

        /// <summary>The prim authors payloads (loaded or not).</summary>
        public bool HasPayload => (Flags & UsdNative.PrimHasPayload) != 0;

        public bool HasReferences => (Flags & UsdNative.PrimHasReferences) != 0;

        /// <summary>An instance: its contents come from a prototype shared with every other instance of it.</summary>
        public bool IsInstance => (Flags & UsdNative.PrimInstance) != 0;

        /// <summary>Below an instance: readable, and editable only through the instance's prototype.</summary>
        public bool IsInstanceProxy => (Flags & UsdNative.PrimInstanceProxy) != 0;

        public bool HasVariantSets => (Flags & UsdNative.PrimVariantSets) != 0;

        /// <summary>Its kind is a model kind (component, group, assembly).</summary>
        public bool IsModel => (Flags & UsdNative.PrimModel) != 0;

        public UsdSpecifier Specifier
        {
            get
            {
                RequirePath();
                string path = Path;
                int specifier = Stage.Run(context =>
                {
                    UsdNative.GetPrimInfo(context, path, out int found);
                    return found;
                });
                return specifier < 0 ? UsdSpecifier.Over : (UsdSpecifier)specifier;
            }
        }

        /// <summary>The schema type ("Xform", "Mesh"); empty for a typeless prim.</summary>
        public string TypeName => Query(UsdNative.QueryTypeName);

        /// <summary>The prim's kind ("component", "assembly"); empty when unauthored.</summary>
        public string Kind => GetMetadata("kind");

        /// <summary>The parent prim; for a root prim, the pseudo-root.</summary>
        public UsdPrim Parent
        {
            get
            {
                RequirePath();
                if (IsPseudoRoot)
                {
                    return new UsdPrim(Stage, null);
                }

                int slash = Path.LastIndexOf('/');
                return new UsdPrim(Stage, slash <= 0 ? "/" : Path.Substring(0, slash));
            }
        }

        /// <summary>Children that are active, defined and not abstract, instance proxies included.</summary>
        public IReadOnlyList<UsdPrim> Children => ChildrenOf(UsdNative.QueryChildren);

        /// <summary>Every composed child: inactive, over-only and class prims too.</summary>
        public IReadOnlyList<UsdPrim> AllChildren => ChildrenOf(UsdNative.QueryAllChildren);

        public UsdPrim GetChild(string name) => new UsdPrim(Stage, ChildPath(name));

        /// <summary>The attributes the prim has, authored or from its schema.</summary>
        public IReadOnlyList<UsdAttribute> Attributes
        {
            get
            {
                var attributes = new List<UsdAttribute>();
                foreach (string[] record in Records(Query(UsdNative.QueryProperties)))
                {
                    if (record.Length >= 2 && record[1] == "a")
                    {
                        attributes.Add(new UsdAttribute(this, record[0]));
                    }
                }

                return attributes;
            }
        }

        /// <summary>The relationships the prim has.</summary>
        public IReadOnlyList<UsdRelationship> Relationships
        {
            get
            {
                var relationships = new List<UsdRelationship>();
                foreach (string[] record in Records(Query(UsdNative.QueryProperties)))
                {
                    if (record.Length >= 2 && record[1] == "r")
                    {
                        relationships.Add(new UsdRelationship(this, record[0]));
                    }
                }

                return relationships;
            }
        }

        /// <summary>The attribute named <paramref name="name"/>; it need not exist yet (<see cref="UsdAttribute.Exists"/>).</summary>
        public UsdAttribute GetAttribute(string name) => new UsdAttribute(this, CheckName(name));

        public UsdRelationship GetRelationship(string name) => new UsdRelationship(this, CheckName(name));

        /// <summary>The prim's variant sets, with their selections.</summary>
        public IReadOnlyList<UsdVariantSet> VariantSets
        {
            get
            {
                var sets = new List<UsdVariantSet>();
                foreach (string[] record in Records(Query(UsdNative.QueryVariantSets)))
                {
                    var variants = new string[Math.Max(0, record.Length - 2)];
                    Array.Copy(record, Math.Min(2, record.Length), variants, 0, variants.Length);
                    sets.Add(new UsdVariantSet(this, record[0], record.Length > 1 ? record[1] : string.Empty, variants));
                }

                return sets;
            }
        }

        /// <summary>The variant set named <paramref name="name"/>, or null when the prim has none by that name.</summary>
        public UsdVariantSet GetVariantSet(string name)
        {
            foreach (UsdVariantSet set in VariantSets)
            {
                if (set.Name == name)
                {
                    return set;
                }
            }

            return null;
        }

        /// <summary>The string or token metadata field <paramref name="field"/> ("kind", "documentation"); empty when unauthored.</summary>
        public string GetMetadata(string field) => Query(UsdNative.QueryMetadata, field);

        // ---- Edits (to the stage's edit target) ----

        /// <summary>Sets a string or token metadata field ("kind", "documentation", "comment").</summary>
        public void SetMetadata(string field, string value) => Edit(UsdNative.EditMetadata, field, value ?? string.Empty);

        public void SetKind(string kind) => SetMetadata("kind", kind);

        public void SetTypeName(string typeName) => Edit(UsdNative.EditTypeName, null, typeName ?? string.Empty);

        public void SetActive(bool active) => Edit(UsdNative.EditActive, null, active ? "1" : "0");

        /// <summary>Removes this edit target's opinion on active.</summary>
        public void ClearActive() => Edit(UsdNative.EditActive, null, string.Empty);

        public void SetInstanceable(bool instanceable) => Edit(UsdNative.EditInstanceable, null, instanceable ? "1" : "0");

        public void ClearInstanceable() => Edit(UsdNative.EditInstanceable, null, string.Empty);

        /// <summary>
        /// Loads the prim's payloads and those below it. Changes what the stage composes, not any
        /// layer; a read-only import stage loads through <see cref="UsdStageSource.LoadPayload"/>.
        /// </summary>
        public void Load() => Edit(UsdNative.EditLoad, null, null);

        public void Unload() => Edit(UsdNative.EditUnload, null, null);

        /// <summary>Adds a variant to a set on the prim, making the set when needed.</summary>
        public void AddVariant(string variantSet, string variant) => Edit(UsdNative.EditAddVariant, variantSet, variant);

        /// <summary>Selects a variant; null or empty clears this edit target's selection.</summary>
        public void SetVariantSelection(string variantSet, string variant) =>
            Edit(UsdNative.EditSelectVariant, variantSet, variant ?? string.Empty);

        /// <summary>
        /// Adds a reference to <paramref name="primPath"/> in the file at <paramref name="assetPath"/>
        /// (its defaultPrim when null), or with a null asset path to a prim of this stage.
        /// The asset path is written as given; a path relative to the layer keeps files movable.
        /// </summary>
        public void AddReference(string assetPath, string primPath = null) =>
            Edit(UsdNative.EditAddReference, assetPath ?? string.Empty, primPath ?? string.Empty);

        /// <summary>Adds a payload, as <see cref="AddReference"/> adds a reference.</summary>
        public void AddPayload(string assetPath, string primPath = null) =>
            Edit(UsdNative.EditAddPayload, assetPath ?? string.Empty, primPath ?? string.Empty);

        public void ClearReferences() => Edit(UsdNative.EditClearReferences, null, null);

        public void ClearPayloads() => Edit(UsdNative.EditClearPayloads, null, null);

        public void AddInherit(string classPrimPath) => Edit(UsdNative.EditAddInherit, null, classPrimPath);

        public void AddSpecialize(string primPath) => Edit(UsdNative.EditAddSpecialize, null, primPath);

        /// <summary>Removes the property's spec from the edit target.</summary>
        public void RemoveProperty(string name) => Edit(UsdNative.EditRemoveProperty, CheckName(name), null);

        // ---- Transforms ----

        /// <summary>
        /// The prim's local transform at <paramref name="time"/>, as a Unity <see cref="Matrix4x4"/>
        /// (column vectors) of USD's own values: right-handed, in stage units. Identity for a prim that
        /// is not Xformable. <see cref="UsdCoordinates.ToUnity(Matrix4x4)"/> gives Unity's basis.
        /// </summary>
        public Matrix4x4 GetLocalTransform(UsdTimeCode time = default) => Transform(UsdNative.TransformLocal, time, out _);

        /// <summary>Like <see cref="GetLocalTransform"/>, and whether the prim resets the transform stack.</summary>
        public Matrix4x4 GetLocalTransform(UsdTimeCode time, out bool resetsXformStack) =>
            Transform(UsdNative.TransformLocal, time, out resetsXformStack);

        /// <summary>The prim's local-to-world transform at <paramref name="time"/>, in USD's values.</summary>
        public Matrix4x4 GetWorldTransform(UsdTimeCode time = default) => Transform(UsdNative.TransformWorld, time, out _);

        /// <summary>
        /// Writes the local transform at <paramref name="time"/> as one matrix op (replacing the
        /// prim's xformOpOrder). <paramref name="matrix"/> is in USD's values (see
        /// <see cref="UsdCoordinates.FromUnity(Matrix4x4)"/>).
        /// </summary>
        public void SetLocalTransform(Matrix4x4 matrix, UsdTimeCode time = default)
        {
            RequirePath();
            string path = Path;
            RUsdMatrix4x4 usd = UsdCoordinates.ToRowMajor(matrix);
            Stage.Edit(context => UsdNative.SetPrimTransform(context, path, time.Kind, time.Value, usd));
        }

        private Matrix4x4 Transform(int space, UsdTimeCode time, out bool resetsXformStack)
        {
            RequirePath();
            string path = Path;
            bool resets = false;
            RUsdMatrix4x4 matrix = Stage.Run(context => UsdNative.GetPrimTransform(context, path, space, time.Kind, time.Value, out resets));
            resetsXformStack = resets;
            return UsdCoordinates.FromRowMajor(matrix);
        }

        // ---- Plumbing ----

        private int Flags
        {
            get
            {
                if (Stage == null || Path == null || IsPseudoRoot)
                {
                    return 0;
                }

                string path = Path;
                return Stage.Run(context => UsdNative.GetPrimInfo(context, path, out _));
            }
        }

        private string Query(int query, string name = null)
        {
            RequirePath();
            string path = Path;
            return Stage.Run(context => UsdNative.QueryText(context, query, path, name));
        }

        private void Edit(int edit, string name, string value)
        {
            RequirePath();
            string path = Path;
            Stage.Edit(context => UsdNative.EditPrim(context, path, edit, name, value));
        }

        private IReadOnlyList<UsdPrim> ChildrenOf(int query)
        {
            var children = new List<UsdPrim>();
            foreach (string name in Query(query).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                children.Add(new UsdPrim(Stage, ChildPath(name)));
            }

            return children;
        }

        private string ChildPath(string name)
        {
            RequirePath();
            if (string.IsNullOrEmpty(name) || name.IndexOf('/') >= 0)
            {
                throw new ArgumentException($"'{name}' is not a prim name.", nameof(name));
            }

            return IsPseudoRoot ? "/" + name : Path + "/" + name;
        }

        internal static IEnumerable<string[]> Records(string text)
        {
            foreach (string line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return line.Split('\t');
            }
        }

        private static string CheckName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("The property name is empty.", nameof(name));
            }

            return name;
        }

        private void RequirePath()
        {
            if (Stage == null || Path == null)
            {
                throw new InvalidOperationException("This UsdPrim stands for no prim.");
            }
        }

        public bool Equals(UsdPrim other) => ReferenceEquals(Stage, other.Stage) && Path == other.Path;

        public override bool Equals(object obj) => obj is UsdPrim other && Equals(other);

        public override int GetHashCode() => Path?.GetHashCode() ?? 0;

        public override string ToString() => Path ?? "<no prim>";
    }

    /// <summary>A prim's variant set as the stage composed it: its selection and its variants.</summary>
    public sealed class UsdVariantSet
    {
        internal UsdVariantSet(UsdPrim prim, string name, string selection, string[] variants)
        {
            Prim = prim;
            Name = name;
            Selection = selection;
            Variants = variants;
        }

        public UsdPrim Prim { get; }

        public string Name { get; }

        /// <summary>The selected variant when this was read; empty when none is selected.</summary>
        public string Selection { get; }

        public IReadOnlyList<string> Variants { get; }

        /// <summary>Selects <paramref name="variant"/> in the stage's edit target (see <see cref="UsdPrim.SetVariantSelection"/>).</summary>
        public void Select(string variant) => Prim.SetVariantSelection(Name, variant);

        public override string ToString() => $"{Prim.Path}{{{Name}={Selection}}}";
    }

    /// <summary>A prim's relationship: paths it targets.</summary>
    public readonly struct UsdRelationship
    {
        internal UsdRelationship(UsdPrim prim, string name)
        {
            Prim = prim;
            Name = name;
        }

        public UsdPrim Prim { get; }

        public string Name { get; }

        /// <summary>The targets, forwarded ones resolved; empty when the relationship does not exist.</summary>
        public IReadOnlyList<string> Targets
        {
            get
            {
                UsdPrim prim = Prim;
                string name = Name;
                return prim.Stage.Run(context => UsdNative.QueryText(context, UsdNative.QueryTargets, prim.Path, name))
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        /// <summary>Replaces the targets in the edit target, making the relationship when needed.</summary>
        public void SetTargets(IEnumerable<string> targets)
        {
            UsdPrim prim = Prim;
            string name = Name;
            string text = string.Join("\n", targets ?? Array.Empty<string>());
            prim.Stage.Edit(context => UsdNative.EditPrim(context, prim.Path, UsdNative.EditSetTargets, name, text));
        }

        public override string ToString() => $"{Prim.Path}.{Name}";
    }
}
