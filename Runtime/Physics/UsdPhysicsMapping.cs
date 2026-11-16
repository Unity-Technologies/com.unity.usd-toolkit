namespace Unity.USDToolkit
{
    /// <summary>What the physics import made of one UsdPhysics prim.</summary>
    public enum UsdPhysicsMappingStatus
    {
        /// <summary>Built as authored.</summary>
        Mapped,

        /// <summary>Built, but Unity holds it differently from how it is authored (the detail says how).</summary>
        Approximated,

        /// <summary>Not built (the detail says why).</summary>
        Skipped
    }

    /// <summary>The kind of UsdPhysics object a <see cref="UsdPhysicsMappingEntry"/> describes.</summary>
    public enum UsdPhysicsPrimKind
    {
        Scene,
        Articulation,
        Body,
        Collider,
        Joint,
        Material,

        /// <summary>A schema or attribute the import read but has no Unity counterpart for (tendons, custom shapes).</summary>
        Other
    }

    /// <summary>
    /// One line of <see cref="UsdImportResult.PhysicsMapping"/>: a UsdPhysics prim, what Unity
    /// component it became, and whether that holds it as authored. Every physics prim the import
    /// read has an entry, so nothing is dropped without a record.
    /// </summary>
    public readonly struct UsdPhysicsMappingEntry
    {
        public UsdPhysicsMappingEntry(string primPath, UsdPhysicsPrimKind kind, UsdPhysicsMappingStatus status, string unity, string detail)
        {
            PrimPath = primPath ?? string.Empty;
            Kind = kind;
            Status = status;
            Unity = unity ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public string PrimPath { get; }

        public UsdPhysicsPrimKind Kind { get; }

        public UsdPhysicsMappingStatus Status { get; }

        /// <summary>The Unity component(s) built ("ArticulationBody", "MeshCollider ×3"); empty when skipped.</summary>
        public string Unity { get; }

        /// <summary>How it differs from what is authored, or why it was skipped; empty when mapped as authored.</summary>
        public string Detail { get; }

        public override string ToString() =>
            string.IsNullOrEmpty(Detail) ? $"{Status} {Kind} {PrimPath} -> {Unity}" : $"{Status} {Kind} {PrimPath} -> {Unity}: {Detail}";
    }
}
