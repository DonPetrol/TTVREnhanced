// Tiny Town Enhanced - how World Select lists the worlds: three rows of thumbnails instead of two (WorldGrid), and a
// button for the order they are in (WorldSort).
// ----------------------------------------------------------------------------------------------------
// WorldGrid: World Select shows three rows of worlds instead of two.
// The game's first page is one large thumbnail (two rows high) and two small ones beside it; its other pages are
// two rows of three. A third row of three is added under both - nine worlds to a page, six on the first - by
// copying the game's own thumbnails, and everything is drawn a little smaller so that it fits. The game works out its pages from how many thumbnails there are, so the paging follows by itself.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine.UI;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class WorldGrid : IModule
    {
        public string Name { get { return "WorldGrid"; } }
        const float Shrink = 0.9f;          // the thumbnails' size, compared with the game's
        const float Lift = 18f;             // how far the middle of the three rows is above the middle of the page (panel units; the panel is 1400 wide)
        static readonly FieldInfo fFirst = WorldSelect.FirstPage, fNext = WorldSelect.NextPage;

        public void Start(Plugin plugin)
        {
            // before the game first lays out the thumbnails (and again in a new menu scene, whose thumbnails are new)
            plugin.Harmony.Patch(AccessTools.Method(typeof(MenuState), "UpdateButtons"), new HarmonyMethod(typeof(WorldGrid), "Grow"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // a thumbnail's picture sits two levels inside the object that is the whole tile
        static Transform Tile(RawImage thumb) { return thumb.transform.parent.parent; }

        static void Grow(MenuState __instance)
        {
            try
            {
                var first = (RawImage[])fFirst.GetValue(__instance); var next = (RawImage[])fNext.GetValue(__instance);
                if (next == null || first == null || next.Length != 6 || first.Length != 3) return;     // done already, or not the layout this was written for
                var menu = Tile(next[0]).GetComponentInParent<WristMenu>();
                Transform grid = Tile(next[0]).parent;                   // everything is measured in the second page's own space

                // the middle of the two rows, the lower row, and the distance between rows once shrunk
                Vector3 middle = Vector3.zero; float top = float.MinValue, bottom = float.MaxValue;
                foreach (RawImage thumb in next)
                {
                    Vector3 p = Tile(thumb).localPosition;
                    middle.x += p.x / next.Length; top = Mathf.Max(top, p.y); bottom = Mathf.Min(bottom, p.y);
                }
                middle.y = (top + bottom) * 0.5f;
                float pitch = (top - bottom) * Shrink;
                float lift = Lift * (menu != null && grid.lossyScale.y > 0f ? menu.transform.lossyScale.y / grid.lossyScale.y : 1f);
                var lower = new List<RawImage>();
                foreach (RawImage thumb in next) if (Tile(thumb).localPosition.y < middle.y) lower.Add(thumb);
                lower.Sort((a, b) => Tile(a).localPosition.x.CompareTo(Tile(b).localPosition.x));

                // every thumbnail there is: smaller, about the middle, and up to make room for the new row underneath
                foreach (RawImage[] page in new[] { first, next })
                    foreach (RawImage thumb in page)
                    {
                        Transform tile = Tile(thumb);
                        Vector3 p = grid.InverseTransformPoint(tile.position);
                        p = middle + (p - middle) * Shrink + new Vector3(0f, lift + pitch * 0.5f, 0f);
                        tile.position = grid.TransformPoint(p);
                        tile.localScale = tile.localScale * Shrink;
                    }

                // the third row: copies of the lower row, one row further down, on both pages
                var moreFirst = new List<RawImage>(first); var moreNext = new List<RawImage>(next);
                foreach (RawImage thumb in lower)
                {
                    Vector3 below = grid.TransformPoint(grid.InverseTransformPoint(Tile(thumb).position) - new Vector3(0f, pitch, 0f));
                    moreNext.Add(Copy(thumb, Tile(thumb).parent, below));
                    moreFirst.Add(Copy(thumb, Tile(first[0]).parent, below));
                }
                fFirst.SetValue(__instance, moreFirst.ToArray()); fNext.SetValue(__instance, moreNext.ToArray());
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldGrid: " + e); }
        }

        // a copy of a whole tile, in the given place; gives back the copy's picture (which is what the game keeps a list of)
        static RawImage Copy(RawImage thumb, Transform parent, Vector3 position)
        {
            Transform tile = Tile(thumb);
            var copy = (GameObject)UnityEngine.Object.Instantiate(tile.gameObject, parent, false);
            copy.name = tile.name + " (row 3)";
            copy.transform.position = position; copy.transform.rotation = tile.rotation;
            // (the same scale in the world as the tile it was copied from, whichever page it is put on)
            float theirs = tile.parent.lossyScale.x, mine = parent.lossyScale.x;
            copy.transform.localScale = tile.localScale * (mine > 0f ? theirs / mine : 1f);
            return copy.transform.GetChild(thumb.transform.parent.GetSiblingIndex()).GetChild(thumb.transform.GetSiblingIndex()).GetComponent<RawImage>();
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // WorldSort: the order of the worlds in World Select.
    // The game lists worlds by the date of their picture (roughly: last saved first). A button in the bottom left of
    // World Select steps through: Last Played (the world you opened most recently first), Name (named worlds from A to
    // Z, then the unnamed), and Date Created (newest first). The choice is remembered.
    // "Last played" is the date of a small empty file, "played", that is put in a world's folder whenever the world
    // is opened or left (Saves.cs does that); a world that has none yet goes by its folder's own date.
    public class WorldSort : IModule
    {
        public string Name { get { return "WorldSort"; } }
        public const string PlayedFile = "played";
        static readonly string[] Orders = { "Last Played", "Name", "Date Created" };
        static readonly Type M = typeof(MenuState);
        static ConfigEntry<int> order; static GameObject button;

        public void Start(Plugin plugin)
        {
            order = plugin.Config.Bind("Gameplay", "WorldSort", 0, "The order of World Select: 0 last played, 1 name, 2 date created.");
            var sort = new HarmonyMethod(typeof(WorldSort), "Sort") { priority = Priority.Last };       // (after CreateWorld.cs has taken worlds out of the list)
            plugin.Harmony.Patch(AccessTools.Method(M, "LoadWorlds"), null, sort);
            plugin.Harmony.Patch(AccessTools.Method(M, "UpdateButtons"), null, new HarmonyMethod(typeof(WorldSort), "Shown"));
        }
        public void SceneLoaded(string scene) { button = null; }
        public void Tick() { }

        /// When a world was last opened or left: the date of its "played" file. Without one, its folder's own date -
        /// which is only roughly right, since that changes whenever any file in the folder comes or goes (giving
        /// the world a name, say), and is why the file is there.
        public static DateTime Played(string directory)
        {
            try
            {
                string played = Path.Combine(directory, PlayedFile);
                return File.Exists(played) ? File.GetLastWriteTime(played) : Directory.GetLastWriteTime(directory);
            }
            catch (Exception) { return DateTime.MinValue; }
        }

        /// note that the world in this folder is being played now (the folder is not made if it is not there)
        public static void Touch(string directory)
        {
            try { if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) File.WriteAllBytes(Path.Combine(directory, PlayedFile), new byte[0]); }
            catch (Exception e) { Plugin.Log.LogWarning("WorldSort: " + e.Message); }
        }

        // made: the date in the folder's name (the game names a new world by the date and time), else the folder's own
        static DateTime Created(string directory)
        {
            DateTime made;
            if (DateTime.TryParseExact(Path.GetFileName(directory.TrimEnd('/', '\\')), "d_M_yyyy_HH_mm_ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out made)) return made;
            try { return Directory.GetCreationTime(directory); } catch (Exception) { return DateTime.MinValue; }
        }

        // a world with what it is sorted by. All of it is found out once for each world before sorting: asking the
        // disk for a date at every comparison meant hundreds of such questions for a long list.
        class Row { public object World; public string Name; public DateTime Played, Created; }

        static void Sort(MenuState __instance)
        {
            try
            {
                var worlds = WorldSelect.Worlds(__instance);
                if (worlds == null || worlds.Count < 2) return;
                int by = Mathf.Clamp(order.Value, 0, Orders.Length - 1);
                var sorted = new List<Row>();
                foreach (object world in worlds)
                {
                    string directory = WorldSelect.Folder(world);
                    sorted.Add(new Row { World = world, Name = by == 1 ? WorldNames.Get(directory) : "", Played = Played(directory), Created = by == 2 ? Created(directory) : DateTime.MinValue });
                }
                sorted.Sort(delegate (Row a, Row b)
                {
                    if (by == 2) return b.Created.CompareTo(a.Created);
                    if (by == 1)
                    {
                        if ((a.Name.Length > 0) != (b.Name.Length > 0)) return a.Name.Length > 0 ? -1 : 1;
                        int names = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                        if (names != 0) return names;
                    }
                    return b.Played.CompareTo(a.Played);
                });
                for (int i = 0; i < sorted.Count; i++) worlds[i] = sorted[i].World;
                if (__instance.worldSelectPreviewImage != null)
                    __instance.worldSelectPreviewImage.texture = WorldSelect.Picture(worlds[0]);
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldSort: " + e.Message); }
        }

        static void Shown(MenuState __instance)
        {
            try
            {
                if (button != null || !WorldSelect.Find()) return;
                MenuState state = __instance;
                button = MenuPage.Paint(MenuPage.Button(WorldSelect.Menu, WorldSelect.OnSelection("TinyTownEnhanced Sort"), Label(), 400f, 56f, -470f, -362f, delegate
                {
                    order.Value = (Mathf.Clamp(order.Value, 0, Orders.Length - 1) + 1) % Orders.Length;
                    button.GetComponentInChildren<Text>(true).text = Label();
                    WorldSelect.Click(state);
                    AccessTools.Field(M, "pageIndex").SetValue(state, 0);       // back to the first page, in the new order
                    // (the list the game already has is put in the new order and laid out again; having the game read
                    // the worlds again would load every world's picture from the disk once more, for nothing)
                    Sort(state);
                    WorldSelect.Relay(state);
                }), MenuPage.Role.Quiet);
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldSort: " + e.Message); }
        }
        static string Label() { return "Sort: " + Orders[Mathf.Clamp(order.Value, 0, Orders.Length - 1)]; }
    }
}
