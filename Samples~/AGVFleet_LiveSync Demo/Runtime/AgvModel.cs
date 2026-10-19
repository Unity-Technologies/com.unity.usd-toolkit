using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// The AGV's physical size, shared by the model, the planner and the follower. Robot frame (USD):
    /// origin at the drive-axle midpoint at axle height, +X forward, +Y left. Same values as
    /// Tools~/agv_fleet/config.py, so the Unity model and the Isaac Sim robot agree.
    /// </summary>
    public static class AgvSpec
    {
        public const float WheelRadius = 0.081f;
        public const float WheelBase = 0.579f;            // distance between the drive wheels
        public const float FootprintRear = -1.033f;       // x of the rear edge
        public const float FootprintFront = 0.398f;       // x of the front edge
        public const float HalfWidth = 0.330f;
        public const float Height = 0.30f;
        public const float AxleHeight = WheelRadius;      // z of the robot origin above the floor

        public static float Length => FootprintFront - FootprintRear;
        public static float CenterX => (FootprintFront + FootprintRear) * 0.5f;
    }

    /// <summary>
    /// A low, flat AGV built from primitives, matching <see cref="AgvSpec"/>. Built in the Unity frame;
    /// <see cref="AgvView"/> tints the body and spins the two drive-wheel pivots
    /// (<c>left_wheel</c>, <c>right_wheel</c>) about their axle (Unity +Z).
    /// </summary>
    public static class AgvModel
    {
        public const string LeftWheel = "left_wheel";
        public const string RightWheel = "right_wheel";
        /// <summary>Parts under this group are drawn dark (bumpers, scanner), under <see cref="TopGroup"/> light grey.</summary>
        public const string TrimGroup = "trim";
        public const string TopGroup = "top";

        /// <summary>Creates an inactive template to clone per AGV.</summary>
        public static GameObject CreateTemplate(Transform parent)
        {
            var root = new GameObject("AGV template (primitive)");
            root.transform.SetParent(parent, false);
            root.SetActive(false);

            float cx = AgvSpec.CenterX, len = AgvSpec.Length, w = AgvSpec.HalfWidth * 2f;
            float floor = -AgvSpec.AxleHeight;
            Transform body = Group(root.transform, "body");
            Part(body, PrimitiveType.Cube, "chassis", new Vector3(cx, floor + 0.17f, 0f), new Vector3(len, 0.20f, w));
            Transform deck = Group(root.transform, TopGroup);
            Part(deck, PrimitiveType.Cube, "deck", new Vector3(cx - 0.02f, floor + 0.285f, 0f), new Vector3(len - 0.12f, 0.03f, w - 0.08f));
            Part(body, PrimitiveType.Cube, "lift_plate", new Vector3(cx - 0.05f, floor + 0.31f, 0f), new Vector3(0.9f, 0.02f, 0.5f));

            Transform trim = Group(root.transform, TrimGroup);
            Part(trim, PrimitiveType.Cube, "bumper_front", new Vector3(AgvSpec.FootprintFront - 0.015f, floor + 0.11f, 0f), new Vector3(0.03f, 0.09f, w + 0.01f));
            Part(trim, PrimitiveType.Cube, "bumper_rear", new Vector3(AgvSpec.FootprintRear + 0.015f, floor + 0.11f, 0f), new Vector3(0.03f, 0.09f, w + 0.01f));
            Part(trim, PrimitiveType.Cube, "scanner", new Vector3(AgvSpec.FootprintFront - 0.08f, floor + 0.29f, 0f), new Vector3(0.08f, 0.05f, 0.12f));

            Wheel(root.transform, LeftWheel, +AgvSpec.WheelBase * 0.5f);
            Wheel(root.transform, RightWheel, -AgvSpec.WheelBase * 0.5f);
            Caster(root.transform, "caster_left", new Vector3(AgvSpec.FootprintRear + 0.18f, 0f, 0.22f));
            Caster(root.transform, "caster_right", new Vector3(AgvSpec.FootprintRear + 0.18f, 0f, -0.22f));
            return root;
        }

        private static Transform Group(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static Transform Part(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>()); // AgvView adds one pick box for the whole AGV
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            return go.transform;
        }

        /// <summary>A drive wheel: a pivot on the axle (USD +Y = Unity +Z) with a tyre and a hub mark.</summary>
        private static void Wheel(Transform parent, string name, float lateral)
        {
            // USD y (left) is Unity z
            Transform pivot = Group(parent, name);
            pivot.localPosition = new Vector3(0f, 0f, lateral);
            float d = AgvSpec.WheelRadius * 2f;
            Transform tyre = Part(pivot, PrimitiveType.Cylinder, "tyre", Vector3.zero, new Vector3(d, 0.025f, d));
            tyre.localRotation = Quaternion.Euler(90f, 0f, 0f);
            // an off-centre mark in the body colour (AgvView tints by parent name), so the spin is visible
            Part(Group(pivot, "mark"), PrimitiveType.Cube, "hub_mark", new Vector3(0f, AgvSpec.WheelRadius * 0.55f, Mathf.Sign(lateral) * 0.03f),
                new Vector3(0.03f, 0.05f, 0.012f));
        }

        private static void Caster(Transform parent, string name, Vector3 pos)
        {
            Transform pivot = Group(parent, name);
            pivot.localPosition = pos;
            float r = 0.045f;
            Part(pivot, PrimitiveType.Sphere, "ball", new Vector3(0f, -AgvSpec.AxleHeight + r, 0f), Vector3.one * (2f * r));
        }
    }
}
