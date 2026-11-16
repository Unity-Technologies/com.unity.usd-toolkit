using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>The colours one skin of the sample's UI uses.</summary>
    public sealed class SimReadyPalette
    {
        public Color Window;
        public Color Header;
        public Color Card;
        public Color Inset;
        public Color Chip;
        public Color Line;
        public Color Text;
        public Color Muted;
        public Color Link;
        public Color Accent = SimReadyStyle.HandleColor;
        public Color Success = new Color(0.3f, 0.72f, 0.4f);
        public Color Error = new Color(0.9f, 0.33f, 0.3f);
        public Color Warning = new Color(0.95f, 0.64f, 0.23f);
        public Color Info = new Color(0.35f, 0.6f, 0.98f);

        public static readonly SimReadyPalette Dark = new SimReadyPalette
        {
            Window = new Color(0.16f, 0.16f, 0.17f, 0.97f),
            Header = new Color(0.1f, 0.1f, 0.12f),
            Card = new Color(0.21f, 0.21f, 0.23f),
            Inset = new Color(0.17f, 0.17f, 0.19f),
            Chip = new Color(0.28f, 0.28f, 0.31f),
            Line = new Color(0.29f, 0.29f, 0.32f),
            Text = new Color(0.92f, 0.92f, 0.94f),
            Muted = new Color(0.62f, 0.62f, 0.66f),
            Link = new Color(0.45f, 0.68f, 1f),
        };

        public static readonly SimReadyPalette Light = new SimReadyPalette
        {
            Window = new Color(0.87f, 0.87f, 0.88f),
            Header = new Color(0.1f, 0.1f, 0.12f),
            Card = new Color(0.96f, 0.96f, 0.97f),
            Inset = new Color(0.9f, 0.9f, 0.92f),
            Chip = new Color(0.84f, 0.84f, 0.87f),
            Line = new Color(0.8f, 0.8f, 0.83f),
            Text = new Color(0.1f, 0.1f, 0.12f),
            Muted = new Color(0.38f, 0.38f, 0.42f),
            Link = new Color(0.1f, 0.35f, 0.8f),
        };

        /// <summary>The colour of a physics-log status (mapped, approximated, skipped, applied, recorded, logged).</summary>
        public Color Status(string status)
        {
            switch (status)
            {
                case SimReadyPhysicsReport.Mapped: return Success;
                case SimReadyPhysicsReport.Approximated: return Warning;
                case SimReadyPhysicsReport.Skipped: return Error;
                case SimReadyPhysicsReport.Applied: return Info;
                default: return Muted;
            }
        }
    }

    /// <summary>
    /// The building blocks of the sample's UI, shared by the Editor window and the runtime viewer
    /// so both look the same: header, cards, stat tiles, pills, buttons, the asset card, joint rows
    /// and the physics log with its filter chips. Styles are set in code, so no stylesheet ships.
    /// </summary>
    public static class SimReadyUiKit
    {
        public const string AllFilter = "all";

        public static VisualElement Header(string title, string subtitle)
        {
            var header = new VisualElement();
            header.style.backgroundColor = new Color(0.1f, 0.1f, 0.12f);
            header.style.paddingLeft = header.style.paddingRight = 14f;
            header.style.paddingTop = 12f;
            header.style.paddingBottom = 12f;
            header.style.borderBottomWidth = 3f;
            header.style.borderBottomColor = SimReadyStyle.HandleColor;
            var heading = new Label(title);
            heading.style.fontSize = 18;
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.color = Color.white;
            var sub = new Label(subtitle);
            sub.style.color = new Color(1f, 1f, 1f, 0.7f);
            sub.style.whiteSpace = WhiteSpace.Normal;
            sub.style.marginTop = 2f;
            header.Add(heading);
            header.Add(sub);
            return header;
        }

        public static VisualElement Card(SimReadyPalette p, string title, string subtitle)
        {
            var card = new VisualElement();
            card.style.backgroundColor = p.Card;
            card.style.marginLeft = card.style.marginRight = 10f;
            card.style.marginTop = 10f;
            card.style.paddingLeft = card.style.paddingRight = 12f;
            card.style.paddingTop = card.style.paddingBottom = 10f;
            Radius(card, 8f);
            Border(card, 1f, p.Line);
            if (!string.IsNullOrEmpty(title))
            {
                var heading = new Label(title);
                heading.style.fontSize = 13;
                heading.style.unityFontStyleAndWeight = FontStyle.Bold;
                heading.style.color = p.Text;
                card.Add(heading);
            }

            if (!string.IsNullOrEmpty(subtitle))
            {
                Label sub = Muted(p, subtitle);
                sub.style.marginBottom = 6f;
                card.Add(sub);
            }

            return card;
        }

        public static VisualElement Tile(SimReadyPalette p, string caption, string value, string unit)
        {
            var tile = new VisualElement();
            tile.style.backgroundColor = p.Inset;
            tile.style.paddingLeft = tile.style.paddingRight = 10f;
            tile.style.paddingTop = tile.style.paddingBottom = 6f;
            tile.style.marginRight = 6f;
            tile.style.marginBottom = 6f;
            tile.style.minWidth = 76f;
            Radius(tile, 6f);
            var label = new Label(caption.ToUpperInvariant());
            label.style.fontSize = 9;
            label.style.color = p.Muted;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            VisualElement line = Row();
            line.style.alignItems = Align.FlexEnd;
            var number = new Label(value);
            number.style.fontSize = 15;
            number.style.unityFontStyleAndWeight = FontStyle.Bold;
            number.style.color = p.Text;
            line.Add(number);
            if (!string.IsNullOrEmpty(unit))
            {
                Label suffix = Muted(p, " " + unit);
                suffix.style.marginBottom = 2f;
                line.Add(suffix);
            }

            tile.Add(label);
            tile.Add(line);
            return tile;
        }

        public static VisualElement Pill(string text, Color background, Color color)
        {
            var pill = new VisualElement();
            pill.style.flexDirection = FlexDirection.Row;
            pill.style.backgroundColor = background;
            pill.style.paddingLeft = pill.style.paddingRight = 8f;
            pill.style.paddingTop = pill.style.paddingBottom = 2f;
            pill.style.marginRight = 4f;
            pill.style.marginBottom = 4f;
            Radius(pill, 9f);
            var label = new Label(text);
            label.style.color = color;
            label.style.fontSize = 11;
            pill.Add(label);
            return pill;
        }

        public static Label Muted(SimReadyPalette p, string text)
        {
            var label = new Label(text);
            label.style.color = p.Muted;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 11;
            return label;
        }

        public static Button PrimaryButton(string text, Action click)
        {
            var button = new Button(click) { text = text };
            button.style.backgroundColor = SimReadyStyle.HandleColor;
            button.style.color = Color.white;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.height = 26f;
            button.style.paddingLeft = button.style.paddingRight = 12f;
            button.style.marginLeft = 0f;
            Border(button, 0f, Color.clear);
            Radius(button, 5f);
            return button;
        }

        public static Button SecondaryButton(string text, Action click)
        {
            var button = new Button(click) { text = text };
            button.style.height = 26f;
            button.style.paddingLeft = button.style.paddingRight = 10f;
            Radius(button, 5f);
            return button;
        }

        /// <summary>
        /// The Kinematic switch: <paramref name="asset"/>'s mode, remembered for the next asset loaded.
        /// <paramref name="changed"/> runs after the asset has switched.
        /// </summary>
        public static Toggle KinematicToggle(SimReadyPalette p, SimReadyAsset asset, Action changed)
        {
            var toggle = new Toggle("Kinematic") { value = asset.Kinematic };
            toggle.tooltip = "Play mode without physics: the handles pose the links directly, as in edit mode, " +
                             "so an asset that cannot hold itself up (a humanoid without joint drives) stands still.";
            toggle.style.marginLeft = 0f;
            toggle.labelElement.style.color = p.Text;
            toggle.labelElement.style.minWidth = 0f;
            toggle.labelElement.style.marginRight = 6f;
            toggle.RegisterValueChangedCallback(e =>
            {
                if (asset == null)
                {
                    return;
                }

                asset.Kinematic = e.newValue;
                SimReadyAsset.KinematicByDefault = e.newValue;
                changed?.Invoke();
            });
            return toggle;
        }

        public static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }

        public static VisualElement Wrapping()
        {
            var wrap = new VisualElement();
            wrap.style.flexDirection = FlexDirection.Row;
            wrap.style.flexWrap = Wrap.Wrap;
            return wrap;
        }

        public static VisualElement KeyValue(SimReadyPalette p, string key, string value)
        {
            VisualElement row = Row();
            row.style.alignItems = Align.FlexStart;
            Label k = Muted(p, key);
            k.style.width = 150f;
            k.style.flexShrink = 0f;
            var v = new Label(value);
            v.style.whiteSpace = WhiteSpace.Normal;
            v.style.flexShrink = 1f;
            v.style.color = p.Text;
            row.Add(k);
            row.Add(v);
            return row;
        }

        /// <summary>A coloured message box (success or error) that hides itself when empty.</summary>
        public static Label StatusBox()
        {
            var status = new Label();
            status.style.whiteSpace = WhiteSpace.Normal;
            status.style.marginTop = 8f;
            status.style.paddingLeft = status.style.paddingRight = 8f;
            status.style.paddingTop = status.style.paddingBottom = 6f;
            Radius(status, 4f);
            status.style.display = DisplayStyle.None;
            return status;
        }

        public static void SetStatus(SimReadyPalette p, Label status, string message, bool error)
        {
            status.text = message ?? string.Empty;
            status.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
            Color color = error ? p.Error : p.Success;
            status.style.color = p.Text;
            status.style.backgroundColor = new Color(color.r, color.g, color.b, 0.15f);
            status.style.borderLeftWidth = 3f;
            status.style.borderLeftColor = color;
        }

        // ---- Download progress ----

        /// <summary>
        /// A download's progress: the bar with the percentage only, then the stage and the file
        /// count on the left and the megabytes on the right, then the file being fetched by name
        /// (cut with an ellipsis, the repo path in its tooltip). Hidden until <see cref="Show"/>.
        /// </summary>
        public sealed class DownloadProgress
        {
            public readonly VisualElement Root;
            private readonly ProgressBar bar;
            private readonly Label stage;
            private readonly Label size;
            private readonly Label file;

            public DownloadProgress(SimReadyPalette p)
            {
                Root = new VisualElement();
                Root.style.marginTop = 8f;
                Root.style.display = DisplayStyle.None;

                bar = new ProgressBar { lowValue = 0f, highValue = 1f };
                bar.style.marginLeft = bar.style.marginRight = 0f;
                Root.Add(bar);

                VisualElement line = Row();
                line.style.marginTop = 2f;
                stage = Muted(p, string.Empty);
                stage.style.flexGrow = 1f;
                stage.style.flexShrink = 1f;
                stage.style.whiteSpace = WhiteSpace.NoWrap;
                stage.style.overflow = Overflow.Hidden;
                stage.style.textOverflow = TextOverflow.Ellipsis;
                size = Muted(p, string.Empty);
                size.style.flexShrink = 0f;
                size.style.whiteSpace = WhiteSpace.NoWrap;
                size.style.marginLeft = 8f;
                line.Add(stage);
                line.Add(size);
                Root.Add(line);

                file = Muted(p, string.Empty);
                file.style.whiteSpace = WhiteSpace.NoWrap;
                file.style.overflow = Overflow.Hidden;
                file.style.textOverflow = TextOverflow.Ellipsis;
                Root.Add(file);
            }

            public void Show(bool visible)
            {
                Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (visible)
                {
                    Report(new SimReadyProgress { Stage = "Starting" });
                }
            }

            public void Report(SimReadyProgress progress)
            {
                bar.value = progress.Fraction;
                bar.title = progress.BytesKnown > 0 ? $"{Mathf.RoundToInt(progress.Fraction * 100f)} %" : string.Empty;
                stage.text = progress.FilesKnown > 0 ? $"{progress.Stage} · {progress.FilesDone} of {progress.FilesKnown} files" : progress.Stage + "…";
                size.text = progress.BytesKnown > 0
                    ? $"{SimReadyWebTransport.Megabytes(progress.BytesDone).Replace(" MB", string.Empty)} of {SimReadyWebTransport.Megabytes(progress.BytesKnown)}"
                    : string.Empty;
                string current = progress.CurrentFile ?? string.Empty;
                int slash = current.LastIndexOf('/');
                file.text = slash >= 0 ? current.Substring(slash + 1) : current;
                file.tooltip = current;
                file.style.display = string.IsNullOrEmpty(current) ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        // ---- The asset card ----

        /// <summary>
        /// Thumbnail, name, author, pills (type, category, licence, profile), the source repo as a
        /// link, and tiles for size, mass, moving parts and rigid bodies. <paramref name="actions"/>
        /// (may be null) is put under the tiles.
        /// </summary>
        public static VisualElement AssetCard(SimReadyPalette p, SimReadyAsset asset, Texture thumbnail, VisualElement actions)
        {
            SimReadyMetadata m = asset.Metadata;
            VisualElement card = Card(p, null, null);

            VisualElement top = Row();
            top.style.alignItems = Align.FlexStart;
            var image = new Image { image = thumbnail, scaleMode = ScaleMode.ScaleToFit };
            image.style.width = image.style.height = 96f;
            image.style.flexShrink = 0f;
            image.style.backgroundColor = p.Inset;
            image.style.marginRight = 12f;
            Radius(image, 6f);
            top.Add(image);

            var titles = new VisualElement();
            titles.style.flexGrow = 1f;
            titles.style.flexShrink = 1f;
            var name = new Label(string.IsNullOrEmpty(m.AssetName) ? asset.name : m.AssetName);
            name.style.fontSize = 16;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.color = p.Text;
            titles.Add(name);
            if (!string.IsNullOrEmpty(m.Author))
            {
                titles.Add(Muted(p, "by " + m.Author));
            }

            VisualElement pills = Wrapping();
            pills.style.marginTop = 6f;
            AddPill(pills, m.AssetType, p.Accent, Color.white);
            AddPill(pills, m.Category, p.Chip, p.Text);
            AddPill(pills, m.License, p.Chip, p.Text);
            AddPill(pills, m.Profile, p.Chip, p.Text);
            titles.Add(pills);

            if (!string.IsNullOrEmpty(asset.RepoId))
            {
                var source = new Label(string.IsNullOrEmpty(asset.Commit) ? asset.RepoId : $"{asset.RepoId} @ {asset.Commit.Substring(0, Math.Min(8, asset.Commit.Length))}");
                source.style.color = p.Link;
                source.style.marginTop = 6f;
                source.style.whiteSpace = WhiteSpace.Normal;
                source.tooltip = "Open the repo on Hugging Face";
                source.RegisterCallback<ClickEvent>(_ =>
                {
                    if (!string.IsNullOrEmpty(asset.RepoUrl))
                    {
                        Application.OpenURL(asset.RepoUrl);
                    }
                });
                titles.Add(source);
            }

            top.Add(titles);
            card.Add(top);

            VisualElement tiles = Wrapping();
            tiles.style.marginTop = 10f;
            if (m.HasExtents)
            {
                Vector3 e = m.ExtentsMeters;
                tiles.Add(Tile(p, "Size", $"{e.x * 100f:0.#} × {e.y * 100f:0.#} × {e.z * 100f:0.#}", "cm"));
            }

            if (m.HasMass)
            {
                double kg = m.MassKilograms;
                tiles.Add(kg < 1.0 ? Tile(p, "Mass", $"{kg * 1000.0:0}", "g") : Tile(p, "Mass", $"{kg:0.##}", "kg"));
            }

            tiles.Add(Tile(p, "Moving parts", asset.MovingParts.ToString(), string.Empty));
            if (m.HasRigidBodyCount)
            {
                tiles.Add(Tile(p, "Rigid bodies", m.RigidBodyCount.ToString(), string.Empty));
            }

            card.Add(tiles);
            if (actions != null)
            {
                card.Add(actions);
            }

            var more = new Foldout { text = "All metadata", value = false };
            more.style.marginTop = 6f;
            for (int i = 0; i < m.Keys.Count; i++)
            {
                more.Add(KeyValue(p, m.Keys[i].Replace("SimReady_Metadata:", string.Empty), m.Values[i]));
            }

            more.Add(KeyValue(p, "up axis · units", $"{m.UpAxis} · {m.MetersPerUnit:G4} m · {m.KilogramsPerUnit:G4} kg per unit"));
            card.Add(more);
            Label licence = Muted(p, "The licence is the asset repo's. Read it before you use or share the asset.");
            licence.style.marginTop = 4f;
            card.Add(licence);
            return card;
        }

        private static void AddPill(VisualElement parent, string text, Color background, Color color)
        {
            if (!string.IsNullOrEmpty(text))
            {
                parent.Add(Pill(text, background, color));
            }
        }

        // ---- Joint rows ----

        /// <summary>A moving part: dot, name, the value large, a slider over its range, min and max.</summary>
        public sealed class JointRow
        {
            public SimReadyJointHandle Handle;
            public VisualElement Root;
            public Label Value;
            public Slider Slider;
            public float Scale;

            /// <summary>Shows the handle's value now without feeding it back into the slider's callback.</summary>
            public void Refresh(SimReadyAsset asset)
            {
                if (asset == null || Handle?.Joint == null)
                {
                    return;
                }

                float v = asset.GetValue(Handle);
                Value.text = SimReadyJointMath.Badge(v, Handle.Angular, asset.Metadata.MetersPerUnit).Replace("mm", " mm");
                Slider.SetValueWithoutNotify(v * Scale);
            }
        }

        /// <summary><paramref name="changed"/> gets the new value in USD units; begin and end bracket a slider drag.</summary>
        public static JointRow JointRowFor(SimReadyPalette p, SimReadyAsset asset, SimReadyJointHandle handle, Action<float> changed,
                                           Action begin, Action end)
        {
            double mpu = asset.Metadata.MetersPerUnit;
            var row = new JointRow { Handle = handle, Scale = handle.Angular ? 1f : (float)(mpu * 1000.0) };
            var block = new VisualElement();
            block.style.backgroundColor = p.Inset;
            block.style.paddingLeft = block.style.paddingRight = 10f;
            block.style.paddingTop = 8f;
            block.style.paddingBottom = 4f;
            block.style.marginTop = 6f;
            Radius(block, 6f);

            VisualElement head = Row();
            var dot = new VisualElement();
            dot.style.width = dot.style.height = 10f;
            dot.style.backgroundColor = SimReadyStyle.HandleColor;
            Radius(dot, 5f);
            dot.style.marginRight = 8f;
            var name = new Label(handle.Label);
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.color = p.Text;
            name.style.flexGrow = 1f;
            row.Value = new Label();
            row.Value.style.fontSize = 18;
            row.Value.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Value.style.color = SimReadyStyle.HandleColor;
            head.Add(dot);
            head.Add(name);
            head.Add(row.Value);
            block.Add(head);

            row.Slider = new Slider(handle.Min * row.Scale, handle.Max * row.Scale);
            row.Slider.style.marginLeft = row.Slider.style.marginRight = 0f;
            row.Slider.RegisterValueChangedCallback(e => changed(e.newValue / row.Scale));
            row.Slider.RegisterCallback<PointerDownEvent>(_ => begin?.Invoke(), TrickleDown.TrickleDown);
            row.Slider.RegisterCallback<PointerUpEvent>(_ => end?.Invoke(), TrickleDown.TrickleDown);
            block.Add(row.Slider);

            VisualElement range = Row();
            string unit = SimReadyJointMath.Unit(handle.Angular).Trim();
            Label min = Muted(p, $"{SimReadyJointMath.DisplayValue(handle.Min, handle.Angular, mpu)} {unit}");
            Label max = Muted(p, $"{SimReadyJointMath.DisplayValue(handle.Max, handle.Angular, mpu)} {unit}");
            min.style.flexGrow = 1f;
            range.Add(min);
            range.Add(max);
            block.Add(range);
            row.Root = block;
            row.Refresh(asset);
            return row;
        }

        // ---- The physics log ----

        /// <summary>
        /// Count chips per status (click one to filter) over a virtualised list, each row a coloured
        /// status pill, the prim, what it became and why. <paramref name="filter"/> keeps the choice.
        /// </summary>
        public static VisualElement PhysicsLog(SimReadyPalette p, IReadOnlyList<SimReadyMappingRow> rows, Func<string> getFilter, Action<string> setFilter)
        {
            var root = new VisualElement();
            VisualElement counts = Wrapping();
            root.Add(counts);

            var list = new ListView
            {
                makeItem = () => MakeLogRow(p),
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                selectionType = SelectionType.None,
                showBorder = false,
            };
            list.style.maxHeight = 260f;
            list.style.minHeight = 60f;
            list.style.marginTop = 6f;
            list.style.backgroundColor = p.Inset;
            Radius(list, 6f);
            list.bindItem = (element, index) => BindLogRow(p, element, (SimReadyMappingRow)list.itemsSource[index]);
            root.Add(list);

            void Apply()
            {
                string filter = getFilter();
                list.itemsSource = rows.Where(r => filter == AllFilter || r.Status == filter).ToList();
                list.Rebuild();
                counts.Clear();
                counts.Add(CountChip(p, AllFilter, rows.Count, filter == AllFilter, () => { setFilter(AllFilter); Apply(); }));
                foreach (IGrouping<string, SimReadyMappingRow> group in rows.GroupBy(r => r.Status).OrderBy(g => StatusOrder(g.Key)))
                {
                    string status = group.Key;
                    counts.Add(CountChip(p, status, group.Count(), filter == status, () => { setFilter(status); Apply(); }));
                }
            }

            Apply();
            return root;
        }

        private static VisualElement CountChip(SimReadyPalette p, string status, int count, bool active, Action click)
        {
            Color color = status == AllFilter ? p.Muted : p.Status(status);
            var chip = new VisualElement();
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.marginRight = 6f;
            chip.style.marginTop = 4f;
            chip.style.paddingLeft = chip.style.paddingRight = 8f;
            chip.style.paddingTop = chip.style.paddingBottom = 3f;
            chip.style.backgroundColor = active ? new Color(color.r, color.g, color.b, 0.35f) : p.Chip;
            Border(chip, 1f, active ? color : Color.clear);
            Radius(chip, 10f);
            var number = new Label(count.ToString());
            number.style.unityFontStyleAndWeight = FontStyle.Bold;
            number.style.color = color;
            number.style.marginRight = 4f;
            var text = new Label(status);
            text.style.color = p.Text;
            chip.Add(number);
            chip.Add(text);
            chip.RegisterCallback<ClickEvent>(_ => click());
            return chip;
        }

        private static VisualElement MakeLogRow(SimReadyPalette p)
        {
            var row = new VisualElement();
            row.style.paddingLeft = row.style.paddingRight = 8f;
            row.style.paddingTop = row.style.paddingBottom = 4f;
            row.style.borderBottomWidth = 1f;
            row.style.borderBottomColor = p.Line;
            VisualElement head = Row();
            var pill = new Label { name = "status" };
            pill.style.fontSize = 10;
            pill.style.unityFontStyleAndWeight = FontStyle.Bold;
            pill.style.paddingLeft = pill.style.paddingRight = 6f;
            pill.style.paddingTop = pill.style.paddingBottom = 1f;
            pill.style.marginRight = 6f;
            Radius(pill, 8f);
            var path = new Label { name = "path" };
            path.style.color = p.Text;
            path.style.flexShrink = 1f;
            path.style.overflow = Overflow.Hidden;
            path.style.textOverflow = TextOverflow.Ellipsis;
            head.Add(pill);
            head.Add(path);
            row.Add(head);
            var unity = new Label { name = "unity" };
            unity.style.color = p.Muted;
            unity.style.fontSize = 11;
            unity.style.marginTop = 1f;
            row.Add(unity);
            var detail = new Label { name = "detail" };
            detail.style.color = p.Muted;
            detail.style.fontSize = 11;
            detail.style.whiteSpace = WhiteSpace.Normal;
            row.Add(detail);
            return row;
        }

        private static void BindLogRow(SimReadyPalette p, VisualElement row, SimReadyMappingRow entry)
        {
            Color color = p.Status(entry.Status);
            var pill = row.Q<Label>("status");
            pill.text = entry.Status.ToUpperInvariant();
            pill.style.color = color;
            pill.style.backgroundColor = new Color(color.r, color.g, color.b, 0.18f);
            var path = row.Q<Label>("path");
            path.text = $"{entry.Kind} · {Last(entry.PrimPath)}";
            path.tooltip = entry.PrimPath;
            row.Q<Label>("unity").text = string.IsNullOrEmpty(entry.Unity) ? string.Empty : "→ " + entry.Unity;
            var detail = row.Q<Label>("detail");
            detail.text = entry.Detail;
            detail.style.display = string.IsNullOrEmpty(entry.Detail) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static int StatusOrder(string status) =>
            Array.IndexOf(new[] { SimReadyPhysicsReport.Mapped, SimReadyPhysicsReport.Approximated, SimReadyPhysicsReport.Skipped,
                                  SimReadyPhysicsReport.Applied, SimReadyPhysicsReport.Recorded, SimReadyPhysicsReport.Logged }, status);

        // ---- The host allowlist ----

        /// <summary>The allowed hosts as removable chips, an add field, and the size caps.</summary>
        public static void HostSettings(SimReadyPalette p, VisualElement into, SimReadyHostPolicy policy, Action rebuild)
        {
            into.Add(Muted(p, "Downloads go over HTTPS to these hosts only, every redirect included. Add a host only if you trust it."));
            VisualElement chips = Wrapping();
            chips.style.marginTop = 6f;
            foreach (string host in policy.Hosts.ToList())
            {
                VisualElement chip = Pill(host, p.Chip, p.Text);
                var remove = new Label("  ×");
                remove.style.color = p.Muted;
                remove.tooltip = "Remove " + host;
                remove.RegisterCallback<ClickEvent>(_ =>
                {
                    policy.RemoveHost(host);
                    policy.Save();
                    rebuild();
                });
                chip.Add(remove);
                chips.Add(chip);
            }

            into.Add(chips);
            VisualElement add = Row();
            add.style.marginTop = 6f;
            var field = new TextField();
            field.textEdition.placeholder = "add a host, e.g. eu.aws.cdn.hf.co";
            field.style.flexGrow = 1f;
            field.style.marginLeft = 0f;
            Label message = Muted(p, string.Empty);
            message.style.color = p.Error;
            add.Add(field);
            add.Add(SecondaryButton("Add", () =>
            {
                if (policy.TryAddHost(field.value, out string error))
                {
                    policy.Save();
                    rebuild();
                }
                else
                {
                    message.text = error;
                }
            }));
            into.Add(add);
            into.Add(message);

            var fileCap = new IntegerField("Max file size (MB)") { value = (int)(policy.MaxFileBytes / (1024 * 1024)) };
            fileCap.RegisterValueChangedCallback(e =>
            {
                policy.MaxFileBytes = Math.Max(1, e.newValue) * 1024L * 1024L;
                policy.Save();
            });
            var totalCap = new IntegerField("Max total size (MB)") { value = (int)(policy.MaxTotalBytes / (1024 * 1024)) };
            totalCap.RegisterValueChangedCallback(e =>
            {
                policy.MaxTotalBytes = Math.Max(1, e.newValue) * 1024L * 1024L;
                policy.Save();
            });
            into.Add(fileCap);
            into.Add(totalCap);
        }

        // ---- Thumbnails ----

        /// <summary>The package thumbnail as a texture, or null when there is none to read.</summary>
        public static Texture2D LoadThumbnail(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                return null;
            }

            var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            if (texture.LoadImage(System.IO.File.ReadAllBytes(path)))
            {
                return texture;
            }

            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        // ---- Helpers ----

        public static void Radius(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = element.style.borderBottomRightRadius = radius;
        }

        public static void Border(VisualElement element, float width, Color color)
        {
            element.style.borderTopWidth = element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = element.style.borderRightWidth = width;
            element.style.borderTopColor = element.style.borderBottomColor = color;
            element.style.borderLeftColor = element.style.borderRightColor = color;
        }

        private static string Last(string path)
        {
            int slash = (path ?? string.Empty).LastIndexOf('/');
            return slash >= 0 && slash + 1 < path.Length ? path.Substring(slash + 1) : path ?? string.Empty;
        }
    }
}
