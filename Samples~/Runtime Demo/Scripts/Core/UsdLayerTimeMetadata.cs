namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// The time metadata of a source stage: whether it has a time range, the range itself, and the
    /// rates it runs at.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="UsdStage"/>, so every format the toolkit opens works the same way:
    /// <c>.usda</c>, <c>.usdc</c>, text or binary <c>.usd</c>, and <c>.usdz</c>. The rates are the
    /// stage's effective values. USD falls back from timeCodesPerSecond to framesPerSecond to 24,
    /// and the stage API does not say which of them the layer authors.
    /// </remarks>
    public readonly struct UsdLayerTimeMetadata
    {
        /// <summary>Whether the stage authors both startTimeCode and endTimeCode.</summary>
        public readonly bool HasTimeRange;
        public readonly double StartTimeCode;
        public readonly double EndTimeCode;
        public readonly double TimeCodesPerSecond;
        public readonly double FramesPerSecond;

        private UsdLayerTimeMetadata(UsdStage stage)
        {
            HasTimeRange = stage.HasTimeRange;
            StartTimeCode = stage.StartTimeCode;
            EndTimeCode = stage.EndTimeCode;
            TimeCodesPerSecond = stage.TimeCodesPerSecond;
            FramesPerSecond = stage.FramesPerSecond;
        }

        /// <summary>
        /// Opens the stage at <paramref name="path"/> without its payloads and reads its time
        /// metadata. Throws what <see cref="UsdStage.Open"/> throws when the file cannot be opened.
        /// </summary>
        public static UsdLayerTimeMetadata Read(string path)
        {
            using (UsdStage stage = UsdStage.Open(path, loadPayloads: false))
            {
                return new UsdLayerTimeMetadata(stage);
            }
        }
    }
}
