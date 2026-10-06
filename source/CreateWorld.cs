// Tiny Town Enhanced - making a new world. The game's "+" button in the corner of World Select's header opens a page
// with four choices instead of making an empty world at once:
//   Blank      a new empty world, as the "+" made
//   Template   the worlds the game comes with (the files in its DefaultWorlds folder)
//   Workshop   the Workshop worlds you are subscribed to, most recently subscribed first, with their Workshop names
//   Import     (the plain button underneath) the worlds zipped in "TTVREnhanced/CustomWorlds/Imported" in the game's folder - where a world's
//              page puts them with Export, and where a zip from elsewhere can be dropped; picking one unpacks it
//              as a new world (only the files a world is made of are taken out of the zip, and within limits of size)
// Picking a template or a Workshop world makes a new world that is a copy of it, and opens it; the original is
// never changed, so it can be picked again for another fresh copy.
//
// The game used to put all of these in the World Select list itself: its six ready-made worlds (World0 to World5 in
// the Worlds folder), and a copy of every subscribed Workshop world (wi_world_<number>). Those copies are left out
// of the list as long as they are untouched - still the same as the original, with no autosave, backups or name;
// one you have built on stays in the list like any other world. Nothing is deleted, so without this mod the game
// shows them all again.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class CreateWorld : IModule
    {
        public string Name { get { return "CreateWorld"; } }
        static readonly Type M = typeof(MenuState);
        static readonly string[] Templates = { "World0", "World1", "World2", "World3", "World4", "World5" };
        // layout, in the wrist panel's own units (1400 wide; the middle of a page is the middle of the panel)
        const float BackY = 320f, BottomY = -330f;
        const float TileWidth = 400f, TileHeight = 225f, TileGap = 30f, TilesTopY = 130f; const int TilesPerPage = 6;

        static MenuState state; static WristMenu menu; static GameObject selection;
        static GameObject choices, templates, workshop, imports, sender;

        public void Start(Plugin plugin)
        {
            Folders.MakeAll();
            var h = plugin.Harmony; var me = typeof(CreateWorld);
            h.Patch(AccessTools.Method(M, "LoadWorlds"), null, new HarmonyMethod(me, "Listed"));
            h.Patch(AccessTools.Method(M, "UpdateButtons"), null, new HarmonyMethod(me, "Shown"));
            // the game no longer copies every subscribed Workshop world into the Worlds folder at start-up
            h.Patch(AccessTools.Method(typeof(InitState), "CopyWorkshopWorlds"), new HarmonyMethod(me, "NoCopies"));
        }
        public void SceneLoaded(string scene) { choices = null; }
        public void Tick() { }

        static string TemplateFolder { get { return Path.Combine(BepInEx.Paths.GameRootPath, "DefaultWorlds"); } }

        static bool NoCopies(ref IEnumerator __result) { __result = Nothing(); return false; }
        static IEnumerator Nothing() { yield break; }

        // ---- the ready-made worlds and the game's copies of Workshop worlds stay out of the list while untouched
        static void Listed(MenuState __instance)
        {
            try
            {
                var worlds = WorldSelect.Worlds(__instance);
                for (int i = worlds.Count - 1; i >= 0 && worlds.Count > 1; i--)       // (the game expects at least one world in the list)
                {
                    object world = worlds[i];
                    string name = WorldSelect.FolderName(world), directory = WorldSelect.Folder(world);
                    string original = Array.IndexOf(Templates, name) >= 0 ? Path.Combine(TemplateFolder, name)
                                    : name.StartsWith(WorldCopies.WorkshopPrefix) ? Path.Combine(Path.Combine(FileUtils.GetWorkshopSubscribedDirectory(), name), "data") : null;
                    if (original == null || !WorldCopies.Untouched(directory, original, name.StartsWith(WorldCopies.WorkshopPrefix))) continue;
                    UnityEngine.Object.Destroy(WorldSelect.Picture(world));
                    worlds.RemoveAt(i);
                }
                WorldCopies.Remember();         // (what was found out about them is written down once, not once for each world)
                // (the game pointed its own preview picture at the first world before any were taken out)
                if (worlds.Count > 0 && __instance.worldSelectPreviewImage != null)
                    __instance.worldSelectPreviewImage.texture = WorldSelect.Picture(worlds[0]);
            }
            catch (Exception e) { Plugin.Log.LogWarning("CreateWorld: " + e.Message); }
        }

        /// the Workshop title of a subscribed world, from what was saved with its download ("" if there is none)
        public static string WorkshopTitle(string folderName)
        {
            try
            {
                string meta = Path.Combine(Path.Combine(FileUtils.GetWorkshopSubscribedDirectory(), folderName), "metadata.json");
                if (!File.Exists(meta)) return "";
                var info = JsonUtility.FromJson<SteamWorkshop.WorkshopItemInfo>(File.ReadAllText(meta));
                return info != null && info.title != null ? info.title.Trim() : "";
            }
            catch (Exception) { return ""; }
        }

        // ---- the button and its pages
        static void Shown(MenuState __instance)
        {
            try { state = __instance; if (choices == null) Build(); }
            catch (Exception e) { Plugin.Log.LogWarning("CreateWorld: " + e); }
        }
        static void Sound() { WorldSelect.Click(state); }
        static void Page(GameObject page)
        {
            Sound();
            selection.SetActive(page == null);
            choices.SetActive(page == choices); templates.SetActive(page == templates); workshop.SetActive(page == workshop); imports.SetActive(page == imports);
            // the pictures of the templates and of the Workshop worlds are only held while their page is the one shown
            if (page != templates) LetGo(templatePictures);
            else for (int i = 0; i < templatePictures.Count; i++) { if (templatePictures[i].texture == null) templatePictures[i].texture = WorldSelect.LoadPicture(state, templateFiles[i]); }
            if (page != workshop) LetGo(pictures);
            if (page == workshop) { FindItems(); itemPager.Refresh(); }
            if (page == imports) { FindZips(); zipPager.Refresh(); }
        }

        static readonly List<RawImage> templatePictures = new List<RawImage>(); static readonly List<string> templateFiles = new List<string>();
        static void LetGo(List<RawImage> shown)
        {
            foreach (RawImage picture in shown) if (picture != null && picture.texture != null) { UnityEngine.Object.Destroy(picture.texture); picture.texture = null; }
        }

        static void Blank()
        {
            choices.SetActive(false); selection.SetActive(true);
            state.OnNewWorld(null);
        }

        // a new world that starts as a copy of the given save and picture, opened straight away
        static void NewFrom(string data, string picture, string title)
        {
            if (!File.Exists(data) || !File.Exists(picture)) { Popup.Show("That world is no longer there", 3f); return; }
            // (WorldSelect.MakeWorld: nothing is left behind if the copying fails part-way)
            string directory = WorldSelect.MakeWorld(delegate (string folder)
            {
                File.Copy(data, Path.Combine(folder, "data"));
                File.Copy(picture, Path.Combine(folder, "preview.jpg"));
                if (!string.IsNullOrEmpty(title)) WorldNames.Set(folder, title);
            });
            if (directory == null) return;
            string name = Path.GetFileName(directory);

            WorldSelect.Refresh(state);
            templates.SetActive(false); workshop.SetActive(false); selection.SetActive(true);
            LetGo(templatePictures); LetGo(pictures);
            // the game opens the world whose picture the touched thumbnail shows: a stand-in thumbnail with the new world's picture
            foreach (object world in WorldSelect.Worlds(state))
                if (WorldSelect.FolderName(world) == name)
                {
                    sender.GetComponent<RawImage>().texture = WorldSelect.Picture(world);
                    // (a Workshop world may use Workshop models you have not got: MissingItems.cs asks first)
                    Action open = delegate { WorldPage.LoadNow(state, sender); };
                    if (!MissingItems.Ask(Path.Combine(directory, "data"), open, delegate { selection.SetActive(true); })) open();
                    return;
                }
        }

        // ---- the Workshop page: the subscribed worlds that have been downloaded, six to a page
        class Item { public string Folder, Title; public long Subscribed; }
        static readonly List<Item> items = new List<Item>();
        static readonly List<GameObject> tiles = new List<GameObject>(); static readonly List<RawImage> pictures = new List<RawImage>();
        static MenuPage.Pager itemPager; static Text none;

        static void FindItems()
        {
            items.Clear();
            string folder = FileUtils.GetWorkshopSubscribedDirectory();
            if (!Directory.Exists(folder)) return;
            foreach (string d in Directory.GetDirectories(folder))
            {
                string name = Path.GetFileName(d);
                if (!name.StartsWith(WorldCopies.WorkshopPrefix) || !File.Exists(Path.Combine(d, "data")) || !File.Exists(Path.Combine(d, "preview.jpg"))) continue;
                // when it was subscribed to, as Steam told the start-up check (FastSync.cs); failing that, when it was downloaded
                long when = 0; string note = Path.Combine(d, WorldCopies.SubscribedFile);
                if (!File.Exists(note) || !long.TryParse(File.ReadAllText(note).Trim(), out when)) when = (long)(Directory.GetCreationTimeUtc(d) - new DateTime(1970, 1, 1)).TotalSeconds;
                items.Add(new Item { Folder = d, Title = WorkshopTitle(name), Subscribed = when });
            }
            items.Sort((a, b) => b.Subscribed.CompareTo(a.Subscribed));          // most recently subscribed first
        }

        static void Fill()
        {
            int first = itemPager.First;
            for (int i = 0; i < TilesPerPage; i++)
            {
                bool there = first + i < items.Count;
                if (pictures[i].texture != null) { UnityEngine.Object.Destroy(pictures[i].texture); pictures[i].texture = null; }
                tiles[i].SetActive(there);
                if (!there) continue;
                Item item = items[first + i];
                pictures[i].texture = WorldSelect.LoadPicture(state, Path.Combine(item.Folder, "preview.jpg"));
                WorldNames.Caption(pictures[i], item.Title);
            }
            none.gameObject.SetActive(items.Count == 0);
        }

        static void Pick(int tile)
        {
            if (itemPager.First + tile >= items.Count) return;
            Item item = items[itemPager.First + tile];
            NewFrom(Path.Combine(item.Folder, "data"), Path.Combine(item.Folder, "preview.jpg"), item.Title);
        }

        // ---- the Import page: the zips in the Imported folder, newest first, five to a page
        // (limits on what a zip may unpack to: a world's save is a few megabytes, and a zip can be made that unpacks to
        // thousands of times its own size)
        const int ZipsPerPage = 5; const int LargestFile = 64 << 20; const long LargestWorld = 200L << 20;
        static readonly string[] WorldFiles = { "data", "autosave", "backup_1", "backup_2", "backup_3", "preview.jpg", "name.txt", Thumbnails.FileName };
        static readonly Dictionary<string, DateTime> zipDates = new Dictionary<string, DateTime>();      // (each zip's date asked for once, not at every comparison)
        static readonly List<string> zips = new List<string>(); static readonly List<GameObject> zipRows = new List<GameObject>();
        static MenuPage.Pager zipPager; static Text zipWords;
        const string ZipHelp = "To import a world, put its .zip in the " + Folders.Shown + "Imported folder inside the game's folder.";

        static void FindZips()
        {
            zips.Clear();
            try { zips.AddRange(Directory.GetFiles(Folders.Imported, "*.zip")); } catch (Exception) { }
            zipDates.Clear();
            foreach (string zip in zips) zipDates[zip] = File.GetLastWriteTime(zip);
            zips.Sort((a, b) => zipDates[b].CompareTo(zipDates[a]));
        }

        static void FillZips()
        {
            int first = zipPager.First;
            for (int i = 0; i < ZipsPerPage; i++)
            {
                bool there = first + i < zips.Count;
                zipRows[i].SetActive(there);
                if (there) zipRows[i].GetComponentInChildren<Text>(true).text = Path.GetFileNameWithoutExtension(zips[first + i]) + "   \u2022   " + zipDates[zips[first + i]].ToString("d MMM yyyy, HH:mm");
            }
            zipWords.text = ZipHelp + (zips.Count == 0 ? "\nThere are none there at the moment." : "");
        }

        // unpack a zip as a new world: only the files a world is made of are taken, whatever folder they are in inside the zip
        static void Import(int row)
        {
            if (zipPager.First + row >= zips.Count) return;
            string zip = zips[zipPager.First + row];
            try
            {
                var take = new Dictionary<string, byte[]>();
                // (the zip is only asked for the files a world is made of, the first of each name: nothing else in it is
                // unpacked. The names are only ever compared with that list, never used as a place to write to.)
                var seen = new HashSet<string>();
                Predicate<string> wanted = delegate (string path)
                {
                    string called = Path.GetFileName(path.Replace('\\', '/'));
                    return Array.IndexOf(WorldFiles, called) >= 0 && seen.Add(called);
                };
                foreach (var file in Zip.Read(zip, LargestFile, LargestWorld, wanted))
                    take[Path.GetFileName(file.Key.Replace('\\', '/'))] = file.Value;
                if (!take.ContainsKey("data") && !take.ContainsKey("autosave")) { zipWords.text = "\"" + Path.GetFileName(zip) + "\" has no world in it."; return; }
                // (WorldSelect.MakeWorld: nothing is left behind if the writing fails part-way)
                string directory = WorldSelect.MakeWorld(delegate (string folder)
                {
                    foreach (var file in take) File.WriteAllBytes(Path.Combine(folder, file.Key), file.Value);
                    string preview = Path.Combine(folder, "preview.jpg");
                    if (!File.Exists(preview)) File.Copy(Path.Combine(TemplateFolder, "defaultPreview.jpg"), preview);
                });
                if (directory == null) return;
                Plugin.Log.LogInfo("CreateWorld: imported " + zip + " (" + take.Count + " files) as " + directory);
                AccessTools.Field(M, "pageIndex").SetValue(state, 0);
                WorldSelect.Refresh(state);
                Page(null);
                Popup.Show("World imported", 3f);
            }
            catch (Exception e)
            {
                zipWords.text = "\"" + Path.GetFileName(zip) + "\" could not be imported: " + e.Message + ".";
                Plugin.Log.LogWarning("CreateWorld: import " + zip + ": " + e);
            }
        }

        // ---- building it all
        // a picture in a frame that can be pressed (place `index` of six, three to a line)
        public static Color Frame = new Color(0.87f, 0.87f, 0.85f);     // the colour of the frame round World Select's own thumbnails, once known
        public static RawImage Tile(Transform page, int index, Action pressed, out GameObject tile)
        {
            WristMenu menu = WorldSelect.Menu;
            float x = (index % 3 - 1) * (TileWidth + TileGap), y = TilesTopY - (index / 3) * (TileHeight + TileGap);
            tile = MenuPage.Button(menu, page, "", TileWidth, TileHeight, x, y, pressed);
            tile.GetComponent<Image>().color = Frame;
            var image = new GameObject("Picture", typeof(RectTransform)).AddComponent<RawImage>();
            image.transform.SetParent(tile.transform, false);
            image.rectTransform.anchorMin = Vector2.zero; image.rectTransform.anchorMax = Vector2.one;
            image.rectTransform.offsetMin = new Vector2(8f, 8f); image.rectTransform.offsetMax = new Vector2(-8f, -8f);     // (the button shows as a frame)
            image.raycastTarget = false;
            return image;
        }

        static void Build()
        {
            if (!WorldSelect.Find()) return;
            menu = WorldSelect.Menu; selection = WorldSelect.Selection;

            // the game's "+" (the button wired to the game's "new world") opens the choices instead. It stays in its
            // corner of the header, moved up with the header (PanelSize.cs made the panel taller) if it is not part of it.
            PushButton plus = null;
            foreach (var button in menu.GetComponentsInChildren<PushButton>(true))
                for (int i = 0; button.onButtonPress != null && i < button.onButtonPress.GetPersistentEventCount(); i++)
                    if (button.onButtonPress.GetPersistentMethodName(i) == "OnNewWorld") plus = button;
            if (plus != null)
            {
                Transform header = menu.transform.Find("Background/Header");
                if (header == null || !plus.transform.IsChildOf(header))
                    plus.transform.position += menu.transform.up * (PanelSize.Half * menu.transform.lossyScale.y);
                plus.onButtonPress = new PushButton.OnButtonPressEvent();
                plus.onButtonPress.AddListener(delegate (GameObject pressedBy) { Page(choices); });
            }
            else Plugin.Log.LogWarning("CreateWorld: the game's + button was not found");

            // the three choices
            var c = WorldSelect.NewPage("TinyTownEnhanced Create Choices");
            choices = c.gameObject;
            MenuPage.Paint(MenuPage.Chevron(MenuPage.Button(menu, c, "Back", 200f, 60f, -560f, BackY, delegate { Page(null); })), MenuPage.Role.Quiet);
            // rounded: Blank in the green of the "+", Template in the menu's purple, Workshop in blue
            MenuPage.Paint(MenuPage.Button(menu, c, "Blank", 380f, 260f, -420f, 50f, Blank), MenuPage.Role.Go);
            MenuPage.Paint(MenuPage.Button(menu, c, "Template", 380f, 260f, 0f, 50f, delegate { Page(templates); }), MenuPage.Role.Choice);
            MenuPage.Paint(MenuPage.Button(menu, c, "Workshop", 380f, 260f, 420f, 50f, delegate { Page(workshop); }), MenuPage.Role.Action);
            // Import: a plain button in the middle, underneath
            MenuPage.Paint(MenuPage.Button(menu, c, "Import", 380f, 80f, 0f, -200f, delegate { Page(imports); }), MenuPage.Role.Quiet, false);
            choices.SetActive(false);

            // the frame round each picture: the colour of the frame round World Select's own thumbnails
            var thumbs = (RawImage[])WorldSelect.FirstPage.GetValue(state);
            if (thumbs.Length > 0)
            {
                var around = thumbs[0].transform.parent.GetComponent<Image>();
                if (around == null) around = thumbs[0].transform.parent.parent.GetComponent<Image>();
                if (around != null) Frame = around.color;
            }

            // the templates: the game's ready-made worlds (their pictures are read when the page is shown: see Page)
            var t = WorldSelect.NewPage("TinyTownEnhanced Create Templates");
            templates = t.gameObject;
            MenuPage.Paint(MenuPage.Chevron(MenuPage.Button(menu, t, "Back", 200f, 60f, -560f, BackY, delegate { Page(choices); })), MenuPage.Role.Quiet);
            int shown = 0; templatePictures.Clear(); templateFiles.Clear();
            foreach (string template in Templates)
            {
                string picture = Path.Combine(TemplateFolder, template + ".jpg"), data = Path.Combine(TemplateFolder, template);
                if (!File.Exists(picture) || !File.Exists(data)) continue;
                GameObject tile;
                templatePictures.Add(Tile(t, shown++, delegate { NewFrom(data, picture, ""); }, out tile));
                templateFiles.Add(picture);
            }
            templates.SetActive(false);

            // the Workshop worlds: six places, filled a page at a time, with the page arrows underneath
            var w = WorldSelect.NewPage("TinyTownEnhanced Create Workshop");
            workshop = w.gameObject;
            MenuPage.Paint(MenuPage.Chevron(MenuPage.Button(menu, w, "Back", 200f, 60f, -560f, BackY, delegate { Page(choices); })), MenuPage.Role.Quiet);
            tiles.Clear(); pictures.Clear();
            for (int i = 0; i < TilesPerPage; i++)
            {
                int place = i; GameObject tile;
                pictures.Add(Tile(w, i, delegate { Pick(place); }, out tile));
                tiles.Add(tile);
            }
            itemPager = new MenuPage.Pager(menu, w, BottomY, TilesPerPage, () => items.Count, Fill, Sound);
            none = MenuPage.Label(w, 1000f, 160f, 0f, 0f, 44);
            none.text = "No Workshop worlds yet.\nSubscribe to a world on the Steam Workshop and it will appear here.";
            workshop.SetActive(false);

            // the Import page: what to do, a row for each zip, and the page arrows
            var z = WorldSelect.NewPage("TinyTownEnhanced Create Import");
            imports = z.gameObject;
            MenuPage.Paint(MenuPage.Chevron(MenuPage.Button(menu, z, "Back", 200f, 60f, -560f, BackY, delegate { Page(choices); })), MenuPage.Role.Quiet);
            zipWords = MenuPage.Label(z, 1000f, 160f, 0f, 235f, 30);
            zipRows.Clear();
            for (int i = 0; i < ZipsPerPage; i++)
            {
                int row = i;
                zipRows.Add(MenuPage.Paint(MenuPage.Button(menu, z, "Zip", 1000f, 70f, 0f, 130f - i * 84f, delegate { Import(row); }), MenuPage.Role.Choice));
            }
            zipPager = new MenuPage.Pager(menu, z, BottomY, ZipsPerPage, () => zips.Count, FillZips, Sound);
            imports.SetActive(false);

            sender = new GameObject("TinyTownEnhanced New World", typeof(RectTransform), typeof(RawImage));
            sender.transform.SetParent(t, false);
            sender.GetComponent<RawImage>().enabled = false;
        }
    }
}
