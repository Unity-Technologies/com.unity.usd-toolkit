using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// Builds a warehouse hall from <see cref="GeneratedFactoryParams"/>. Deterministic: the same parameters
    /// and seed give the same parts, lanes and stations (System.Random with the seed; no Unity randomness).
    ///
    /// Plan view (USD: +x east, +y north; the hall is centred on the origin):
    ///
    ///     +------------------- north wall: drop stations ---------------------+
    ///     |   [D]          [D]          [D]              (spurs off the top aisle)
    ///     |  ==================== top aisle =============================  |
    ///  C  |  || [rack segment] || [rack segment] || [rack segment] ||     | P
    ///  h  |  ==================== aisle ==================================  | i
    ///  a  |  || [rack segment] || [rack segment] || [rack segment] ||     | c
    ///  r  |  ==================== bottom aisle ===========================  | k
    ///  g  |  west lane          cross aisles                east lane      |
    ///  e  |        (free floor: a few static obstacles, walkway)           |
    ///     +-------------------------------------------------------------------+
    ///
    /// AGVs drive on aisle and lane centre lines (the route graph) and on short spurs to the docks
    /// (west wall), pick stations (east wall) and drop stations (north wall).
    /// </summary>
    public static class GeneratedFactoryBuilder
    {
        public const float WallThickness = 0.3f;
        public const float WallHeight = 3.0f;
        public const float DockZone = 2.4f;        // west wall to the west lane's aisle edge
        public const float StationZone = 3.0f;     // east wall to the east lane's aisle edge
        public const float NorthZone = 4.4f;       // north wall to the top aisle's centre line
        public const float SouthMargin = 2.0f;     // south wall to the bottom aisle's edge, at least
        public const float RackDepth = 1.1f;
        public const float BayLength = 2.7f;
        public const float RackHeight = 3.4f;
        public const float StationDepth = 1.6f;    // conveyor / drop table depth from the wall
        public const float StationStandoff = 1.0f; // station face to the AGV origin where it stops
        public const float DockSpacing = 2.2f;
        public const float PickSpacing = 3.0f;
        public const float MinDropSpacing = 2.6f;
        /// <summary>Minimum distance from any lane to any obstacle placed by the generator.</summary>
        public const float ObstacleLaneClearance = 1.4f;

        private static readonly float[] LevelZ = { 0f, 1.2f, 2.35f };

        public static GeneratedFactory Build(GeneratedFactoryParams input)
        {
            GeneratedFactoryParams p = (input ?? new GeneratedFactoryParams()).Clamped();
            var g = new GeneratedFactory { Params = p };
            var rng = new System.Random(p.seed);
            AddLooks(g);

            float L = p.hallLength, W = p.hallWidth, A = p.aisleWidth, hx = L * 0.5f, hy = W * 0.5f;
            FleetLayout layout = g.Layout;
            layout.gridBounds = new Rect(-hx, -hy, L, W);

            // ---- rack block along x: west lane | segments separated by cross aisles | east lane -------
            float xw = -hx + DockZone + A * 0.5f;
            float x0 = -hx + DockZone + A;
            float x1 = hx - StationZone - A;
            float xe = hx - StationZone - A * 0.5f;
            int columns = p.rackColumns;
            float slot = (x1 - x0 - (columns - 1) * A) / columns;
            while (columns > 1 && slot < BayLength)
            {
                columns--;
                slot = (x1 - x0 - (columns - 1) * A) / columns;
            }
            if (columns != p.rackColumns) Adjust(g, $"rack columns {p.rackColumns} -> {columns} (hall length {L:0.#} m)");
            p.rackColumns = columns;
            int bays = Mathf.Max(1, Mathf.FloorToInt(slot / BayLength));
            float segLen = Mathf.Min(bays * BayLength, slot);

            var laneX = new List<float> { xw };
            for (int c = 1; c < columns; c++) laneX.Add(x0 + c * (slot + A) - A * 0.5f);
            laneX.Add(xe);

            // ---- rows along y, from the top aisle down ---------------------------------------------------
            float yTop = hy - NorthZone;
            int rows = p.rackRows;
            while (rows > 1 && yTop - rows * (RackDepth + A) - A * 0.5f < -hy + SouthMargin) rows--;
            if (rows != p.rackRows) Adjust(g, $"rack rows {p.rackRows} -> {rows} (hall width {W:0.#} m)");
            p.rackRows = rows;
            float y0 = yTop - rows * (RackDepth + A);
            var aisleY = new List<float>();
            for (int j = 0; j <= rows; j++) aisleY.Add(y0 + j * (RackDepth + A));
            yTop = aisleY[rows];   // the exact key of the top aisle below
            float yMid = (y0 + yTop) * 0.5f;

            // ---- stations: how many fit ------------------------------------------------------------------
            int stations = p.stationCount;
            int pickFit = Mathf.Max(1, Mathf.FloorToInt((W - 4f) / PickSpacing));
            int dropFit = Mathf.Max(1, Mathf.FloorToInt((xe - xw) / MinDropSpacing) - 1);
            int fit = Mathf.Min(stations, Mathf.Min(pickFit, dropFit));
            if (fit != stations) Adjust(g, $"stations {stations} -> {fit} (hall too small)");
            p.stationCount = stations = fit;
            float dockSpacing = Mathf.Min(DockSpacing, (W - 4f) / p.agvCount);

            // ---- route graph: aisles (horizontal) x lanes (vertical), plus station spurs -------------------
            var hLines = new SortedDictionary<float, SortedSet<float>>();   // aisle y -> x stops
            var vLines = new SortedDictionary<float, SortedSet<float>>();   // lane x -> y stops
            foreach (float y in aisleY) hLines[y] = new SortedSet<float>(laneX);
            foreach (float x in laneX) vLines[x] = new SortedSet<float>(aisleY);
            var spurs = new List<(Vector2 from, Vector2 to)>();

            var docks = new List<Station>();
            for (int k = 0; k < p.agvCount; k++)
            {
                float y = Mathf.Clamp(yMid + (k - (p.agvCount - 1) * 0.5f) * dockSpacing, -hy + 1.6f, hy - 1.6f);
                // the AGV parks with its rear to the charger, facing the lane (+x)
                var at = new Vector2(-hx + 0.4f + 1.3f, y);
                docks.Add(new Station { name = $"Dock_{k + 1:00}", kind = StationKind.Charge, position = at, yawDeg = 0f });
                vLines[xw].Add(y);
                spurs.Add((new Vector2(xw, y), at));
            }
            var picks = new List<Station>();
            for (int k = 0; k < stations; k++)
            {
                float y = Mathf.Clamp(yMid + (k - (stations - 1) * 0.5f) * PickSpacing, -hy + 1.6f, hy - 1.6f);
                var at = new Vector2(hx - StationDepth - StationStandoff, y);
                picks.Add(new Station { name = $"Pick_{k + 1:00}", kind = StationKind.Pick, position = at, yawDeg = 0f });
                vLines[xe].Add(y);
                spurs.Add((new Vector2(xe, y), at));
            }
            var drops = new List<Station>();
            float dropSpacing = (xe - xw) / (stations + 1);
            for (int k = 0; k < stations; k++)
            {
                float x = xw + (k + 1) * dropSpacing;
                var at = new Vector2(x, hy - StationDepth - StationStandoff);
                drops.Add(new Station { name = $"Drop_{k + 1:00}", kind = StationKind.Drop, position = at, yawDeg = 90f });
                hLines[yTop].Add(x);
                spurs.Add((new Vector2(x, yTop), at));
            }

            RouteGraph graph = layout.routeGraph;
            foreach (var kv in hLines)
            {
                float? prev = null;
                foreach (float x in kv.Value)
                {
                    if (prev.HasValue) graph.AddEdge(new Vector2(prev.Value, kv.Key), new Vector2(x, kv.Key));
                    prev = x;
                }
            }
            foreach (var kv in vLines)
            {
                float? prev = null;
                foreach (float y in kv.Value)
                {
                    if (prev.HasValue) graph.AddEdge(new Vector2(kv.Key, prev.Value), new Vector2(kv.Key, y));
                    prev = y;
                }
            }
            foreach (var (from, to) in spurs) graph.AddEdge(from, to);
            layout.freeSeed = new Vector2(xw, yTop);

            // ---- geometry -------------------------------------------------------------------------------
            var solids = new List<StaticBox>();   // everything inside the hall, for obstacle placement
            BuildShell(g, L, W);
            BuildFloorZones(g, x0, x1, y0, yTop, A, L, W);
            for (int r = 0; r < rows; r++)
            {
                float cy = y0 + A * 0.5f + RackDepth * 0.5f + r * (RackDepth + A);
                for (int c = 0; c < columns; c++)
                {
                    float cx = x0 + c * (slot + A) + slot * 0.5f;
                    solids.Add(BuildRack(g, rng, $"Racks/Row_{r + 1:00}/Seg_{c + 1:00}", cx, cy, segLen, bays));
                }
            }
            foreach (Station d in docks) solids.Add(BuildDock(g, d, hx));
            foreach (Station s in picks) solids.Add(BuildPick(g, rng, s, hx));
            foreach (Station s in drops) solids.Add(BuildDrop(g, rng, s, hy));
            BuildLaneMarkings(g, graph);
            if (y0 - A * 0.5f > -hy + 3.2f) BuildWalkway(g, L, W);
            BuildObstacles(g, rng, graph, solids, hx, hy);

            // ---- layout: stations, spawns, loops ----------------------------------------------------------
            layout.stations.AddRange(docks);
            layout.stations.AddRange(picks);
            layout.stations.AddRange(drops);
            for (int k = 0; k < p.agvCount; k++)
            {
                string id = $"AGV_{k + 1:00}";
                layout.spawns.Add(new AgvSpawn { agvId = id, position = docks[k].position, yawDeg = 0f });
                layout.loops.Add(BuildLoop(graph, id, docks[k], picks[k % stations], drops[(k + 1) % stations]));
            }
            return g;
        }

        // ---- looks ----------------------------------------------------------------------------------------
        private static void AddLooks(GeneratedFactory g)
        {
            void Look(string name, float r, float gr, float b, float rough, float metal = 0f, Color emissive = default) =>
                g.Looks.Add(new LookDef { name = name, color = new Color(r, gr, b), roughness = rough, metallic = metal, emissive = emissive });

            Look("Concrete", 0.43f, 0.44f, 0.45f, 0.92f);
            Look("ConcreteDark", 0.35f, 0.36f, 0.38f, 0.95f);
            Look("Wall", 0.84f, 0.85f, 0.86f, 0.85f);
            Look("WallBase", 0.28f, 0.30f, 0.33f, 0.7f);
            Look("PaintYellow", 0.98f, 0.76f, 0.08f, 0.6f);
            Look("PaintWhite", 0.93f, 0.93f, 0.91f, 0.6f);
            Look("PaintGreen", 0.18f, 0.55f, 0.32f, 0.7f);
            Look("PaintBlue", 0.16f, 0.42f, 0.80f, 0.6f);
            Look("RackUpright", 0.12f, 0.30f, 0.66f, 0.45f, 0.35f);
            Look("RackBeam", 0.95f, 0.44f, 0.08f, 0.45f, 0.3f);
            Look("WireDeck", 0.62f, 0.63f, 0.65f, 0.4f, 0.7f);
            Look("Pallet", 0.64f, 0.47f, 0.27f, 0.9f);
            Look("Carton", 0.77f, 0.61f, 0.41f, 0.85f);
            Look("CartonDark", 0.62f, 0.47f, 0.30f, 0.85f);
            Look("Tote", 0.20f, 0.36f, 0.58f, 0.6f);
            Look("Wrap", 0.80f, 0.84f, 0.86f, 0.3f);
            Look("Charger", 0.16f, 0.17f, 0.19f, 0.5f, 0.4f);
            Look("ChargerLight", 0.25f, 0.95f, 0.45f, 0.3f, 0f, new Color(0.25f, 0.95f, 0.45f));
            Look("Conveyor", 0.26f, 0.27f, 0.29f, 0.5f, 0.5f);
            Look("Roller", 0.74f, 0.76f, 0.78f, 0.25f, 0.85f);
            Look("Table", 0.30f, 0.40f, 0.52f, 0.5f, 0.4f);
            Look("Column", 0.72f, 0.72f, 0.70f, 0.9f);
            Look("HazardBlack", 0.07f, 0.07f, 0.08f, 0.7f);
        }

        // ---- building blocks ------------------------------------------------------------------------------
        private static void Part(GeneratedFactory g, string path, Vector3 center, Vector3 size, string look,
            bool collision = false, float yawDeg = 0f, bool visible = true)
        {
            g.Parts.Add(new BoxPart { path = path, center = center, size = size, yawDeg = yawDeg, look = look, collision = collision, visible = visible });
        }

        /// <summary>A collision-only box that the fleet also plans around.</summary>
        private static StaticBox Blocker(GeneratedFactory g, string path, Vector2 center, Vector2 size, float height, float yawDeg = 0f)
        {
            Part(g, path, new Vector3(center.x, center.y, height * 0.5f), new Vector3(size.x, size.y, height), "Concrete",
                collision: true, yawDeg: yawDeg, visible: false);
            var box = new StaticBox { name = path, center = center, size = size, height = height, yawDeg = yawDeg };
            g.Layout.staticObstacles.Add(box);
            return box;
        }

        /// <summary>A floor marking: paint only, no collision.</summary>
        private static void Paint(GeneratedFactory g, string path, Vector2 center, Vector2 size, string look, float yawDeg = 0f, float lift = 0f) =>
            Part(g, path, new Vector3(center.x, center.y, 0.003f + lift), new Vector3(size.x, size.y, 0.006f), look, yawDeg: yawDeg);

        private static void BuildShell(GeneratedFactory g, float L, float W)
        {
            float hx = L * 0.5f, hy = W * 0.5f, t = WallThickness;
            // The floor is a plane, not a box: PhysX contact between Isaac Sim's iw_hub cylinder wheels and a
            // large box collider fails along the box's centre lines (the wheels sink through and the AGV
            // grounds). A plane collider is what Isaac Sim's own ground uses.
            g.Parts.Add(new BoxPart
            {
                shape = PartShape.Plane, path = "Floor/Ground", center = Vector3.zero,
                size = new Vector3(L + 2f * t, W + 2f * t, 0f), look = "Concrete", collision = true, visible = true,
            });
            var walls = new (string name, Vector2 c, Vector2 s)[]
            {
                ("West", new Vector2(-hx - t * 0.5f, 0f), new Vector2(t, W + 2f * t)),
                ("East", new Vector2(hx + t * 0.5f, 0f), new Vector2(t, W + 2f * t)),
                ("South", new Vector2(0f, -hy - t * 0.5f), new Vector2(L, t)),
                ("North", new Vector2(0f, hy + t * 0.5f), new Vector2(L, t)),
            };
            foreach (var (name, c, s) in walls)
            {
                Part(g, $"Walls/{name}", new Vector3(c.x, c.y, WallHeight * 0.5f), new Vector3(s.x, s.y, WallHeight), "Wall", collision: true);
                // a dark base course on the inside face
                Vector2 inward = -c.normalized * 0.02f;
                Part(g, $"Walls/{name}_Base", new Vector3(c.x + inward.x, c.y + inward.y, 0.3f), new Vector3(s.x, s.y, 0.6f), "WallBase");
                g.Layout.staticObstacles.Add(new StaticBox { name = $"Walls/{name}", center = c, size = s, height = WallHeight });
            }
        }

        private static void BuildFloorZones(GeneratedFactory g, float x0, float x1, float y0, float yTop, float A, float L, float W)
        {
            // storage zone: a darker floor under the rack block
            Vector2 c = new Vector2((x0 + x1) * 0.5f, (y0 + yTop) * 0.5f);
            Vector2 s = new Vector2(x1 - x0 + 0.6f, yTop - y0 - A + 0.6f);
            Paint(g, "Markings/StorageZone", c, s, "ConcreteDark", lift: -0.002f);
        }

        private static StaticBox BuildRack(GeneratedFactory g, System.Random rng, string path, float cx, float cy, float len, int bays)
        {
            float d = RackDepth, h = RackHeight, u = 0.08f;
            float xs = cx - len * 0.5f;
            // uprights at every bay boundary, front and back
            for (int b = 0; b <= bays; b++)
            {
                float x = Mathf.Clamp(xs + b * BayLength, xs + u * 0.5f, xs + len - u * 0.5f);
                Part(g, $"{path}/Upright_F{b:00}", new Vector3(x, cy - d * 0.5f + u * 0.5f, h * 0.5f), new Vector3(u, u, h), "RackUpright");
                Part(g, $"{path}/Upright_B{b:00}", new Vector3(x, cy + d * 0.5f - u * 0.5f, h * 0.5f), new Vector3(u, u, h), "RackUpright");
            }
            for (int lv = 1; lv < LevelZ.Length; lv++)
            {
                float z = LevelZ[lv];
                Part(g, $"{path}/Beam_F{lv}", new Vector3(cx, cy - d * 0.5f + 0.03f, z - 0.06f), new Vector3(len, 0.06f, 0.12f), "RackBeam");
                Part(g, $"{path}/Beam_B{lv}", new Vector3(cx, cy + d * 0.5f - 0.03f, z - 0.06f), new Vector3(len, 0.06f, 0.12f), "RackBeam");
                Part(g, $"{path}/Deck_{lv}", new Vector3(cx, cy, z - 0.015f), new Vector3(len - 0.02f, d - 0.06f, 0.03f), "WireDeck");
            }
            // loads: two pallet positions per bay and level, filled at random
            string[] cartons = { "Carton", "Carton", "CartonDark", "Tote", "Wrap" };
            for (int b = 0; b < bays; b++)
            {
                for (int lv = 0; lv < LevelZ.Length; lv++)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        double roll = rng.NextDouble();
                        float loadH = 0.45f + (float)rng.NextDouble() * 0.5f;
                        string look = cartons[rng.Next(cartons.Length)];
                        if (roll > 0.72) continue;
                        float x = xs + b * BayLength + BayLength * (k == 0 ? 0.27f : 0.73f);
                        float z = LevelZ[lv];
                        string n = $"{path}/Load_{b:00}_{lv}_{k}";
                        Part(g, n + "_Pallet", new Vector3(x, cy, z + 0.07f), new Vector3(1.0f, 1.0f, 0.14f), "Pallet");
                        Part(g, n + "_Goods", new Vector3(x, cy, z + 0.14f + loadH * 0.5f), new Vector3(0.95f, 0.92f, loadH), look);
                    }
                }
            }
            // floor outline, 0.15 m off the rack
            float o = 0.15f, w = 0.08f;
            Paint(g, $"{path}/Outline_S", new Vector2(cx, cy - d * 0.5f - o), new Vector2(len + 2f * o, w), "PaintYellow");
            Paint(g, $"{path}/Outline_N", new Vector2(cx, cy + d * 0.5f + o), new Vector2(len + 2f * o, w), "PaintYellow");
            Paint(g, $"{path}/Outline_W", new Vector2(xs - o, cy), new Vector2(w, d + 2f * o), "PaintYellow");
            Paint(g, $"{path}/Outline_E", new Vector2(xs + len + o, cy), new Vector2(w, d + 2f * o), "PaintYellow");
            return Blocker(g, $"{path}/Collision", new Vector2(cx, cy), new Vector2(len, d), h);
        }

        /// <summary>A painted parking pad under the AGV footprint at <paramref name="s"/>, with a border.</summary>
        private static void Pad(GeneratedFactory g, string path, Station s, string fill, string border)
        {
            float yaw = s.yawDeg * Mathf.Deg2Rad;
            var fwd = new Vector2(Mathf.Cos(yaw), Mathf.Sin(yaw));
            Vector2 c = s.position + fwd * AgvSpec.CenterX;
            bool alongX = Mathf.Abs(fwd.x) > 0.5f;
            Vector2 size = alongX ? new Vector2(1.9f, 1.0f) : new Vector2(1.0f, 1.9f);
            Paint(g, path + "/Pad", c, size, fill);
            float w = 0.08f;
            Paint(g, path + "/Border_A", c + new Vector2(0f, size.y * 0.5f + w * 0.5f), new Vector2(size.x + 2f * w, w), border);
            Paint(g, path + "/Border_B", c - new Vector2(0f, size.y * 0.5f + w * 0.5f), new Vector2(size.x + 2f * w, w), border);
            Paint(g, path + "/Border_C", c + new Vector2(size.x * 0.5f + w * 0.5f, 0f), new Vector2(w, size.y), border);
            Paint(g, path + "/Border_D", c - new Vector2(size.x * 0.5f + w * 0.5f, 0f), new Vector2(w, size.y), border);
        }

        private static StaticBox BuildDock(GeneratedFactory g, Station s, float hx)
        {
            string path = $"Docks/{s.name}";
            float y = s.position.y;
            Part(g, path + "/Charger", new Vector3(-hx + 0.2f, y, 0.6f), new Vector3(0.4f, 0.8f, 1.2f), "Charger");
            Part(g, path + "/Light", new Vector3(-hx + 0.405f, y, 0.95f), new Vector3(0.012f, 0.5f, 0.08f), "ChargerLight");
            Part(g, path + "/Contacts", new Vector3(-hx + 0.43f, y, 0.15f), new Vector3(0.06f, 0.4f, 0.1f), "Roller");
            Pad(g, path, s, "PaintGreen", "PaintYellow");
            return Blocker(g, path + "/Collision", new Vector2(-hx + 0.2f, y), new Vector2(0.4f, 0.8f), 1.2f);
        }

        private static StaticBox BuildPick(GeneratedFactory g, System.Random rng, Station s, float hx)
        {
            string path = $"Stations/{s.name}";
            float y = s.position.y, cx = hx - StationDepth * 0.5f;
            Part(g, path + "/Frame", new Vector3(cx, y, 0.35f), new Vector3(StationDepth, 1.0f, 0.7f), "Conveyor");
            for (int i = 0; i < 6; i++)
            {
                Part(g, $"{path}/Roller_{i}", new Vector3(hx - StationDepth + 0.15f + i * 0.26f, y, 0.72f), new Vector3(0.07f, 0.92f, 0.05f), "Roller");
            }
            Part(g, path + "/Rail_L", new Vector3(cx, y + 0.5f, 0.78f), new Vector3(StationDepth, 0.05f, 0.1f), "PaintYellow");
            Part(g, path + "/Rail_R", new Vector3(cx, y - 0.5f, 0.78f), new Vector3(StationDepth, 0.05f, 0.1f), "PaintYellow");
            if (rng.NextDouble() < 0.7)
            {
                Part(g, path + "/Tote", new Vector3(cx + 0.1f, y, 0.75f + 0.18f), new Vector3(0.6f, 0.45f, 0.36f), "Tote");
            }
            Pad(g, path, s, "PaintBlue", "PaintWhite");
            return Blocker(g, path + "/Collision", new Vector2(cx, y), new Vector2(StationDepth, 1.0f), 0.8f);
        }

        private static StaticBox BuildDrop(GeneratedFactory g, System.Random rng, Station s, float hy)
        {
            string path = $"Stations/{s.name}";
            float x = s.position.x, cy = hy - StationDepth * 0.5f;
            Part(g, path + "/Table", new Vector3(x, cy, 0.38f), new Vector3(1.2f, StationDepth, 0.76f), "Table");
            Part(g, path + "/Top", new Vector3(x, cy, 0.78f), new Vector3(1.26f, StationDepth + 0.04f, 0.04f), "Roller");
            if (rng.NextDouble() < 0.6)
            {
                Part(g, path + "/Pallet", new Vector3(x, cy + 0.1f, 0.8f + 0.07f), new Vector3(1.0f, 1.0f, 0.14f), "Pallet");
                Part(g, path + "/Goods", new Vector3(x, cy + 0.1f, 0.94f + 0.25f), new Vector3(0.9f, 0.9f, 0.5f), "Wrap");
            }
            Pad(g, path, s, "PaintBlue", "PaintWhite");
            return Blocker(g, path + "/Collision", new Vector2(x, cy), new Vector2(1.2f, StationDepth), 0.8f);
        }

        /// <summary>Dashed white centre lines on every lane and spur: the route graph, painted.</summary>
        private static void BuildLaneMarkings(GeneratedFactory g, RouteGraph graph)
        {
            const float dash = 0.9f, gap = 0.6f, width = 0.1f;
            int n = 0;
            foreach (Vector2Int e in graph.edges)
            {
                Vector2 a = graph.nodes[e.x], b = graph.nodes[e.y];
                float len = Vector2.Distance(a, b);
                if (len < 0.2f) continue;
                Vector2 dir = (b - a) / len;
                float yaw = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                for (float s = 0.3f; s < len - 0.3f; s += dash + gap)
                {
                    float l = Mathf.Min(dash, len - 0.3f - s);
                    if (l < 0.2f) break;
                    Paint(g, $"Markings/Lanes/Dash_{n++:0000}", a + dir * (s + l * 0.5f), new Vector2(l, width), "PaintWhite", yaw);
                }
            }
        }

        private static void BuildWalkway(GeneratedFactory g, float L, float W)
        {
            float y = -W * 0.5f + 0.4f + 0.6f;
            Paint(g, "Markings/Walkway", new Vector2(0f, y), new Vector2(L - 0.8f, 1.2f), "PaintGreen");
            Paint(g, "Markings/Walkway_EdgeN", new Vector2(0f, y + 0.64f), new Vector2(L - 0.8f, 0.08f), "PaintWhite");
            Paint(g, "Markings/Walkway_EdgeS", new Vector2(0f, y - 0.64f), new Vector2(L - 0.8f, 0.08f), "PaintWhite");
        }

        /// <summary>A few columns, pallet stacks and barriers on free floor, never within reach of a lane.</summary>
        private static void BuildObstacles(GeneratedFactory g, System.Random rng, RouteGraph graph, List<StaticBox> solids, float hx, float hy)
        {
            int want = 3 + rng.Next(3);
            int placed = 0;
            for (int attempt = 0; attempt < 400 && placed < want; attempt++)
            {
                int kind = rng.Next(3);
                Vector2 size = kind == 0 ? new Vector2(0.6f, 0.6f) : kind == 1 ? new Vector2(1.2f, 1.0f) : new Vector2(2.4f, 0.3f);
                float yaw = rng.Next(2) * 90f;
                float half = size.magnitude * 0.5f;
                var c = new Vector2(Mathf.Lerp(-hx + 1.2f + half, hx - 1.2f - half, (float)rng.NextDouble()),
                                    Mathf.Lerp(-hy + 1.2f + half, hy - 1.2f - half, (float)rng.NextDouble()));
                var box = new StaticBox { center = c, size = size, yawDeg = yaw };
                bool ok = true;
                foreach (Vector2Int e in graph.edges)
                {
                    if (RouteGraph.SegmentBoxDistance(graph.nodes[e.x], graph.nodes[e.y], box) < ObstacleLaneClearance) { ok = false; break; }
                }
                if (ok)
                {
                    foreach (StaticBox s in solids)
                    {
                        if (Vector2.Distance(s.center, c) < half + s.size.magnitude * 0.5f + 0.6f) { ok = false; break; }
                    }
                }
                if (!ok) continue;
                placed++;
                string path = $"Obstacles/{(kind == 0 ? "Column" : kind == 1 ? "PalletStack" : "Barrier")}_{placed:00}";
                float height;
                if (kind == 0)
                {
                    height = WallHeight;
                    Part(g, path + "/Body", new Vector3(c.x, c.y, height * 0.5f), new Vector3(size.x, size.y, height), "Column", yawDeg: yaw);
                    Part(g, path + "/Guard", new Vector3(c.x, c.y, 0.5f), new Vector3(size.x + 0.04f, size.y + 0.04f, 1.0f), "PaintYellow", yawDeg: yaw);
                    Part(g, path + "/Stripe", new Vector3(c.x, c.y, 0.65f), new Vector3(size.x + 0.05f, size.y + 0.05f, 0.15f), "HazardBlack", yawDeg: yaw);
                }
                else if (kind == 1)
                {
                    int layers = 2 + rng.Next(2);
                    height = 0.14f;
                    for (int i = 0; i < layers; i++)
                    {
                        float lh = 0.4f + (float)rng.NextDouble() * 0.2f;
                        Part(g, $"{path}/Pallet_{i}", new Vector3(c.x, c.y, height + 0.07f - 0.14f), new Vector3(size.x, size.y, 0.14f), "Pallet", yawDeg: yaw);
                        Part(g, $"{path}/Goods_{i}", new Vector3(c.x, c.y, height + lh * 0.5f), new Vector3(size.x - 0.08f, size.y - 0.08f, lh), i % 2 == 0 ? "Carton" : "CartonDark", yawDeg: yaw);
                        height += lh + 0.14f;
                    }
                    height -= 0.14f;
                }
                else
                {
                    height = 1.0f;
                    Part(g, path + "/Body", new Vector3(c.x, c.y, height * 0.5f), new Vector3(size.x, size.y, height), "PaintYellow", yawDeg: yaw);
                    var along = yaw == 0f ? Vector2.right : Vector2.up;
                    for (int i = -1; i <= 1; i++)
                    {
                        Vector2 sc = c + along * (i * 0.7f);
                        Part(g, $"{path}/Stripe_{i + 1}", new Vector3(sc.x, sc.y, height * 0.5f), new Vector3(0.25f, size.y + 0.02f, height + 0.01f), "HazardBlack", yawDeg: yaw);
                    }
                }
                solids.Add(Blocker(g, path + "/Collision", c, size, height, yaw));
            }
        }

        /// <summary>Dock -> pick -> drop -> back to the dock, along the lanes, corners only.</summary>
        private static RouteLoop BuildLoop(RouteGraph graph, string agv, Station dock, Station pick, Station drop)
        {
            var loop = new RouteLoop { agvId = agv };
            Station[] stops = { dock, pick, drop };
            var pts = new List<(Vector2 p, string station)>();
            for (int i = 0; i < stops.Length; i++)
            {
                Station a = stops[i], b = stops[(i + 1) % stops.Length];
                List<int> path = graph.ShortestPath(graph.FindNode(a.position), graph.FindNode(b.position));
                if (path.Count == 0) throw new InvalidOperationException($"{agv}: no lane from {a.name} to {b.name}");
                for (int k = 0; k < path.Count - 1; k++)   // the end is the next leg's start
                {
                    Vector2 q = graph.nodes[path[k]];
                    pts.Add((q, k == 0 ? a.name : ""));
                }
            }
            // keep corners and stations only
            for (int i = 0; i < pts.Count; i++)
            {
                var prev = pts[(i - 1 + pts.Count) % pts.Count].p;
                var next = pts[(i + 1) % pts.Count].p;
                var (q, station) = pts[i];
                Vector2 d1 = q - prev, d2 = next - q;
                bool straight = Mathf.Abs(d1.x * d2.y - d1.y * d2.x) < 1e-4f && Vector2.Dot(d1, d2) > 0f;
                if (straight && station.Length == 0) continue;
                loop.waypoints.Add(q);
                loop.stations.Add(station);
            }
            return loop;
        }

        private static void Adjust(GeneratedFactory g, string note) => g.Adjustments.Add(note);
    }
}
