using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Marks a GameObject the importer created only to turn a light or camera to Unity's
    /// convention. USD lights emit and cameras look along their local -Z; Unity's along +Z, so the
    /// component sits on a child turned 180 degrees about Y. The child is not a prim: exporters
    /// skip it and everything below it.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdImportHelperObject : MonoBehaviour
    {
    }
}
