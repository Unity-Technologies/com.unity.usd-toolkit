using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// How the demo reconciles a source stage's up-axis and units with Unity's Y-up metres.
    /// </summary>
    /// <remarks>
    /// Since com.unity.usd-toolkit 0.8.0-exp.1 the importer can do this itself
    /// (<c>UsdImportOptions.ApplyStageUpAxisAndUnits</c>, on by default), but only as one switch
    /// for both. The demo turns that off and applies its own correction so up-axis and units can
    /// be toggled independently; leaving both on scales a centimetre stage by 0.0001 and turns a
    /// Z-up stage 180 degrees. Handedness (USD right-handed to Unity left-handed) is handled in
    /// the native layer, so this type deliberately leaves the basis alone.
    /// </remarks>
    public enum UsdConversionMode
    {
        /// <summary>Read the stage metadata and correct when it disagrees with Unity.</summary>
        Auto = 0,

        /// <summary>Import exactly what the toolkit produces. Useful to show the difference.</summary>
        Off = 1
    }

    /// <summary>
    /// The correction applied to one imported stage, kept so the UI and the exporter can both
    /// describe and reuse it.
    /// </summary>
    public readonly struct UsdStageConversion
    {
        public readonly bool RotatedToYUp;
        public readonly float UnitScale;
        public readonly string SourceUpAxis;
        public readonly double SourceMetersPerUnit;

        public UsdStageConversion(bool rotatedToYUp, float unitScale, string sourceUpAxis, double sourceMetersPerUnit)
        {
            RotatedToYUp = rotatedToYUp;
            UnitScale = unitScale;
            SourceUpAxis = sourceUpAxis;
            SourceMetersPerUnit = sourceMetersPerUnit;
        }

        public bool IsIdentity => !RotatedToYUp && Mathf.Approximately(UnitScale, 1.0f);

        /// <summary>
        /// A rotation about X of -90 degrees maps +Z onto +Y in Unity's left-handed basis, which
        /// is the Z-up to Y-up correction.
        /// </summary>
        public static readonly Quaternion ZUpToYUp = Quaternion.Euler(-90.0f, 0.0f, 0.0f);

        public static UsdStageConversion Resolve(
            string stageUpAxis,
            double stageMetersPerUnit,
            UsdConversionMode upAxisMode,
            UsdConversionMode unitMode)
        {
            bool stageIsZUp = string.Equals(stageUpAxis, "Z", System.StringComparison.OrdinalIgnoreCase);
            bool rotate = upAxisMode == UsdConversionMode.Auto && stageIsZUp;

            float scale = 1.0f;
            if (unitMode == UsdConversionMode.Auto && stageMetersPerUnit > 0.0 &&
                !Mathf.Approximately((float)stageMetersPerUnit, 1.0f))
            {
                scale = (float)stageMetersPerUnit;
            }

            return new UsdStageConversion(rotate, scale, stageUpAxis, stageMetersPerUnit);
        }

        /// <summary>Writes this correction onto the dedicated correction transform.</summary>
        public void ApplyTo(Transform correctionTransform)
        {
            correctionTransform.localPosition = Vector3.zero;
            correctionTransform.localRotation = RotatedToYUp ? ZUpToYUp : Quaternion.identity;
            correctionTransform.localScale = Vector3.one * UnitScale;
        }

        public string Describe()
        {
            if (IsIdentity)
            {
                return "Y-up / metres, no correction";
            }

            var parts = new System.Text.StringBuilder();
            if (RotatedToYUp)
            {
                parts.Append("Z-up corrected");
            }

            if (!Mathf.Approximately(UnitScale, 1.0f))
            {
                if (parts.Length > 0)
                {
                    parts.Append(", ");
                }

                parts.Append(SourceMetersPerUnit.ToString("0.####")).Append(" m/unit scaled");
            }

            return parts.ToString();
        }
    }
}
