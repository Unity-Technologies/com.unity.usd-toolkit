using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// The arithmetic both front-ends share, kept free of scene state so it is tested on its own:
    /// handle labels, display units, the mimic coupling, limits, and turning a mouse drag into a
    /// joint value.
    /// </summary>
    public static class SimReadyJointMath
    {
        /// <summary>The range a joint with no (or an infinite) limit gets for its handle.</summary>
        public const float UnlimitedDegrees = 180f;

        /// <summary>Below this many pixels per USD unit the joint's motion is edge-on to the view.</summary>
        public const float MinScreenPixelsPerUnit = 1e-3f;

        private static readonly HashSet<string> Sides = new HashSet<string>(StringComparer.Ordinal) { "left", "right", "l", "r" };
        private static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.Ordinal) { "joint", "jnt", "sm", "link" };

        // ---- The coupling ----

        /// <summary>
        /// Where a PhysxMimicJointAPI follower is held for a reference (leader) position, both in
        /// USD units: <c>follower + gearing · reference + offset = 0</c>, in radians for rotations
        /// (the offset is radians or stage units, as PhysX authors it).
        /// </summary>
        public static float FollowerPosition(float leader, float gearing, float offset, bool leaderAngular, bool followerAngular)
        {
            float reference = leaderAngular ? leader * Mathf.Deg2Rad : leader;
            float follower = -(gearing * reference) - offset;
            return followerAngular ? follower * Mathf.Rad2Deg : follower;
        }

        // ---- Limits ----

        /// <summary>
        /// The handle's range in USD units: the authored limits, or ±180° / ±<paramref name="unlimitedLength"/>
        /// for an axis with none. False when the axis is locked (lower above upper).
        /// </summary>
        public static bool TryRange(bool limitEnabled, float lower, float upper, bool angular, float unlimitedLength,
                                    out float min, out float max)
        {
            if (limitEnabled && lower > upper)
            {
                min = max = 0f;
                return false;
            }

            float span = angular ? UnlimitedDegrees : Mathf.Max(unlimitedLength, 1e-6f);
            min = limitEnabled && !float.IsInfinity(lower) && !float.IsNaN(lower) ? Mathf.Max(lower, -span * 1e3f) : -span;
            max = limitEnabled && !float.IsInfinity(upper) && !float.IsNaN(upper) ? Mathf.Min(upper, span * 1e3f) : span;
            if (angular)
            {
                min = Mathf.Max(min, -UnlimitedDegrees * 2f);
                max = Mathf.Min(max, UnlimitedDegrees * 2f);
            }

            return max > min;
        }

        public static float Clamp(float value, float min, float max) => float.IsNaN(value) ? min : Mathf.Clamp(value, min, max);

        // ---- Display ----

        /// <summary>The handle's value as the Space shows it: whole degrees, or whole millimetres.</summary>
        public static int DisplayValue(float usdValue, bool angular, double metersPerUnit) =>
            Mathf.RoundToInt(angular ? usdValue : (float)(usdValue * metersPerUnit * 1000.0));

        public static string Unit(bool angular) => angular ? "°" : " mm";

        /// <summary>"Fingers · 70°", "Drawer 01 · 294 mm".</summary>
        public static string Caption(string label, float usdValue, bool angular, double metersPerUnit) =>
            label + " · " + DisplayValue(usdValue, angular, metersPerUnit).ToString(CultureInfo.InvariantCulture) + Unit(angular);

        /// <summary>The badge at the joint origin: the value alone.</summary>
        public static string Badge(float usdValue, bool angular, double metersPerUnit) =>
            DisplayValue(usdValue, angular, metersPerUnit).ToString(CultureInfo.InvariantCulture) + Unit(angular).Trim();

        // ---- Labels ----

        /// <summary>
        /// A handle's label: the joint's displayName when it has one, else its prim name made
        /// readable. Words that only say "joint", repeat the asset's name or count from zero are
        /// dropped. A leader whose coupled followers move both sides of the asset (left and right)
        /// takes the word they share, plural: RG2's <c>finger_joint</c> and 2FG14's
        /// <c>finger_stroke</c>, which drive both fingers, are both "Fingers".
        /// </summary>
        public static string Label(string primName, string displayName, IEnumerable<string> followerNames, string movingBodyName, string assetName)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName.Trim();
            }

            var assetWords = new HashSet<string>(Words(assetName), StringComparer.Ordinal);
            List<string> words = Words(primName).Where(w => !Noise.Contains(w) && !assetWords.Contains(w) && !IsZeroCount(w)).ToList();
            List<string> followers = (followerNames ?? Array.Empty<string>()).ToList();

            var sides = new HashSet<string>(StringComparer.Ordinal);
            foreach (string word in Words(primName).Concat(Words(movingBodyName)).Concat(followers.SelectMany(Words)))
            {
                if (Sides.Contains(word))
                {
                    sides.Add(word.Length == 1 ? (word == "l" ? "left" : "right") : word);
                }
            }

            List<string> core = words.Where(w => !Sides.Contains(w)).ToList();
            if (followers.Count > 0 && sides.Count >= 2)
            {
                var shared = new HashSet<string>(followers.SelectMany(Words), StringComparer.Ordinal);
                List<string> common = core.Where(shared.Contains).ToList();
                if (common.Count > 0)
                {
                    core = common;
                }

                if (core.Count > 0)
                {
                    core[core.Count - 1] = Plural(core[core.Count - 1]);
                }
            }

            if (core.Count == 0)
            {
                core = words.Count > 0 ? words : new List<string> { primName ?? "Joint" };
            }

            string text = string.Join(" ", core);
            return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text.Substring(1) : "Joint";
        }

        private static IEnumerable<string> Words(string name) =>
            (name ?? string.Empty).ToLowerInvariant().Split(new[] { '_', '-', ' ', '.', ':' }, StringSplitOptions.RemoveEmptyEntries);

        private static bool IsZeroCount(string word) => word.Length > 0 && word.All(c => c == '0');

        private static string Plural(string word)
        {
            if (word.EndsWith("s", StringComparison.Ordinal))
            {
                return word;
            }

            return word.EndsWith("y", StringComparison.Ordinal) && word.Length > 1 && "aeiou".IndexOf(word[word.Length - 2]) < 0
                ? word.Substring(0, word.Length - 1) + "ies"
                : word + "s";
        }

        // ---- Dragging ----

        /// <summary>
        /// The change of a joint's value (USD units) for a mouse move from <paramref name="from"/>
        /// to <paramref name="to"/> (screen pixels): the move projected on where one unit of the
        /// joint moves the handle on screen (<paramref name="screenPerUnit"/>, the prismatic axis
        /// or the tangent of the revolute arc at the handle). When that is edge-on to the view
        /// (shorter than <see cref="MinScreenPixelsPerUnit"/>), vertical motion drives the joint
        /// at <paramref name="fallbackUnitsPerPixel"/> instead.
        /// </summary>
        public static float DragDelta(Vector2 from, Vector2 to, Vector2 screenPerUnit, float fallbackUnitsPerPixel)
        {
            Vector2 move = to - from;
            float length = screenPerUnit.sqrMagnitude;
            if (length < MinScreenPixelsPerUnit * MinScreenPixelsPerUnit)
            {
                return move.y * fallbackUnitsPerPixel;
            }

            return Vector2.Dot(move, screenPerUnit) / length;
        }

        /// <summary>The handle's screen motion for one USD unit: where it is drawn for value +1 minus where it is now.</summary>
        public static Vector2 ScreenPerUnit(Func<Vector3, Vector2> worldToScreen, Vector3 handleWorld, Vector3 worldMotionPerUnit, float step)
        {
            // A small step keeps the projection linear; it is scaled back to one unit.
            Vector2 a = worldToScreen(handleWorld);
            Vector2 b = worldToScreen(handleWorld + (worldMotionPerUnit * step));
            return (b - a) / step;
        }
    }
}
