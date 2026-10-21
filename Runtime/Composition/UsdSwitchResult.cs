using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// What a <see cref="UsdStageSource"/> switch did. Usually the import stays where it is and only
    /// the parts the switch recomposed are rebuilt (<see cref="RebuiltPrimPaths"/>); when that cannot
    /// be done the whole stage is imported again in the import's place (<see cref="Reimported"/>,
    /// with the reason), and the old root is destroyed.
    /// </summary>
    public sealed class UsdSwitchResult
    {
        internal UsdSwitchResult(GameObject root, IReadOnlyList<string> rebuilt, string reimportReason, UsdImportResult import)
        {
            RootObject = root;
            RebuiltPrimPaths = rebuilt ?? Array.Empty<string>();
            ReimportReason = reimportReason;
            Import = import;
        }

        /// <summary>The import's root after the switch: the same object, or the new one after a reimport.</summary>
        public GameObject RootObject { get; }

        /// <summary>The prims whose subtrees were rebuilt. Empty after a reimport.</summary>
        public IReadOnlyList<string> RebuiltPrimPaths { get; }

        /// <summary>True when the whole stage was imported again.</summary>
        public bool Reimported => Import != null;

        /// <summary>
        /// Why the whole stage was imported again: a native plugin older than 0.8.1, a prefab
        /// instance in Edit Mode, a stage changed on disk since the import, or a change that reaches
        /// past one part of the stage (a material, a skeleton animation). Null otherwise.
        /// </summary>
        public string ReimportReason { get; }

        /// <summary>The new import, after a reimport; null otherwise.</summary>
        public UsdImportResult Import { get; }

        /// <summary>The import's UsdStageSource after the switch.</summary>
        public UsdStageSource Source => RootObject != null ? RootObject.GetComponent<UsdStageSource>() : null;
    }
}
