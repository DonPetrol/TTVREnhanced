// Tiny Town Enhanced - a wrist-menu page with category buttons and rows of settings.
// Used by the Settings page (SettingsTabs.cs) and the World page (WorldTab.cs). A row of category buttons sits under
// the title; only the chosen category's rows are shown, five at a time, with page arrows when there are more.
// Every control is a copy of one the game already has (its checkbox, its volume slider, its tab buttons, the Help
// page's arrows), so it looks and presses like the rest of the menu.
// Kinds of row: a checkbox, a button that steps through options, a slider, and a colour. Tiles are
// the exception to rows: buttons with a colour swatch, laid out three across. A colour
// row shows a swatch; pressing it opens a picker in place of the rows (a line of ready-made colours, then Hue /
// Saturation / Brightness sliders, and Back).
// Each row reads and writes its value through a pair of small functions, so the page does not care whether a value
// lives in the mod's settings file or in the world's file.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class MenuPage
    {
        // layout, in the page's own units (the panel is 1400 wide; 0,0 is the middle of the page; PanelSize made the
        // panel taller, so a page's area reaches PanelSize.Half further up and down than in the original game)
        const float ButtonsY = 244f + PanelSize.Half, ButtonHeight = 84f, ButtonGap = 12f, SideMargin = 60f, ButtonsMargin = 44f;
        const float FirstRowY = 125f + PanelSize.Half, RowSpacing = 112f; const int RowsPerPage = 5;
        const float PagerY = -302f - PanelSize.Half, PagerArrowX = 100f, PagerArrowScale = 1f;   // (the Help page draws its arrows at 1.7)
        const int TileColumns = 3; const float TileGap = 16f, TileLabel = 70f;
        /// the red of a button that undoes or removes things (a little redder than the menu's header)
        public static readonly Color Warning = new Color(0.90f, 0.27f, 0.24f);
        const float ControlLeft = -82f;                             // where a row's control starts (the checkbox's left edge)

        class Row
        {
            public string Category, Label; public List<GameObject> Parts = new List<GameObject>(); public GameObject Desc;
            public Func<bool> ShowDesc, Visible; public Action Sync; public int Index;
            public int Slot = -1; public float TileY;                // tiles only: which line of the grid it is on, and where that is
        }
        class Wanted
        {
            public string Kind, Category, Label, Desc; public string[] Options, Parts;
            public Func<bool> GetBool, Visible, NoteWhen; public Action<bool> SetBool;
            public Func<int> GetInt; public Action<int> SetInt;
            public Func<float> GetFloat; public Action<float> SetFloat;
            public float Min, Max, Step; public Func<float, string> Format;
            public Func<float, int, float> Nudge;       // (a slider: where its arrows go from a value, one way or the other; null = one step)
            public Action Press;
            public string ButtonLabel; public Action ButtonPress;     // (a checkbox row may have a button at its right, where the description would be)
            public Func<Color> GetColor; public Action<Color> SetColor;
        }

        readonly string name; readonly string[] categories, rowOrder;
        readonly List<Wanted> wanted = new List<Wanted>();
        readonly List<Row> rows = new List<Row>();
        readonly List<GameObject> buttons = new List<GameObject>();
        readonly Dictionary<string, int> pageOf = new Dictionary<string, int>();
        readonly Dictionary<string, GameObject> custom = new Dictionary<string, GameObject>();
        GameObject prev, next, pageLabel;
        Transform built, failed; WristMenu menu;                     // (failed: the page that could not be built, if any)
        readonly Dictionary<string, int> tileLines = new Dictionary<string, int>();   // per category with tiles: how many row places the tiles take
        bool syncing;

        // the colour picker (built only if the page has a colour row)
        static readonly Color[] Palette =
        {
            new Color(1f, 1f, 1f), new Color(0.6f, 0.6f, 0.6f), new Color(0.08f, 0.08f, 0.1f), new Color(0.9f, 0.15f, 0.15f),
            new Color(1f, 0.55f, 0.15f), new Color(1f, 0.9f, 0.3f), new Color(0.3f, 0.75f, 0.25f), new Color(0.2f, 0.75f, 0.7f),
            new Color(0.45f, 0.75f, 1f), new Color(0.15f, 0.3f, 0.85f), new Color(0.55f, 0.3f, 0.85f), new Color(1f, 0.55f, 0.75f)
        };
        Wanted picking;                                              // the colour row whose picker is open, if any
        float hue, saturation, brightness;                           // the picker's own values (so the hue survives going through grey or black)
        readonly List<GameObject> pickerParts = new List<GameObject>();
        readonly List<Action> pickerSync = new List<Action>();
        Text pickerTitle; Image pickerPreview;

        // the "are you sure?" page
        Action asking;                                               // what to do on yes, while the question is up
        readonly List<GameObject> askParts = new List<GameObject>();
        Text askQuestion, askYes;

        public string Current;
        /// no category buttons under the header: the page's owner decides which category shows
        public bool NoButtons;
        const float NoButtonsLift = 90f;      // with no buttons, the rows start this much higher (where the buttons would be)
        /// called whenever a category is shown
        public Action<string> Shown;

        /// rowOrder: labels in the order they should appear; rows not named follow in the order they were added
        public MenuPage(string name, string[] categories, string[] rowOrder)
        {
            this.name = name; this.categories = categories; this.rowOrder = rowOrder ?? new string[0];
            Current = categories[0];
        }

        // ---- what goes on the page (call before it is first opened)

        public void AddCheckbox(string category, string label, string description, Func<bool> get, Action<bool> set, Func<bool> visible = null)
        {
            wanted.Add(new Wanted { Kind = "checkbox", Category = category, Label = label, Desc = description, GetBool = get, SetBool = set, Visible = visible });
        }

        /// a checkbox with no description and, in its place at the right of the row, a button
        public void AddCheckboxWithButton(string category, string label, Func<bool> get, Action<bool> set, string buttonLabel, Action buttonPress)
        {
            wanted.Add(new Wanted { Kind = "checkbox", Category = category, Label = label, GetBool = get, SetBool = set, ButtonLabel = buttonLabel, ButtonPress = buttonPress });
        }

        /// All of a category's rows on one page, however many: the rows past the usual number are set closer together
        /// underneath (for a page with one or two rows too many to be worth a second page; no category uses it at present).
        public void OnePage(string category) { onePage.Add(category); }
        readonly HashSet<string> onePage = new HashSet<string>();
        const float TightSpacing = 88f;

        /// a button that steps through the options each time it is pressed; the value is the option's number
        public void AddChoice(string category, string label, string[] options, Func<int> get, Action<int> set, Func<bool> visible = null)
        {
            wanted.Add(new Wanted { Kind = "choice", Category = category, Label = label, Options = options, GetInt = get, SetInt = set, Visible = visible });
        }

        /// a slider from min to max in steps; format turns the value into the text shown beside it
        /// (noteWhen: a note in brackets at the end of the label is only shown while this says so)
        /// (nudge: where the arrows at its ends take it, from the value now and -1 or 1; by default one step. For a slider
        ///  whose value is turned into something else in uneven steps, where one step can come out as no change)
        public void AddSlider(string category, string label, float min, float max, float step, Func<float, string> format, Func<float> get, Action<float> set, Func<bool> visible = null, Func<bool> noteWhen = null, Func<float, int, float> nudge = null)
        {
            wanted.Add(new Wanted { Kind = "slider", Category = category, Label = label, Min = min, Max = max, Step = step, Format = format, GetFloat = get, SetFloat = set, Visible = visible, NoteWhen = noteWhen, Nudge = nudge });
        }

        /// a tile: a button with a colour swatch and its name; a category's tiles are laid out in a grid, three across
        public void AddTile(string category, string label, Func<Color> swatch, Action press)
        {
            wanted.Add(new Wanted { Kind = "tile", Category = category, Label = label, GetColor = swatch, Press = press });
        }

        /// a colour: a swatch that opens the colour picker when pressed
        public void AddColor(string category, string label, Func<Color> get, Action<Color> set, Func<bool> visible = null)
        {
            wanted.Add(new Wanted { Kind = "color", Category = category, Label = label, GetColor = get, SetColor = set, Visible = visible });
        }

        /// one of the game's own rows already on this page: its parts are found by name
        public void AddGameRow(string category, string label, string descName, Func<bool> showDesc, params string[] partNames)
        {
            wanted.Add(new Wanted { Kind = "game", Category = category, Label = label, Desc = descName, GetBool = showDesc, Parts = partNames });
        }

        /// a category that shows this object as well as its rows (for example the game's own snapping page)
        public void SetCustom(string category, GameObject content) { custom[category] = content; }

        /// a category's rows start this many row places down (to sit under content of its own)
        public void RowsStartAt(string category, int row) { startRow[category] = row; }
        readonly Dictionary<string, int> startRow = new Dictionary<string, int>();

        /// the category's own content (SetCustom) has the whole first page to itself, and its rows start on page 2
        /// (WorldTab's Placement: the game's snapping page first, the scaling rows after it)
        public void OwnFirstPage(string category) { startRow[category] = RowsPerPage; firstPageOnly.Add(category); }
        readonly HashSet<string> firstPageOnly = new HashSet<string>();

        /// go back to a category's first page (the next time it is shown)
        public void ToFirstPage(string category) { pageOf[category] = 0; }

        /// the part of a page free for content of its own: under the category buttons, over the page arrows
        /// (in the page's own units, 0,0 being its middle)
        public static Rect ContentArea
        {
            get
            {
                float top = ButtonsY - ButtonHeight * 0.5f - ButtonGap * 2f, bottom = PagerY + 50f, side = 700f - SideMargin;
                return Rect.MinMaxRect(-side, bottom, side, top);
            }
        }

        /// is the page on show right now
        public bool Visible { get { return built != null && built.gameObject.activeInHierarchy; } }

        /// put a question in place of the rows, with a button to go ahead (in the given colour) and Cancel
        public void Ask(string question, string yes, Action action)
        {
            if (built == null) return;
            picking = null; asking = action;
            if (askQuestion != null) askQuestion.text = question;
            if (askYes != null) askYes.text = yes;
            Show();
        }

        /// A red button in the right-hand corner of the menu's header (where the main menu has its "+"). Call once the
        /// page has been opened; the caller shows and hides it with its page.
        /// (null if the page could not be built, so there is no menu to put it on)
        public GameObject HeaderButton(string text, Action pressed)
        {
            if (menu == null) return null;
            return HeaderButton(menu, name + " " + text, text, Warning, 124f, delegate { Click(); pressed(); });
        }

        /// a button in the wrist menu's header, at its right-hand end (the same place as the pages' Reset and Save), in
        /// the given colour and width, rounded, its words filling it; hidden until the caller shows it
        /// how tall the header buttons are, and how far in from the header's top, bottom and end
        static bool toldHeader;
        // how much wider than asked a header button is (the widths are for the old 76-high buttons): less than it grows
        // in height, so the frame-rate counter and the World tab's notice still fit beside it
        static float Grow(float tall) { return 1f + (tall / 76f - 1f) * 0.5f; }
        static void HeaderButtonSize(WristMenu menu, out float tall, out float room)
        {
            // (most of the header's height, with an even gap round it; 190 if the header's size cannot be read yet, which
            //  is about what it is)
            var bar = menu.title.transform.parent as RectTransform;
            float header = bar != null && bar.rect.height > 1f ? bar.rect.height : 190f;
            if (!toldHeader) { toldHeader = true; Plugin.Log.LogDebug("MenuPage: header is " + (bar != null ? bar.rect.height.ToString("F0") : "?") + " high"); }
            tall = Mathf.Clamp(header * 0.66f, 76f, 140f);
            room = Mathf.Clamp((header - tall) * 0.5f, 14f, 40f);
        }
        /// how far in from the header's end the widest header button (Changelog, 150 wide) reaches: FpsCounter.cs stays clear of it
        public static float HeaderButtonsReach(WristMenu menu)
        {
            float tall, room; HeaderButtonSize(menu, out tall, out room);
            return room + 150f * Grow(tall);
        }
        public static GameObject HeaderButton(WristMenu menu, string buttonName, string text, Color color, float width, Action pressed)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(menu.settingsPageTabButton.gameObject, menu.title.transform.parent, false);
            go.name = buttonName;
            var rt = (RectTransform)go.transform;
            // (sized to the header: about half its height, the width growing with it, and as far in from the end as from
            //  the top and bottom, so it sits in the corner evenly; the widths asked for are for the old 76-high buttons)
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0.5f);
            float tall, room; HeaderButtonSize(menu, out tall, out room);
            rt.sizeDelta = new Vector2(width * Grow(tall), tall);
            rt.anchoredPosition = new Vector2(-room, 0f);
            rt.localRotation = Quaternion.identity; rt.localScale = Vector3.one;
            var face = go.GetComponent<Image>();
            face.color = color;
            // (rounded like the rest of the mod's buttons)
            Round(face);
            var label = go.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = text; label.color = Color.white; label.alignment = TextAnchor.MiddleCenter;
                // (the words fill the button, with a little room all round, and shrink to fit it: best fit only works
                // inside the text's own box, so the box is the button and the words may not spill out of it)
                var lr = label.rectTransform;
                lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one; lr.offsetMin = new Vector2(10f, 6f); lr.offsetMax = new Vector2(-10f, -6f);
                label.horizontalOverflow = HorizontalWrapMode.Wrap; label.verticalOverflow = VerticalWrapMode.Truncate;
                Fit(label, 10);
            }
            fOriginalColor.SetValue(go.GetComponent<PushButton>(), color);     // (what it goes back to after flashing when pressed)
            OnPress(go, buttonName, pressed);
            go.SetActive(false);
            return go;
        }

        // ---- showing it

        /// build the page if it has not been built for this object yet, then show the current category
        public void Open(Transform page, WristMenu wristMenu)
        {
            try
            {
                if (built != page && failed != page)
                {
                    // A page that cannot be built (something to copy was not found) is not tried again every time it
                    // is opened: each try would leave another set of half-made controls on top of the last. What this
                    // try made is taken away again; the game's own page is then shown as it was.
                    int had = page.childCount;
                    try { Build(page, wristMenu); }
                    catch (Exception)
                    {
                        failed = page; built = null;
                        rows.Clear(); buttons.Clear(); pickerParts.Clear(); pickerSync.Clear(); askParts.Clear();
                        for (int i = page.childCount - 1; i >= had; i--) UnityEngine.Object.Destroy(page.GetChild(i).gameObject);
                        throw;
                    }
                }
                picking = null; asking = null;
                Show();
            }
            catch (Exception e) { Plugin.Log.LogWarning(name + ": " + e); }
        }

        void Click() { if (menu != null) menu.soundManager.Play(SoundManager.SoundEffect.BUTTON_CLICK_POSITIVE); }

        void Build(Transform t, WristMenu wristMenu)
        {
            rows.Clear(); buttons.Clear(); pickerParts.Clear(); pickerSync.Clear(); askParts.Clear();
            menu = wristMenu;
            if (menu == null) throw new Exception("wrist menu not found");
            var settings = menu.settingsPage; Transform templates = settings.transform;     // the rows to copy live on the Settings page
            var ST = typeof(WristSettingsPage);
            var onSprite = (Sprite)AccessTools.Field(ST, "checkedSprite").GetValue(settings);
            var offSprite = (Sprite)AccessTools.Field(ST, "uncheckedSprite").GetValue(settings);

            var tiles = new Dictionary<string, int>();               // how many tiles each category has so far
            foreach (var w in wanted)
            {
                var want = w;
                var row = new Row { Category = want.Category, Label = want.Label, Visible = want.Visible };
                if (want.Kind == "game")
                {
                    foreach (string part in want.Parts)
                    {
                        Transform p = t.Find(part);
                        if (p == null) throw new Exception("not found on the page: " + part);
                        row.Parts.Add(p.gameObject);
                    }
                    if (want.Desc != null) { Transform d = t.Find(want.Desc); if (d != null) row.Desc = d.gameObject; }
                    if (want.Desc != null && row.Parts.Count > 0)
                    {   // the game ends its checkbox labels with a colon; the checkboxes added by the mod have none
                        var own = row.Parts[0].GetComponentInChildren<Text>(true);
                        if (own != null) own.text = own.text.TrimEnd().TrimEnd(':');
                    }
                    row.ShowDesc = want.GetBool;
                    rows.Add(row);
                    continue;
                }

                if (want.Kind == "tile")
                {
                    int index; tiles.TryGetValue(want.Category, out index); tiles[want.Category] = index + 1;
                    float full = menu.GetComponent<RectTransform>().rect.width - 2f * SideMargin, width = (full - (TileColumns - 1) * TileGap) / TileColumns;
                    var go = TabButtonCopy(t, want.Label + " Tile", width, 96f, -full * 0.5f + width * 0.5f + (index % TileColumns) * (width + TileGap));
                    Tone(go, Blue);
                    var text = go.GetComponentInChildren<Text>(true);
                    if (text != null)
                    {
                        text.text = want.Label; text.color = menu.selectedTextColor;
                    }
                    var swatch = Panel(go.transform, 10f, 10f);       // (sized below, once the number of lines is known)
                    row.Sync = delegate { swatch.color = Opaque(want.GetColor()); };
                    Press(go, delegate { want.Press(); Click(); Show(); });
                    row.Parts.Add(go); row.Slot = index / TileColumns;
                    rows.Add(row);
                    continue;
                }

                var label = Copy(templates, t, "HighResScreenshotToggleLabel", want.Label + " Label");
                // a note in brackets at the end of a label ("Fog (Costs FPS)") goes on a line of its own underneath, in
                // smaller text; with NoteWhen the note is only there while that says so
                string colon = want.Kind == "checkbox" ? "" : ":";
                int note = want.Label.IndexOf(" (");
                var labelText = label.GetComponentInChildren<Text>(true);
                string plain = (note < 0 ? want.Label : want.Label.Substring(0, note)) + colon, noted = note < 0 ? plain : plain + "\n" + want.Label.Substring(note + 1);
                int size = labelText != null ? labelText.fontSize : 0;
                Action relabel = delegate
                {
                    if (labelText == null) return;
                    bool on = note >= 0 && (want.NoteWhen == null || want.NoteWhen());
                    labelText.text = on ? noted : plain;
                    labelText.fontSize = on ? Mathf.Max(10, Mathf.RoundToInt(size * 0.8f)) : size;
                };
                if (labelText != null && note >= 0)
                {
                    labelText.resizeTextForBestFit = false;
                    labelText.horizontalOverflow = HorizontalWrapMode.Overflow; labelText.verticalOverflow = VerticalWrapMode.Overflow;
                }
                relabel();
                row.Parts.Add(label);

                if (want.Kind == "checkbox")
                {
                    var boxGo = Copy(templates, t, "HighResScreenshotToggleCheckbox", want.Label + " Checkbox");
                    var box = boxGo.GetComponent<Image>();
                    row.Sync = delegate { box.sprite = want.GetBool() ? onSprite : offSprite; };
                    Press(boxGo, delegate { want.SetBool(!want.GetBool()); Click(); Show(); });
                    row.Parts.Add(boxGo);
                    if (want.Desc != null)
                    {
                        var desc = Copy(templates, t, "HighResScreenshotToggleDesc", want.Label + " Desc");
                        SetText(desc, want.Desc);
                        // (light grey, not the game's red: it says what the setting does, it is not a warning)
                        foreach (var words in desc.GetComponentsInChildren<Text>(true)) words.color = Description;
                        row.Desc = desc; row.ShowDesc = want.GetBool;
                    }
                    if (want.ButtonLabel != null)
                    {
                        // (right of the checkbox, where a description would be)
                        var button = Paint(Button(menu, t, want.ButtonLabel, 300f, 70f, 330f), RoleFor(want.ButtonLabel, Role.Action));
                        SetText(button, want.ButtonLabel);
                        Press(button, delegate { want.ButtonPress(); Click(); Show(); });
                        row.Parts.Add(button);
                    }
                }
                else if (want.Kind == "choice")
                {
                    // a button showing the current option; each press moves to the next one
                    const float width = 330f;
                    var go = TabButtonCopy(t, want.Label + " Choice", width, 75f, ControlLeft + width * 0.5f);
                    var text = go.GetComponentInChildren<Text>(true);
                    Tone(go, Blue);
                    if (text != null) text.color = menu.selectedTextColor;
                    row.Sync = delegate { if (text != null) text.text = want.Options[Mathf.Clamp(want.GetInt(), 0, want.Options.Length - 1)]; };
                    Press(go, delegate { want.SetInt((Mathf.Clamp(want.GetInt(), 0, want.Options.Length - 1) + 1) % want.Options.Length); Click(); Show(); });
                    row.Parts.Add(go);
                }
                else if (want.Kind == "color")
                {
                    Image inner;
                    var go = Swatch(t, want.Label + " Swatch", 330f, 75f, ControlLeft + 165f, out inner);
                    row.Sync = delegate { inner.color = Opaque(want.GetColor()); };
                    Press(go, delegate
                    {
                        picking = want;
                        Color.RGBToHSV(want.GetColor(), out hue, out saturation, out brightness);
                        Click(); Show();
                    });
                    row.Parts.Add(go);
                }
                else if (want.Kind == "slider")
                {
                    Action sync = Slider(t, templates, want.Label, want.Min, want.Max, want.Step, want.Format, want.GetFloat,
                                         delegate (float v) { want.SetFloat(v); if (want.NoteWhen != null) relabel(); }, row.Parts, want.Nudge);
                    row.Sync = delegate { sync(); if (want.NoteWhen != null) relabel(); };
                }
                rows.Add(row);
            }

            // tiles fill the space the rows would have had: the lines share its height; each tile is its swatch with
            // its name underneath
            // (ordinary rows in the same category go underneath, and the tiles leave room for them)
            tileLines.Clear();
            foreach (var category in tiles.Keys)
            {
                int below = 0;
                foreach (var row in rows) if (row.Category == category && row.Slot < 0) below++;
                tileLines[category] = Mathf.Max(1, RowsPerPage - below);
            }
            foreach (var kv in startRow) tileLines[kv.Key] = kv.Value;
            foreach (var row in rows)
            {
                if (row.Slot < 0) continue;
                float top = FirstRowY + 48f, bottom = FirstRowY - (tileLines[row.Category] - 1) * RowSpacing - 48f;
                int lines = (tiles[row.Category] + TileColumns - 1) / TileColumns;
                float height = (top - bottom - (lines - 1) * TileGap) / lines;
                var go = row.Parts[0]; var rt = (RectTransform)go.transform;
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
                row.TileY = top - height * 0.5f - row.Slot * (height + TileGap);
                var swatch = (RectTransform)go.transform.Find("Colour");
                swatch.sizeDelta = new Vector2(rt.sizeDelta.x - 28f, height - TileLabel - 22f);
                swatch.localPosition = new Vector3(0f, TileLabel * 0.5f - 3f, 0f);
                var text = go.GetComponentInChildren<Text>(true);
                if (text != null)
                {
                    var tr = text.rectTransform;
                    tr.anchorMin = new Vector2(0f, 0f); tr.anchorMax = new Vector2(1f, 0f); tr.pivot = new Vector2(0.5f, 0f);
                    tr.sizeDelta = new Vector2(-20f, TileLabel); tr.anchoredPosition = new Vector2(0f, 4f);
                }
                go.GetComponent<PushButton>().UpdateBoxCollider();
            }

            // the rows in their chosen order (they are put in their places by Show, because which are visible can change),
            // and every description kept inside its own row: the text shrinks if it would not fit
            var ordered = new List<Row>(rows);
            var added = new Dictionary<Row, int>();
            for (int i = 0; i < rows.Count; i++) added[rows[i]] = i;
            ordered.Sort(delegate (Row a, Row b)
            {
                int x = Array.IndexOf(rowOrder, a.Label), y = Array.IndexOf(rowOrder, b.Label);
                if (x < 0) x = rowOrder.Length + added[a];
                if (y < 0) y = rowOrder.Length + added[b];
                return x.CompareTo(y);
            });
            rows.Clear(); rows.AddRange(ordered);
            foreach (var row in rows)
            {
                if (row.Desc == null) continue;
                var text = row.Desc.GetComponentInChildren<Text>(true);
                if (text == null) continue;
                var rt = text.rectTransform;
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, RowSpacing - 16f);
                Fit(text, 10);
                text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            }

            // the category buttons: copies of the bottom "Settings" tab button
            float panel = menu.GetComponent<RectTransform>().rect.width;
            float each = (panel - 2f * ButtonsMargin - (categories.Length - 1) * ButtonGap) / categories.Length;
            for (int i = 0; i < categories.Length && !NoButtons; i++)      // (a page without buttons has none made)
            {
                string category = categories[i];
                var go = TabButtonCopy(t, category + " Category", each, ButtonHeight, -panel * 0.5f + ButtonsMargin + each * 0.5f + i * (each + ButtonGap));
                SetY(go, ButtonsY);
                var words = go.GetComponentInChildren<Text>(true);          // (with room round the words)
                if (words != null) { words.rectTransform.offsetMin = new Vector2(20f, 14f); words.rectTransform.offsetMax = new Vector2(-20f, -14f); }
                SetText(go, category);
                Press(go, delegate
                {
                    if (Current == category && picking == null && asking == null) return;
                    Current = category; picking = null; asking = null; Click(); Show();
                });
                buttons.Add(go);
            }

            // page arrows (copies of the Help page's) and "1 / 2" between them; only shown when a category needs them
            prev = Arrow(t, "Prev", -PagerArrowX, -1);
            next = Arrow(t, "Next", PagerArrowX, 1);
            pageLabel = Copy(templates, t, "HighResScreenshotToggleLabel", "Page Number");
            var prt = (RectTransform)pageLabel.transform;
            prt.sizeDelta = new Vector2(PagerArrowX * 2f - 60f, prt.sizeDelta.y);
            prt.localPosition = new Vector3(0f, PagerY, prt.localPosition.z);
            var ptext = pageLabel.GetComponentInChildren<Text>(true);
            if (ptext != null) ptext.alignment = TextAnchor.MiddleCenter;
            foreach (var w in wanted) if (w.Kind == "color") { BuildPicker(t, templates, panel); break; }

            // the question page: the question across the middle, and two buttons under it
            var q = Copy(templates, t, "HighResScreenshotToggleLabel", "Question");
            var qrt = (RectTransform)q.transform;
            qrt.sizeDelta = new Vector2(panel - 2f * SideMargin, RowSpacing * 1.6f);
            qrt.localPosition = new Vector3(0f, FirstRowY - RowSpacing * 0.8f, qrt.localPosition.z);
            askQuestion = q.GetComponentInChildren<Text>(true);
            if (askQuestion != null) { askQuestion.alignment = TextAnchor.MiddleCenter; askQuestion.horizontalOverflow = HorizontalWrapMode.Wrap; askQuestion.verticalOverflow = VerticalWrapMode.Overflow; }
            var yes = TabButtonCopy(t, "Question Yes", 320f, 96f, -190f);
            Tone(yes, Warning);
            askYes = yes.GetComponentInChildren<Text>(true);
            if (askYes != null) askYes.color = Color.white;
            Press(yes, delegate { var go = asking; asking = null; if (go != null) go(); Click(); Show(); });
            var no = TabButtonCopy(t, "Question Cancel", 320f, 96f, 190f);
            SetText(no, "Cancel");
            Tone(no, Blue);
            var noText = no.GetComponentInChildren<Text>(true);
            if (noText != null) noText.color = menu.selectedTextColor;
            Press(no, delegate { asking = null; Click(); Show(); });
            Paint(yes, Role.Danger); Paint(no, Role.Quiet);
            foreach (var go in new[] { yes, no }) SetY(go, FirstRowY - RowSpacing * 2.6f);
            askParts.Add(q); askParts.Add(yes); askParts.Add(no);
            built = t;
        }

        // a slider with its value beside it, added to parts; returns what to call to show the current value
        Action Slider(Transform t, Transform templates, string label, float min, float max, float step, Func<float, string> format, Func<float> get, Action<float> set, List<GameObject> parts, Func<float, int, float> moveBy = null)
        {
            // a copy of the music volume slider, a little shorter to leave room for the value beside it
            var go = Copy(templates, t, "MusicVolume", label + " Slider");
            var slider = go.GetComponent<TouchSlider>();
            AccessTools.Field(typeof(TouchSlider), "minimum").SetValue(slider, min);
            AccessTools.Field(typeof(TouchSlider), "maximum").SetValue(slider, max);
            slider.onValueChanged = new TouchSlider.OnValueChangedEvent();   // the copy must not change the music volume
            // (shorter still, for a small arrow at each end: one step down, one step up, for fine adjustment)
            const float shorter = 0.72f, ArrowRoom = 56f, ArrowScale = 0.55f;
            float fullWidth = go.GetComponent<BoxCollider>().size.x;
            Vector3 lp = go.transform.localPosition, ls = go.transform.localScale;
            float left = lp.x - fullWidth * 0.5f;
            float barLeft = left + ArrowRoom, barRight = barLeft + fullWidth * shorter;
            go.transform.localScale = new Vector3(ls.x * shorter, ls.y, ls.z);
            go.transform.localPosition = new Vector3(barLeft + fullWidth * shorter * 0.5f, lp.y, lp.z);

            var value = Copy(templates, t, "HighResScreenshotToggleLabel", label + " Value");
            var vrt = (RectTransform)value.transform;
            vrt.sizeDelta = new Vector2(190f, vrt.sizeDelta.y);
            vrt.localPosition = new Vector3(barRight + ArrowRoom + 10f + 95f, vrt.localPosition.y, vrt.localPosition.z);
            var vtext = value.GetComponentInChildren<Text>(true);
            if (vtext != null) vtext.alignment = TextAnchor.MiddleLeft;

            float last = float.NaN;
            Action sync = delegate
            {
                float v = Mathf.Clamp(get(), min, max);
                if (v.Equals(last)) return;
                last = v;
                syncing = true;
                try { slider.Value = v; } finally { syncing = false; }
                if (vtext != null) vtext.text = format(v);
            };
            slider.onValueChanged.AddListener(delegate (float raw)
            {
                if (syncing) return;
                float v = Mathf.Clamp(Mathf.Round(raw / step) * step, min, max);
                if (Mathf.Approximately(v, last)) return;           // still within the same step
                last = v;
                if (vtext != null) vtext.text = format(v);
                try { set(v); } catch (Exception e) { Plugin.Log.LogWarning(name + ": " + e.Message); }
            });
            // the arrows: the page arrows, small, one step of the slider each press
            Action<int> nudge = delegate (int way)
            {
                float now = Mathf.Clamp(get(), min, max);
                float v = moveBy != null ? Mathf.Clamp(moveBy(now, way), min, max) : Mathf.Clamp(Mathf.Round((now + way * step) / step) * step, min, max);
                try { set(v); } catch (Exception e) { Plugin.Log.LogWarning(name + ": " + e.Message); }
                Click(); sync();
            };
            var down = PageArrow(menu, t, "Prev", barLeft - ArrowRoom * 0.5f, lp.y, delegate { nudge(-1); });
            var up = PageArrow(menu, t, "Next", barRight + ArrowRoom * 0.5f, lp.y, delegate { nudge(1); });
            foreach (var a in new[] { down, up })
            {
                a.name = label + (a == down ? " Down" : " Up");
                Vector3 sc = a.transform.localScale;
                a.transform.localScale = new Vector3(Mathf.Sign(sc.x) * ArrowScale, ArrowScale, sc.z);
                var pb = a.GetComponent<PushButton>(); if (pb != null) pb.UpdateBoxCollider();
            }
            parts.Add(go); parts.Add(value); parts.Add(down); parts.Add(up);
            return sync;
        }

        static Color Opaque(Color c) { return new Color(c.r, c.g, c.b, 1f); }

        // a button showing a colour (the colour is a panel inside the button, because a button flashes its own image when pressed)
        GameObject Swatch(Transform page, string swatchName, float width, float height, float x, out Image inner)
        {
            var go = TabButtonCopy(page, swatchName, width, height, x);
            Tone(go, menu.deselectedColor);
            SetText(go, "");
            inner = Panel(go.transform, width - 12f, height - 12f);
            return go;
        }

        // a plain coloured rectangle in the middle of a button
        static Image Panel(Transform parent, float width, float height)
        {
            var panel = new GameObject("Colour", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)panel.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(width, height);
            var image = panel.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        void Picked()
        {
            Color c = Color.HSVToRGB(Mathf.Clamp01(hue), Mathf.Clamp01(saturation), Mathf.Clamp01(brightness));
            if (pickerPreview != null) pickerPreview.color = c;
            try { picking.SetColor(c); } catch (Exception e) { Plugin.Log.LogWarning(name + ": " + e.Message); }
        }

        // the picker: the colour's name, its swatch and Back; a line of ready-made colours; Hue, Saturation, Brightness
        void BuildPicker(Transform t, Transform templates, float panel)
        {
            var title = Copy(templates, t, "HighResScreenshotToggleLabel", "Picker Title");
            pickerTitle = title.GetComponentInChildren<Text>(true);
            Image preview;
            var shown = Swatch(t, "Picker Preview", 330f, 75f, ControlLeft + 165f, out preview);
            Press(shown, delegate { });
            pickerPreview = preview;
            var back = TabButtonCopy(t, "Picker Back", 200f, 75f, panel * 0.5f - SideMargin - 100f);
            SetText(back, "Back");
            Tone(back, Blue);
            var backText = back.GetComponentInChildren<Text>(true);
            if (backText != null) backText.color = menu.selectedTextColor;
            Chevron(back);
            Press(back, delegate { picking = null; Click(); Show(); });
            Paint(back, Role.Quiet);
            foreach (var go in new[] { title, shown, back }) { SetY(go, FirstRowY); pickerParts.Add(go); }

            float gap = 16f, each = (panel - 2f * SideMargin - (Palette.Length - 1) * gap) / Palette.Length;
            for (int i = 0; i < Palette.Length; i++)
            {
                Color c = Palette[i]; Image inner;
                var go = Swatch(t, "Picker Colour " + i, each, 75f, -panel * 0.5f + SideMargin + each * 0.5f + i * (each + gap), out inner);
                inner.color = c;
                SetY(go, FirstRowY - RowSpacing);
                Press(go, delegate { Color.RGBToHSV(c, out hue, out saturation, out brightness); Picked(); Click(); Show(); });
                pickerParts.Add(go);
            }

            string[] names = { "Hue", "Saturation", "Brightness" };
            for (int i = 0; i < 3; i++)
            {
                int which = i;
                var parts = new List<GameObject>();
                var label = Copy(templates, t, "HighResScreenshotToggleLabel", "Picker " + names[i] + " Label");
                SetText(label, names[i] + ":");
                parts.Add(label);
                Func<float, string> format = which == 0 ? (Func<float, string>)(v => v.ToString("0") + "\u00B0") : (v => v.ToString("0") + "%");
                pickerSync.Add(Slider(t, templates, "Picker " + names[i], 0f, which == 0 ? 355f : 100f, 5f, format,
                    delegate { return which == 0 ? hue * 360f : (which == 1 ? saturation : brightness) * 100f; },
                    delegate (float v)
                    {
                        if (which == 0) hue = v / 360f; else if (which == 1) saturation = v / 100f; else brightness = v / 100f;
                        Picked();
                    }, parts));
                foreach (var go in parts) { SetY(go, FirstRowY - (2 + i) * RowSpacing); pickerParts.Add(go); }
            }
        }

        GameObject Arrow(Transform page, string which, float x, int step)
        {
            var go = PageArrow(menu, page, which, x, PagerY, delegate
            {
                pageOf[Current] = Mathf.Clamp(PageNow(Current) + step, 0, Pages(Current) - 1);
                Click(); Show();
            });
            go.name = "Page " + which;
            return go;
        }

        /// A copy of one of the Help page's own page arrows ("Prev" or "Next"), at (x, y) on a page, doing `pressed`:
        /// the arrows every paged list of this mod uses. The Help page's arrows are looked for anywhere on it (HelpIndex.cs
        /// moves them into a holder of its own), and each menu's are found only once.
        public static GameObject PageArrow(WristMenu menu, Transform page, string which, float x, float y, Action pressed)
        {
            if (arrowsOf != menu) { arrowsOf = menu; arrowTemplates.Clear(); }
            Transform source;
            if (!arrowTemplates.TryGetValue(which, out source) || source == null)
            {
                source = null;
                foreach (var t in menu.helpPage.GetComponentsInChildren<Transform>(true))
                    if (t.name == which && t.GetComponent<PushButton>() != null) { source = t; break; }
                if (source == null) throw new Exception("Help page arrow not found: " + which);
                arrowTemplates[which] = source;
            }
            var go = (GameObject)UnityEngine.Object.Instantiate(source.gameObject, page, false);
            go.SetActive(true);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.localPosition = new Vector3(x, y, 0f);
            Vector3 sc = rt.localScale;
            rt.localScale = new Vector3(Mathf.Sign(sc.x) * PagerArrowScale, PagerArrowScale, sc.z);     // (the Next arrow is a mirrored Prev)
            OnPress(go, "page arrow", pressed);
            return go;
        }
        static WristMenu arrowsOf; static readonly Dictionary<string, Transform> arrowTemplates = new Dictionary<string, Transform>();

        /// an empty box the size of a page, at its middle, to hold some of its content (shown and hidden as one)
        public static RectTransform Holder(Transform page, string name)
        {
            var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rt.SetParent(page, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = ((RectTransform)page).sizeDelta;
            rt.localPosition = Vector3.zero; rt.localRotation = Quaternion.identity; rt.localScale = Vector3.one;
            return rt;
        }

        /// a Holder with everything already on the page moved into it (a game page's own content, kept together so it can
        /// be shown and hidden as a whole without disturbing what the game switches on and off inside it)
        public static RectTransform Gather(Transform page, string name)
        {
            var holder = Holder(page, name);
            var existing = new List<Transform>();
            foreach (Transform child in page) if (child != holder) existing.Add(child);
            foreach (var child in existing) child.SetParent(holder, false);
            return holder;
        }

        int PageNow(string category) { int p; return pageOf.TryGetValue(category, out p) ? p : 0; }
        static bool IsVisible(Row row) { return row.Visible == null || row.Visible(); }

        int Pages(string category)
        {
            int n = 0, lines = 0;
            foreach (var row in rows)
                if (row.Category != category || !IsVisible(row)) continue;
                else if (row.Slot >= 0) tileLines.TryGetValue(category, out lines);     // (the tiles take a fixed number of row places)
                else n++;
            if (startRow.ContainsKey(category)) tileLines.TryGetValue(category, out lines);
            n += lines;
            if (onePage.Contains(category)) return 1;
            return Mathf.Max(1, (n + RowsPerPage - 1) / RowsPerPage);
        }

        // a copy of the bottom "Settings" tab button, as a free-standing button of the given size
        GameObject TabButtonCopy(Transform page, string buttonName, float width, float height, float x) { return Button(menu, page, buttonName, width, height, x); }

        /// a copy of the bottom "Settings" tab button, as a free-standing button of the given size (for any page of the wrist menu)
        public static GameObject Button(WristMenu menu, Transform page, string buttonName, float width, float height, float x)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(menu.settingsPageTabButton.gameObject, page, false);
            go.name = buttonName;
            go.SetActive(true);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(width, height);
            rt.localRotation = Quaternion.identity; rt.localScale = Vector3.one;
            rt.localPosition = new Vector3(x, 0f, 0f);
            var text = go.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.alignment = TextAnchor.MiddleCenter;
                Fit(text, 10);
                // (kept inside the button with a little room either side: a long word shrinks instead of spilling over)
                var tr = text.rectTransform;
                tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(10f, 0f); tr.offsetMax = new Vector2(-10f, 0f);
                text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            }
            return go;
        }

        /// a button in the menu's usual colours, with words on it, at a place on the page, that does `pressed`
        public static GameObject Button(WristMenu menu, Transform page, string label, float width, float height, float x, float y, Action pressed)
        {
            var go = Button(menu, page, label, width, height, x);
            var text = go.GetComponentInChildren<Text>(true);
            if (text != null) { text.text = label; text.color = menu.selectedTextColor; }
            Tone(go, Blue);
            go.transform.localPosition = new Vector3(x, y, 0f);
            OnPress(go, label, pressed);
            return go;
        }

        // ---- the colours of the buttons this mod adds, by what a button is for. They are the game's own menu colours:
        // its two purples, the green of its "+", the red of its bin, and a blue beside them.
        public enum Role
        {
            Go,         // the main thing to do on a page: open, create, confirm, done            (green)
            Action,     // something else you can do to what is shown: rename, duplicate, export  (blue)
            Choice,     // one of several of a kind: a backup, a file in a list                    (blue)
            Quiet,      // leaving or moving about: back, cancel, page arrows, sort               (blue)
            Danger,     // cannot be undone, or puts things back: delete, reset, clear, discard   (red)
            // (Choice and Quiet were the menu's purples; every button the mod adds is now the blue of the tabs)
        }
        public static readonly Color Ink = new Color(0.13f, 0.13f, 0.15f);
        public static readonly Color Description = new Color(0.62f, 0.62f, 0.64f);  // the words under a setting        // plain words on the panel (not on a button)
        public static readonly Color Green = new Color(0.30f, 0.74f, 0.68f);     // the green of the game's "+" button
        public static readonly Color Blue = new Color(0.33f, 0.55f, 0.90f);
        static readonly Color TabChosen = new Color(0.20f, 0.36f, 0.72f), TabWords = new Color(1f, 1f, 1f, 0.75f);     // a page's own tabs: the open one, and the words on the others
        static readonly System.Reflection.FieldInfo fOriginalColor = AccessTools.Field(typeof(PushButton), "originalColor");

        public static Color ColorOf(Role role)
        {
            return role == Role.Go ? Green : role == Role.Danger ? Warning : Blue;
        }
        /// a button that resets or clears something is red, whatever it was asked to be
        public static Role RoleFor(string label, Role role)
        {
            return label != null && (label.StartsWith("Reset") || label.StartsWith("Clear")) ? Role.Danger : role;
        }

        /// A free-standing checkbox (the Settings page's own box) with its words to the right, the box's middle at (x, y)
        /// on a page. Returns what to call to show the box ticked or not after the setting changes elsewhere.
        /// the colour of the words beside the Settings page's own checkboxes (the menus' grey)
        public static Color LabelColour(WristMenu menu)
        {
            foreach (var t in menu.settingsPage.GetComponentsInChildren<Transform>(true))
                if (t.name == "HighResScreenshotToggleLabel") { var text = t.GetComponentInChildren<Text>(true); if (text != null) return text.color; }
            return Ink;
        }

        public static Action Checkbox(WristMenu menu, Transform page, string words, float x, float y, float wordsWidth, Func<bool> get, Action<bool> set, float boxScale = 1f, int size = 34)
        {
            var settings = menu.settingsPage;
            var on = (Sprite)AccessTools.Field(typeof(WristSettingsPage), "checkedSprite").GetValue(settings);
            var off = (Sprite)AccessTools.Field(typeof(WristSettingsPage), "uncheckedSprite").GetValue(settings);
            Transform source = null;
            foreach (var t in settings.GetComponentsInChildren<Transform>(true)) if (t.name == "HighResScreenshotToggleCheckbox") { source = t; break; }
            if (source == null) throw new Exception("checkbox not found on the Settings page");
            var boxGo = (GameObject)UnityEngine.Object.Instantiate(source.gameObject, page, false);
            boxGo.name = words + " Checkbox"; boxGo.SetActive(true);
            var rt = (RectTransform)boxGo.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.localPosition = new Vector3(x, y, 0f);
            rt.localScale *= boxScale;
            var box = boxGo.GetComponent<Image>();
            Action sync = delegate { box.sprite = get() ? on : off; };
            OnPress(boxGo, words, delegate { set(!get()); sync(); });
            float half = rt.rect.width * 0.5f * rt.localScale.x;
            var label = Label(page, wordsWidth, size * 1.6f, x + half + 14f + wordsWidth * 0.5f, y, size);
            label.text = words; label.alignment = TextAnchor.MiddleLeft; label.color = LabelColour(menu);
            label.resizeTextForBestFit = true; label.resizeTextMinSize = 14; label.resizeTextMaxSize = size;
            sync();
            return sync;
        }

        /// give a button the colour of its role, white words, and (unless told not to) rounded corners
        /// (slightly rounded: the game's own rounded rectangle picture)
        public static GameObject Paint(GameObject button, Role role, bool round = true)
        {
            Color color = ColorOf(role);
            var face = button.GetComponent<Image>();
            if (round) Round(face);
            Tone(button, color);
            var text = button.GetComponentInChildren<Text>(true);
            if (text != null) text.color = Color.white;
            Transform arrow = button.transform.Find("Chevron");
            if (arrow != null) arrow.GetComponent<Image>().color = Color.white;
            return button;
        }

        /// A button's colour, and with it the colour the button goes back to after flashing when pressed. The game's
        /// button remembers that colour when it is made - for a copy, the colour of the tab button it was copied
        /// from - so a button only given a new colour would turn into the old one the first time it was pressed.
        public static void Tone(GameObject button, Color color)
        {
            var face = button.GetComponent<Image>();
            if (face != null) face.color = color;
            var push = button.GetComponent<PushButton>();
            if (push != null) fOriginalColor.SetValue(push, color);
        }

        // the game's rounded rectangle picture; looked for once only (the search goes through every picture the game
        // has loaded, so it is not repeated if there is none of that name)
        static Sprite rounded; static bool roundedLooked, chevronLooked;
        static Sprite Rounded()
        {
            if (!roundedLooked) { roundedLooked = true; rounded = SpriteNamed("roundedRect"); }
            return rounded;
        }

        /// give a button's picture the game's rounded corners (nothing happens if it has them, or the game has no such picture)
        public static void Round(Image face)
        {
            Sprite round = Rounded();
            if (face == null || round == null || face.sprite == round) return;
            face.sprite = round; face.type = round.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
        }

        /// Words that are drawn smaller (down to `smallest`) when they would not fit their box; never larger than they
        /// are now. The three settings this takes were written out wherever a button or label was made.
        public static void Fit(Text text, int smallest)
        {
            if (text == null) return;
            text.resizeTextMaxSize = Mathf.Max(text.fontSize, smallest); text.resizeTextMinSize = smallest; text.resizeTextForBestFit = true;
        }

        /// the wrist menu a page is on
        public static WristMenu MenuOf(Component page)
        {
            var menu = page != null ? page.GetComponentInParent<WristMenu>() : null;
            return menu != null ? menu : UnityEngine.Object.FindObjectOfType<WristMenu>();
        }

        /// where the small Back button of a page is (top left), and the row along the bottom of a page
        public const float BackX = -560f, BackY = 320f, BottomRowY = -330f;
        /// the Back button every page of the mod has: small, light purple, with the game's "<", top left
        public static GameObject BackButton(WristMenu menu, Transform page, Action pressed)
        {
            return Paint(Chevron(Button(menu, page, "Back", 200f, 60f, BackX, BackY, pressed)), Role.Quiet);
        }

        /// a button (see Button) already given the colour of its role
        public static GameObject Button(WristMenu menu, Transform page, string label, float width, float height, float x, float y, Action pressed, Role role)
        {
            return Paint(Button(menu, page, label, width, height, x, y, pressed), RoleFor(label, role));
        }

        /// one of the game's own pictures, by name (null if it has none of that name)
        public static Sprite SpriteNamed(string name)
        {
            foreach (Sprite s in Resources.FindObjectsOfTypeAll<Sprite>()) if (s.name == name) return s;
            return null;
        }

        /// the game's own menu font if it can be found, otherwise the engine's built-in one
        public static Font GameFont()
        {
            if (font != null) return font;
            foreach (var menu in Resources.FindObjectsOfTypeAll<WristMenu>())
                if (menu.title != null && menu.title.font != null) return font = menu.title.font;
            return font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        /// one of the game's own fonts by name (Open Sans in several weights, Laffayette Comic Pro), or Unity's Arial
        public static Font GameFont(string name)
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>()) if (f.name == name) return f;
            Plugin.Log.LogInfo("font " + name + " not found, using Arial");
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        static Font font;

        /// Words in the menu's header that may be long: drawn smaller (down to `smallest`) until they fit in `room`
        /// panel units, and past that cut short with "...". `normal` is the header's usual size.
        public static void FitTitle(Text title, string words, float room, int normal, int smallest)
        {
            int size = normal; string shown = words;
            while (size > smallest && Wide(title, shown, size) > room) size -= 2;
            if (Wide(title, shown, size) > room)
            {
                int keep = words.Length;
                while (keep > 1 && Wide(title, words.Substring(0, keep).TrimEnd() + "...", size) > room) keep--;
                shown = words.Substring(0, keep).TrimEnd() + "...";
            }
            title.fontSize = size; title.text = shown;
        }
        static float Wide(Text text, string words, int size)
        {
            text.fontSize = size;
            return text.cachedTextGeneratorForLayout.GetPreferredWidth(words, text.GetGenerationSettings(Vector2.zero)) / text.pixelsPerUnit;
        }

        /// plain words on a page (not a button): centred in a box of the given width at the given place
        public static Text Label(Transform page, float width, float height, float x, float y, int size)
        {
            var text = new GameObject("Words", typeof(RectTransform)).AddComponent<Text>();
            text.transform.SetParent(page, false);
            text.rectTransform.sizeDelta = new Vector2(width, height);
            text.rectTransform.localPosition = new Vector3(x, y, 0f);
            text.font = GameFont(); text.fontSize = size; text.color = Ink; text.alignment = TextAnchor.MiddleCenter; text.raycastTarget = false;
            return text;
        }

        /// Page arrows and "2 / 5" along the bottom of a page, for a list that is shown a few at a time. The page tells
        /// it how long the list is and how to show the things from First on; the arrows only appear when there is more
        /// than one page.
        public class Pager
        {
            public int First;                       // the place in the list of the first thing on the page being shown
            readonly int perPage; readonly Func<int> count; readonly Action show; readonly GameObject previous, next; readonly Text counter;

            public Pager(WristMenu menu, Transform page, float y, int perPage, Func<int> count, Action show, Action click)
            {
                this.perPage = perPage; this.count = count; this.show = show;
                previous = PageArrow(menu, page, "Prev", -PagerArrowX, y, delegate { click(); Turn(-1); });
                next = PageArrow(menu, page, "Next", PagerArrowX, y, delegate { click(); Turn(1); });
                counter = Label(page, PagerArrowX * 2f - 60f, 64f, 0f, y, 36);
            }

            void Turn(int pages)
            {
                int last = Mathf.Max(0, (count() - 1) / perPage) * perPage;
                First += pages * perPage;
                if (First < 0) First = last; else if (First > last) First = 0;
                Refresh();
            }

            /// show the page (call it when the page opens and whenever the list has changed)
            public void Refresh()
            {
                int n = count(), pages = Mathf.Max(1, (n + perPage - 1) / perPage);
                if (First >= n) First = 0;
                counter.text = pages > 1 ? (First / perPage + 1) + " / " + pages : "";
                previous.SetActive(pages > 1); next.SetActive(pages > 1);
                show();
            }
        }

        /// the game's "<" icon at the left of a button's words, in the words' colour: for buttons that go back
        public static GameObject Chevron(GameObject button)
        {
            if (!chevronLooked) { chevronLooked = true; chevron = SpriteNamed("chevron"); }
            var text = button.GetComponentInChildren<Text>(true);
            if (chevron == null || text == null || button.transform.Find("Chevron") != null) return button;
            float height = ((RectTransform)button.transform).sizeDelta.y, size = Mathf.Max(20f, height * 0.5f);
            var icon = new GameObject("Chevron", typeof(RectTransform)).AddComponent<Image>();
            icon.transform.SetParent(button.transform, false);
            icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = icon.rectTransform.pivot = new Vector2(0f, 0.5f);   // left edge, middle
            icon.rectTransform.sizeDelta = new Vector2(size, size);
            icon.rectTransform.anchoredPosition = new Vector2(14f, 0f);
            icon.sprite = chevron; icon.preserveAspect = true; icon.color = text.color; icon.raycastTarget = false;
            text.rectTransform.offsetMin = new Vector2(14f + size, text.rectTransform.offsetMin.y);     // the words keep clear of it
            return button;
        }
        static Sprite chevron;

        // what a copied push button does when pressed (replacing what the original did)
        void Press(GameObject go, Action action) { OnPress(go, name, action); }

        /// what a copied push button does when pressed (replacing what the original did)
        public static void OnPress(GameObject go, string owner, Action action)
        {
            var button = go.GetComponent<PushButton>();
            button.onButtonPress = new PushButton.OnButtonPressEvent();
            button.onButtonPress.AddListener(delegate (GameObject sender)
            {
                try { action(); } catch (Exception e) { Plugin.Log.LogWarning(owner + ": " + e.Message); }
            });
            button.UpdateBoxCollider();
        }

        // a copy of one of the Settings page's row parts, placed on this page at the same left-right position
        static GameObject Copy(Transform templates, Transform page, string original, string copyName)
        {
            Transform source = templates.Find(original);
            if (source == null) throw new Exception("not found on the Settings page: " + original);
            var go = (GameObject)UnityEngine.Object.Instantiate(source.gameObject, page, false);
            go.name = copyName;
            go.transform.localPosition = source.localPosition;
            return go;
        }

        static void SetText(GameObject go, string text)
        {
            var t = go.GetComponentInChildren<Text>(true);
            if (t != null) t.text = text;
        }

        static void SetY(GameObject go, float y)
        {
            Vector3 p = go.transform.localPosition;
            go.transform.localPosition = new Vector3(p.x, y, p.z);
        }

        /// Bring the values of the rows on show up to date, and nothing else: for a page that follows something which
        /// changes by itself while it is open (WorldTab's World Scale). Much lighter than Show, which also places and
        /// recolours everything - and would cut short the flash of a button that has just been pressed.
        public void SyncValues()
        {
            if (built == null || picking != null || asking != null) return;
            foreach (var row in rows)
                if (row.Sync != null && row.Parts.Count > 0 && row.Parts[0].activeSelf) row.Sync();
        }

        /// show the current category: its rows (the current page of them) with their values, its button highlighted,
        /// and the page arrows if needed
        public void Show()
        {
            if (built == null) return;
            int pages = Pages(Current), p = Mathf.Clamp(PageNow(Current), 0, pages - 1);
            int n = 0;                                               // rows of this category that are visible, in order
            tileLines.TryGetValue(Current, out n);                   // (rows start under the tiles, if it has any)
            foreach (var row in rows)
            {
                bool mine = row.Category == Current && IsVisible(row);
                if (picking != null || asking != null) mine = false; // the colour picker, or a question, takes the rows' place
                if (mine)
                {
                    row.Index = row.Slot >= 0 ? 0 : n++;
                    bool one = onePage.Contains(Current);
                    int place = one ? row.Index : row.Index % RowsPerPage;
                    // (the usual spacing down to the last usual place; any rows after that - one-page categories only - closer together)
                    float y = row.Slot >= 0 ? row.TileY : FirstRowY + (NoButtons ? NoButtonsLift : 0f) - Mathf.Min(place, RowsPerPage - 1) * RowSpacing - Mathf.Max(0, place - (RowsPerPage - 1)) * TightSpacing;
                    foreach (var part in row.Parts) SetY(part, y);
                    if (row.Desc != null) SetY(row.Desc, y);
                    mine = one || row.Index / RowsPerPage == p;
                }
                foreach (var part in row.Parts) part.SetActive(mine);
                if (mine && row.Sync != null) row.Sync();
                if (row.Desc != null) row.Desc.SetActive(mine && row.ShowDesc != null && row.ShowDesc());
            }
            foreach (var c in custom) if (c.Value != null) c.Value.SetActive(c.Key == Current && picking == null && asking == null && (p == 0 || !firstPageOnly.Contains(c.Key)));
            foreach (var part in askParts) part.SetActive(asking != null);
            foreach (var part in pickerParts) part.SetActive(picking != null);
            if (picking != null)
            {
                if (pickerTitle != null) pickerTitle.text = picking.Label + ":";
                if (pickerPreview != null) pickerPreview.color = Color.HSVToRGB(Mathf.Clamp01(hue), Mathf.Clamp01(saturation), Mathf.Clamp01(brightness));
                foreach (var sync in pickerSync) sync();
            }

            for (int i = 0; i < buttons.Count; i++)
            {
                bool chosen = categories[i] == Current;
                var image = buttons[i].GetComponent<Image>();
                // (blue and rounded, like the mod's other buttons that do something: the open tab darker, the rest lighter)
                Round(image);
                Tone(buttons[i], chosen ? TabChosen : Blue);
                var text = buttons[i].GetComponentInChildren<Text>(true);
                if (text != null) text.color = chosen ? Color.white : TabWords;
            }
            bool paged = pages > 1 && picking == null && asking == null;
            if (prev != null) prev.SetActive(paged && p > 0);
            if (next != null) next.SetActive(paged && p < pages - 1);
            if (pageLabel != null) { pageLabel.SetActive(paged); SetText(pageLabel, (p + 1) + " / " + pages); }
            if (Shown != null) Shown(Current);
        }
    }
}
