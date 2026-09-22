namespace Unity.USDToolkit
{
    public sealed class UsdRuntimeInfo
    {
        internal UsdRuntimeInfo(
            bool isSupportedPlatform,
            string nativeDllName,
            int nativeApiVersion,
            string openUsdVersion,
            string nativeDllSearchPath,
            string pluginSearchPath)
        {
            IsSupportedPlatform = isSupportedPlatform;
            NativeDllName = nativeDllName;
            NativeApiVersion = nativeApiVersion;
            OpenUsdVersion = openUsdVersion;
            NativeDllSearchPath = nativeDllSearchPath;
            PluginSearchPath = pluginSearchPath;
        }

        public bool IsSupportedPlatform { get; }
        public string NativeDllName { get; }
        public int NativeApiVersion { get; }
        public string OpenUsdVersion { get; }
        public string NativeDllSearchPath { get; }
        public string PluginSearchPath { get; }

        public override string ToString()
        {
            return $"{NativeDllName} API {NativeApiVersion}, OpenUSD {OpenUsdVersion}";
        }
    }
}

