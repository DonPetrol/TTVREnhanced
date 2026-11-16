// Tiny Town Enhanced - the wrist menu's "Snapping" tab becomes "World", with tabs of its own inside it:
//   Placement  - page 1: the game's snapping page (opened first), spread across the page, and under it the Group
//                Tool's two buttons (GroupTool.cs);
//                page 2: scaling - World Scale (the zoom), Unlock World Scale and Reset Scale, Scale Ground,
//                Head-based Scale, Copy Up and Down, Ignore Copy Collision
//   Presets    - ready-made looks, each a button that sets several of the settings below at once
//   Atmosphere - sky and clouds
//   Lighting   - the sun: direction, height, brightness, shadows, and the fill light
//   Misc       - anything else that belongs to a world
// While one of the saved tabs is open the header also has a red Reset button (in the corner the main menu uses for
// its "+" button); it asks first, then puts every world setting back to the game's own.
// Everything except Placement is saved in the world's own file (WorldFile.cs); a notice in the header says so.
// Placement's own settings are not kept at all: they are back to their defaults every time the game starts (ForThisGame).
// The page itself is a MenuPage, the same as the Settings page. The settings on it are in WorldOptions.cs.
//
// A module adds a world setting with one call in its Start:
//     WorldTab.AddCheckbox("Misc", "key", "Label", "Shown beside the box while it is ticked.", () => gameValue, on => { ... });
//     WorldTab.AddColor("Atmosphere", "key", "Label", () => gameValue, c => { ... });
//     WorldTab.AddSlider("Lighting", "key", "Label", 0f, 2f, 0.05f, v => v.ToString("0.00"), () => gameValue, v => { ... });
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class WorldTab : IModule
    {
        public string Name { get { return "WorldTab"; } }
        const string TabName = "World", GamePage = "Placement";
        const string Notice = "Settings on this tab are saved in the world file and need the Tiny Town Enhanced mod to have any effect.";

        // the rows of each tab in the order they are shown, whichever module adds them (rows not named here follow)
        static readonly string[] Order =
        {
            "Time of Day",
            "Sky Top", "Sky Horizon", "Sky Bottom", "Sky Brightness", "Sky Blend Top", "Sky Blend Bottom",
            "Clouds", "Cloud Amount (Costs FPS)", "Cloud Size", "Cloud Height", "Cloud Color", "Cloud Distance (Costs FPS)", "Cloud Speed (Costs FPS)", "Cloud Direction",
            "Stars", "Stars All Round", "Star Amount", "Star Brightness", "Star Size", "Star Variation", "Star Color", "Fog (Costs FPS)", "Fog Density", "Fog Color",
            "Sun Direction", "Sun Height", "Sun Brightness", "Sun Light Color", "Sun Disc Color", "Sun Size", "Sun Glow", "Shadow Strength", "Fill Light", "Fill Light Color",
            "Water Plane", "Water Level", "Water Color", "Ground Shadows", "Ground Walls", "Ground Wall Color"
        };
        static readonly MenuPage page = new MenuPage("WorldTab", new[] { GamePage, "Presets", "Atmosphere", "Lighting", "Misc" }, Order);
        static WristSnappingPage built, tried;      // (tried: the page Prepare was last run for, whether or not it worked)
        static WristMenu menu;
        static Text notice; static GameObject reset; static GameObject saveNow;

        public static void AddCheckbox(string category, string key, string label, string description, Func<bool> gameValue, Action<bool> apply, Func<bool> visible = null)
        {
            WorldFile.AddBool(key, gameValue, apply);
            page.AddCheckbox(category, label, description, () => WorldFile.GetBool(key), v => WorldFile.SetBool(key, v), visible);
        }

        public static void AddSlider(string category, string key, string label, float min, float max, float step, Func<float, string> format, Func<float> gameValue, Action<float> apply, Func<bool> visible = null, Func<bool> noteWhen = null)
        {
            WorldFile.AddFloat(key, gameValue, apply);
            page.AddSlider(category, label, min, max, step, format, () => WorldFile.GetFloat(key), v => WorldFile.SetFloat(key, v), visible, noteWhen);
        }

        public static void AddColor(string category, string key, string label, Func<Color> gameValue, Action<Color> apply, Func<bool> visible = null)
        {
            WorldFile.AddColor(key, gameValue, apply);
            page.AddColor(category, label, () => WorldFile.GetColor(key), c => WorldFile.SetColor(key, c), visible);
        }

        /// a slider that is not itself a saved setting: it reads and writes through the two functions given
        public static void AddPlainSlider(string category, string label, float min, float max, float step, Func<float, string> format, Func<float> get, Action<float> set)
        {
            page.AddSlider(category, label, min, max, step, format, get, set);
        }

        /// a tile on a grid of buttons, with a colour swatch
        public static void AddTile(string category, string label, Func<Color> swatch, Action press) { page.AddTile(category, label, swatch, press); }

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(WorldTab);
            h.Patch(AccessTools.Method(typeof(WristMenu), "ResetForPlay"), null, new HarmonyMethod(me, "Rename"));
            h.Patch(AccessTools.Method(typeof(WristMenu), "OnSnappingTabClicked"), null, new HarmonyMethod(me, "Title"));
            h.Patch(AccessTools.Method(typeof(WristSnappingPage), "Activate"), null, new HarmonyMethod(me, "Opened"));
            h.Patch(AccessTools.Method(typeof(WristSnappingPage), "Deactivate"), null, new HarmonyMethod(me, "Closed"));
            // Placement: the game's own snapping controls have page 1 to themselves (Prepare draws them larger to fill it);
            // the scaling rows are on page 2.
            page.OwnFirstPage(GamePage);
            // World Scale: the zoom, as a slider. It is the same zoom the two-handed pinch changes, and like that it is
            // not saved with the world.
            // The slider's own value is how far along it is (0 to 1), turned into a scale by ScaleAt: evenly from 0.2x
            // to 4x as the game has it, or - with Unlock World Scale ticked - from 0.01x to 100x, each step the same
            // number of times bigger than the last (1x in the middle), since nobody could set 0.05x on an even slider.
            page.AddSlider(GamePage, "World Scale", 0f, 1f, 0.005f, v => ScaleWords(ScaleAt(v)),
                           () => PlaceOf(Singleton<WorldSettings>.Instance.Scale), delegate (float v) { Gestures.ZoomTo(ScaleAt(v)); ScaleReadout.Seen(); },
                           nudge: (place, way) => PlaceOf(NextScale(Singleton<WorldSettings>.Instance.Scale, way)));
            // under it: the zoom's limits lifted (Gestures.cs), and the zoom put back to 1x
            page.AddCheckboxWithButton(GamePage, "Unlock World Scale", () => Gestures.Unlocked, on => Gestures.Unlocked = on,
                                       "Reset Scale", delegate { Gestures.ZoomTo(1f); ScaleReadout.Seen(); });
            // ground pieces can be resized like any other object, across only (GroundScale.cs)
            page.AddCheckbox(GamePage, "Scale Ground", "Ground pieces can be resized with both hands, across only: they keep their thickness.",
                             () => GroundScale.On, on => GroundScale.On = on);
            // the world zooms around your head instead of between your hands (Gestures.cs; set in ZoomOption)
            page.AddCheckbox(GamePage, "Head-based Scale", "The world scales around your head, so you stay where you are.",
                             () => ZoomOption.On, on => ZoomOption.On = on);
            // Object Copying's extra ghosts above and below (CopyUpDown.cs)
            page.AddCheckbox(GamePage, "Copy Up and Down", "Object Copying also offers a copy above and below.",
                             () => CopyUpDown.On, on => CopyUpDown.On = on);
            // the copy ghosts are offered even where something is already in the way (CopyUpDown.cs)
            page.AddCheckbox(GamePage, "Ignore Copy Collision", "Copies can be made where something is already in the way, such as a ground tile inside another.",
                             () => CopyUpDown.IgnoreCollision, on => CopyUpDown.IgnoreCollision = on);
            page.Shown = delegate (string category)
            {
                if (notice != null) notice.gameObject.SetActive(category != GamePage);
                if (reset != null) reset.SetActive(category != GamePage);
                if (saveNow != null) saveNow.SetActive(category == GamePage);
            };
        }
        static float ScaleAt(float place)
        {
            if (!Gestures.Unlocked) return Mathf.Round(Mathf.Lerp(Gestures.GameSmallest, Gestures.GameLargest, place) * 20f) / 20f;     // (steps of 0.05x)
            float scale = Mathf.Pow(10f, Mathf.Lerp(Mathf.Log10(Gestures.Smallest), Mathf.Log10(Gestures.Largest), place));
            float unit = Step(scale);
            return Mathf.Clamp(Mathf.Round(scale / unit) * unit, Gestures.Smallest, Gestures.Largest);
        }
        // The slider's arrows: the next scale the slider can show, up or down. (One step along the slider is often less
        // than one step of the scale it shows - 0.05x, or two figures unlocked - so a plain step of the slider came out as
        // no change at all.)
        static float NextScale(float scale, int way)
        {
            if (!Gestures.Unlocked)
                return Mathf.Clamp(Mathf.Round(scale * 20f) / 20f + way * 0.05f, Gestures.GameSmallest, Gestures.GameLargest);
            scale = Mathf.Max(scale, Gestures.Smallest);
            // (going down from 1x the step is already the smaller one, so 1x goes to 0.99x)
            float unit = Step(way < 0 ? scale * 0.999f : scale);
            return Mathf.Clamp(Mathf.Round(scale / unit) * unit + way * unit, Gestures.Smallest, Gestures.Largest);
        }
        // unlocked, scales are shown to two figures (0.012x, 0.85x, 1.5x, 23x): this is one step of the second figure
        static float Step(float scale) { return Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(scale)) - 1f); }
        static float PlaceOf(float scale)
        {
            if (!Gestures.Unlocked) return Mathf.InverseLerp(Gestures.GameSmallest, Gestures.GameLargest, scale);
            return Mathf.InverseLerp(Mathf.Log10(Gestures.Smallest), Mathf.Log10(Gestures.Largest), Mathf.Log10(Mathf.Max(scale, 0.0001f)));
        }
        static string ScaleWords(float scale) { return scale.ToString(scale < 0.1f ? "0.000" : scale < 10f ? "0.00" : "0.0") + "x"; }

        public void SceneLoaded(string scene) { }
        // (the World Scale slider on the Placement tab follows the zoom while the page is open: only its value is brought
        // up to date, the page is not laid out again)
        public void Tick() { if (page.Visible && page.Current == GamePage) page.SyncValues(); }

        // the bottom tab's wording, and the title shown when it is opened
        static void Rename(WristMenu __instance)
        {
            try
            {
                var text = __instance.snappingPageTabButton.GetChild(0).GetComponent<Text>();
                if (text != null) text.text = TabName;
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldTab: " + e.Message); }
        }
        static void Title(WristMenu __instance)
        {
            if (__instance.snappingPage.gameObject.activeSelf && __instance.title != null) __instance.title.text = TabName;
        }

        // another bottom tab was opened: the notice in the header goes away with this page
        static void Closed()
        {
            if (notice != null) notice.gameObject.SetActive(false);
            if (reset != null) reset.SetActive(false);
            if (saveNow != null) saveNow.SetActive(false);
        }

        static void Opened(WristSnappingPage __instance)
        {
            try
            {
                if (built != __instance)
                {
                    // one try per page: a second one would put the first try's holder inside another and add a second notice
                    if (tried == __instance) return;
                    tried = __instance;
                    Prepare(__instance);
                }
                page.Current = GamePage;                           // Placement (its first page) is the one shown when the tab is opened
                page.ToFirstPage(GamePage);
                bool fresh = reset == null;
                page.Open(__instance.transform, menu);
                if (fresh) { MakeReset(); page.Show(); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldTab: " + e); }
        }

        static void Prepare(WristSnappingPage snappingPage)
        {
            Transform t = snappingPage.transform;
            menu = MenuPage.MenuOf(snappingPage);
            if (menu == null) throw new Exception("wrist menu not found");

            // everything the game put on this page goes into one holder, so it can be shown and hidden as a whole
            // without disturbing what the game itself switches on and off inside it
            var rt = MenuPage.Gather(t, "Snapping (game)");
            page.SetCustom(GamePage, rt.gameObject);
            // the game's "All Snapping is Disabled" line is not shown (the greyed-out buttons say it already): the game
            // still switches it on and off, but its words are never drawn
            var disabled = AccessTools.Field(typeof(WristSnappingPage), "snapOptionsDisabledLabel").GetValue(snappingPage) as GameObject;
            if (disabled != null) foreach (var words in disabled.GetComponentsInChildren<Text>(true)) words.enabled = false;
            // the game's controls spread over the page, with the Group Tool's buttons under them
            Fill(rt);

            // the notice shown in the header while a saved-with-the-world tab is open: a small copy of the title text,
            // between the title and the frame-rate counter
            var ngo = (GameObject)UnityEngine.Object.Instantiate(menu.title.gameObject, menu.title.transform.parent, false);
            ngo.name = "TinyTownEnhanced World Notice";
            var nrt = (RectTransform)ngo.transform;
            nrt.anchorMin = nrt.anchorMax = nrt.pivot = new Vector2(0.5f, 0.5f);
            // (a little left of the middle: the header buttons at the end are larger now, and the frame-rate counter
            //  moved left to stay clear of them)
            nrt.sizeDelta = new Vector2(540f, 120f);
            nrt.anchoredPosition = new Vector2(-10f, 0f);
            notice = ngo.GetComponent<Text>();
            notice.text = Notice;
            notice.alignment = TextAnchor.MiddleCenter;
            notice.fontStyle = FontStyle.Normal;
            notice.horizontalOverflow = HorizontalWrapMode.Wrap; notice.verticalOverflow = VerticalWrapMode.Truncate;
            notice.resizeTextMaxSize = Mathf.Max(10, Mathf.RoundToInt(menu.title.fontSize * 0.34f)); notice.resizeTextMinSize = 10; notice.resizeTextForBestFit = true;
            notice.raycastTarget = false;
            ngo.SetActive(false);

            built = snappingPage;
        }

        /// Lay the game's snapping controls out over the page, leaving a strip at the bottom for the Group Tool's buttons
        /// (GroupTool.cs). Each control (a title, an icon, an option button) is placed on its own: the columns are spread
        /// across the page's width as far as they will go (never closer than the game had them, and at most half as far
        /// again), while the controls themselves are drawn a little smaller, from the top of the page down. What is
        /// measured is everything on show when the page is first opened; controls the game switches on later (another
        /// look of the same button) are moved with the rest.
        static void Fill(RectTransform holder)
        {
            Bounds all;
            if (!Measure(holder, holder, false, out all)) return;
            Rect area = MenuPage.ContentArea;
            float room = area.height - GroupStrip;                                          // (the bottom strip is the group buttons')
            float spread = Mathf.Clamp(area.width / all.size.x, 1f, 1.5f);                  // how far apart the columns go
            float size = Mathf.Clamp(room / all.size.y, 0.7f, 1.15f);                       // how large each control is drawn
            var controls = new List<RectTransform>();
            Controls(holder, holder, all.size.x, controls);
            foreach (var c in controls)
            {
                Bounds own;
                if (!Measure(c, holder, true, out own)) continue;
                // where its middle goes: across, spread out from the page's middle; down, from the top of the page
                Vector3 to = new Vector3(area.center.x + (own.center.x - all.center.x) * spread, area.yMax - (all.max.y - own.center.y) * size, 0f);
                Vector3 pivot = holder.InverseTransformPoint(c.position);
                c.localScale *= size;
                Vector3 middleNow = pivot + (own.center - pivot) * size;                    // (its middle, once it is smaller)
                c.position += holder.TransformVector(to - middleNow);
            }
            GroupTool.AddPanel(menu, holder, new Rect(area.xMin, area.yMin - StripDrop, area.width, GroupStrip));
        }
        const float GroupStrip = 165f, StripDrop = 28f;     // (StripDrop: the strip sits that much lower, into the space above the page arrows)

        // the controls: the holder's children, except that one stretching across most of the page is a box of several
        // controls, and its own children are taken instead
        static void Controls(Transform t, RectTransform holder, float width, List<RectTransform> into)
        {
            foreach (Transform child in t)
            {
                var r = child as RectTransform;
                if (r == null) continue;
                Bounds b;
                if (!Measure(r, holder, true, out b)) continue;
                if (b.size.x > width * 0.45f && r.childCount > 0) Controls(r, holder, width, into);
                else into.Add(r);
            }
        }

        // the space a part of the page takes, in the holder's own measure (`hidden`: its parts not on show count too)
        static bool Measure(Transform t, RectTransform holder, bool hidden, out Bounds b)
        {
            b = new Bounds(); bool any = false;
            var corners = new Vector3[4];
            foreach (var g in t.GetComponentsInChildren<Graphic>(hidden))
            {
                if (!hidden && !g.enabled) continue;
                g.rectTransform.GetWorldCorners(corners);
                foreach (var c in corners)
                {
                    Vector3 p = holder.InverseTransformPoint(c); p.z = 0f;
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return any && b.size.x > 0f && b.size.y > 0f;
        }

        // the red Reset button in the header: made once the page has been built (see Opened)
        static void MakeReset()
        {
            reset = page.HeaderButton("Reset", delegate { page.Ask("Put all of this world's settings back to the game's own?", "Reset", delegate { WorldFile.Reset(); }); });
            if (reset == null) return;              // (the page could not be built)
            // on the Placement page the same corner has Save: a manual save without leaving the world (Saves.cs)
            saveNow = MenuPage.Paint(page.HeaderButton("Save", Saves.SaveNow), MenuPage.Role.Go);
        }
    }
}
