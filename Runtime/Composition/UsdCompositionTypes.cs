using System;
using System.Collections.Generic;

namespace Unity.USDToolkit
{
    /// <summary>One variant choice: on the prim at <see cref="PrimPath"/>, set <see cref="VariantSet"/> to <see cref="Variant"/>.</summary>
    [Serializable]
    public sealed class UsdVariantSelection
    {
        public string PrimPath;
        public string VariantSet;
        public string Variant;

        public UsdVariantSelection()
        {
        }

        public UsdVariantSelection(string primPath, string variantSet, string variant)
        {
            PrimPath = primPath;
            VariantSet = variantSet;
            Variant = variant;
        }

        public override string ToString() => PrimPath + " {" + VariantSet + "=" + Variant + "}";
    }

    /// <summary>A variant set an imported stage offers, with the selection the import composed.</summary>
    [Serializable]
    public sealed class UsdVariantSetInfo
    {
        public string PrimPath;
        public string Name;
        public string Selection;
        public List<string> Variants = new List<string>();
    }

    /// <summary>A direct composition arc on an imported prim (reference, payload, inherit, specialize, relocate).</summary>
    [Serializable]
    public sealed class UsdCompositionArcInfo
    {
        public string PrimPath;
        public string Kind;
        public string Layer;
        public string TargetPrimPath;
    }

    /// <summary>A prim with a payload, and whether the import loaded it.</summary>
    [Serializable]
    public sealed class UsdPayloadInfo
    {
        public string PrimPath;
        public bool Loaded;
    }

    /// <summary>Composition facts about one imported prim (the native RUSD_NODE_* bits).</summary>
    [Flags]
    public enum UsdPrimCompositionFlags
    {
        None = 0,
        Instance = 1,
        InstanceProxy = 2,
        Payload = 4,
        Unloaded = 8,
        References = 16,
        VariantSets = 32,
        Inherits = 64
    }
}
