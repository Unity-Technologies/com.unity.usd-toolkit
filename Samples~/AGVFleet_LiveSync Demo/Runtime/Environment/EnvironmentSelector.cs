using System;
using System.IO;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    public enum EnvironmentKind
    {
        Factory,
        Generated,
    }

    /// <summary>The environment to run, and why.</summary>
    public readonly struct EnvironmentChoice
    {
        public readonly EnvironmentKind Kind;
        public readonly string FactoryUsdPath;   // resolved, whether or not it exists
        public readonly bool FactoryFound;
        /// <summary>One message for the log when the choice needs explaining; null otherwise.</summary>
        public readonly string Message;
        public readonly bool MessageIsError;

        public EnvironmentChoice(EnvironmentKind kind, string factoryUsdPath, bool factoryFound, string message, bool isError)
        {
            Kind = kind;
            FactoryUsdPath = factoryUsdPath;
            FactoryFound = factoryFound;
            Message = message;
            MessageIsError = isError;
        }
    }

    /// <summary>
    /// Mode selection and path resolution. Pure functions (file system access is injected) so the rules
    /// are testable: Auto picks the factory when its USD exists and falls back to the generated layout
    /// with one message; an explicit mode is always respected.
    /// </summary>
    public static class EnvironmentSelector
    {
        /// <summary>
        /// Resolves the asset pack folder: the override (environment variable) wins, rooted paths are used
        /// as they are, relative paths are resolved against <paramref name="baseDir"/>.
        /// </summary>
        public static string ResolvePackDir(string configured, string overrideValue, string baseDir)
        {
            string p = !string.IsNullOrWhiteSpace(overrideValue) ? overrideValue.Trim() : (configured ?? "").Trim();
            if (p.Length == 0) return "";
            return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p));
        }

        public static string FactoryUsdPath(string packDir, string factoryUsdInPack)
        {
            if (string.IsNullOrEmpty(packDir) || string.IsNullOrEmpty(factoryUsdInPack)) return "";
            return Path.GetFullPath(Path.Combine(packDir, factoryUsdInPack));
        }

        public static EnvironmentChoice Choose(EnvironmentMode mode, string factoryUsdPath, Func<string, bool> fileExists)
        {
            bool found = !string.IsNullOrEmpty(factoryUsdPath) && fileExists(factoryUsdPath);
            string where = string.IsNullOrEmpty(factoryUsdPath) ? "(no asset pack path set)" : factoryUsdPath;
            switch (mode)
            {
                case EnvironmentMode.Factory:
                    return found
                        ? new EnvironmentChoice(EnvironmentKind.Factory, factoryUsdPath, true, null, false)
                        : new EnvironmentChoice(EnvironmentKind.Factory, factoryUsdPath, false,
                            $"[AGV] Environment is set to Factory, but the factory USD was not found at {where}. " +
                            $"Download the {AgvFleetSettings.AssetPackName} ({AgvFleetSettings.AssetPackUrl}), unzip it and " +
                            "set Asset Pack Path in the AGV Fleet Settings asset, or set Environment to Auto or Generated.", true);
                case EnvironmentMode.Generated:
                    return new EnvironmentChoice(EnvironmentKind.Generated, factoryUsdPath, found, null, false);
                default:
                    return found
                        ? new EnvironmentChoice(EnvironmentKind.Factory, factoryUsdPath, true, null, false)
                        : new EnvironmentChoice(EnvironmentKind.Generated, factoryUsdPath, false,
                            $"[AGV] Factory USD not found at {where}: using the generated layout. For the NVIDIA factory, " +
                            $"download the {AgvFleetSettings.AssetPackName} ({AgvFleetSettings.AssetPackUrl}) and unzip it there " +
                            "(or set Asset Pack Path in the AGV Fleet Settings asset).", false);
            }
        }
    }
}
