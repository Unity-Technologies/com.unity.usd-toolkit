using System.Runtime.CompilerServices;

// UsdImportOptions.StageWarningCallback is internal: the USD asset importer (Editor) routes the
// import's stage warnings to its import context, and the editor tests collect them the same way.
[assembly: InternalsVisibleTo("Unity.USDToolkit.Editor")]
[assembly: InternalsVisibleTo("Unity.USDToolkit.Tests.Editor")]
