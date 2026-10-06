// Tiny Town Enhanced - touching a world in World Select opens a page for that world instead of loading it at once.
// The page shows the world's picture and what can be opened, each with the date and time its file was written:
//   Save        the manual save ("data": written by Save and Quit)
//   Autosave    the last autosave, when there is one (see Saves.cs)
//   Backup 1-3  the three manual saves before the current one (the game keeps these itself, but never offered them)
// Duplicate (a copy of the world, with its autosave and backups, as a new world),
// Export (the world's folder as a .zip in "TTVREnhanced/CustomWorlds/Exported" in the game's folder, for keeping or moving it: unzipped
// into the game's Worlds folder it is a world again),
// Rename (WorldNames.cs: the name shows on the picture and on the thumbnail) and Thumbnail (Thumbnails.cs: one of
// your photos as the world's picture), with a line under them saying when the
// world was last played and how many objects it has,
// and Back, and the bin: deleting a world is now done from here (the bins on the thumbnails are gone), through the
// game's own "are you sure" page.
// Opening anything but Save does not change the save: the world is simply loaded from the other file, and only
// "Save and Quit" then writes a new save (the old one becoming Backup 1).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class WorldPage : IModule
    {
        public string Name { get { return "WorldPage"; } }
        static readonly Type M = typeof(MenuState), W = typeof(WristWorldSelectPage);
        static readonly FieldInfo fConfirmation = AccessTools.Field(W, "confirmationPage"),
                                  fVisible = AccessTools.Field(W, "selectionPageVisible"), fToDelete = AccessTools.Field(W, "worldToDelete");
        // (what is not part of a world to copy or export: the game's copy of the open world, and when it was last played)
        static readonly string[] NotCopied = { "original", WorldSort.PlayedFile };
        static readonly string[] Files = { Saves.Manual, Saves.Autosave, "backup_1", "backup_2", "backup_3" }, Names = { "Save", "Autosave", "Backup 1", "Backup 2", "Backup 3" };

        static bool passing;                                        // our own call of the game's "world chosen": let it through
        static GameObject page; static RawImage picture; static Text facts; static readonly List<GameObject> loads = new List<GameObject>();
        static MenuState state; static GameObject chosen; static int index; static string directory;

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(WorldPage);
            h.Patch(AccessTools.Method(M, "OnSelectWorld"), new HarmonyMethod(me, "Chosen"));
            h.Patch(AccessTools.Method(M, "UpdateButtons"), null, new HarmonyMethod(me, "NoBins"));
        }
        public void SceneLoaded(string scene) { page = null; }
        public void Tick() { }

        // whenever the game lays out the thumbnails: their bins stay hidden (deleting is done from the world's page)
        static void NoBins(MenuState __instance)
        {
            try { foreach (Transform bin in Bins(__instance)) bin.gameObject.SetActive(false); } catch (Exception) { }
        }
        static List<Transform> Bins(MenuState menuState)
        {
            var found = new List<Transform>();
            foreach (RawImage thumb in WorldSelect.Thumbs(menuState))
                foreach (Transform sibling in thumb.transform.parent)
                    if (sibling != thumb.transform && sibling.GetComponent<PushButton>() != null) found.Add(sibling);
            return found;
        }

        // the typing page takes this page's place, and gives it back when it closes
        static void Rename()
        {
            Sound();
            if (!WorldNames.Edit(state, directory, delegate
                {
                    page.SetActive(true);
                    WorldNames.Caption(picture, WorldNames.Get(directory));
                    WorldSelect.Redraw(state);       // the thumbnails show the new name
                })) return;
            page.SetActive(false);
        }

        // the photos take this page's place (Thumbnails.cs); Back there gives it back
        static void Thumbnail()
        {
            Sound();
            page.SetActive(false);
            Thumbnails.Open(state, directory, delegate { page.SetActive(true); });
        }

        // a world's thumbnail was touched: show its page instead of loading it
        static bool Chosen(MenuState __instance, GameObject sender)
        {
            if (passing) return true;
            try
            {
                state = __instance; chosen = sender;
                Texture texture = sender.GetComponent<RawImage>().texture;
                var worlds = WorldSelect.Worlds(state);
                index = -1;
                for (int i = 0; i < worlds.Count; i++) if (WorldSelect.Picture(worlds[i]) == texture) index = i;
                if (index < 0) return true;
                directory = WorldSelect.Folder(worlds[index]);
                if (page == null && !Build()) return true;

                picture.texture = texture;
                WorldNames.Caption(picture, WorldNames.Get(directory));
                // when it was last opened, and how big it is (of the save, or the autosave of a world never saved)
                string newest = File.Exists(Path.Combine(directory, Saves.Manual)) ? Saves.Manual : Saves.Autosave;
                int objects = WorldCopies.ObjectCount(Path.Combine(directory, newest));
                facts.text = "Last played " + WorldSort.Played(directory).ToString("d MMM yyyy, HH:mm") + (objects >= 0 ? "   \u2022   " + objects.ToString("N0") + " objects" : "");
                // one button for each file that is there, newest kind first; the autosave only if it is newer than the save
                DateTime saved = Written(Saves.Manual);
                int shown = 0;
                for (int i = 0; i < Files.Length; i++)
                {
                    DateTime when = Written(Files[i]);
                    bool there = when != DateTime.MinValue && !(Files[i] == Saves.Autosave && saved != DateTime.MinValue && when <= saved);
                    loads[i].SetActive(there);
                    if (!there) continue;
                    loads[i].GetComponentInChildren<Text>(true).text = "Load " + Names[i] + "   \u2022   " + when.ToString("d MMM yyyy, HH:mm");
                    var rt = (RectTransform)loads[i].transform;
                    rt.localPosition = new Vector3(rt.localPosition.x, Top - shown * Step, 0f);
                    shown++;
                }
                WorldSelect.Selection.SetActive(false);
                page.SetActive(true);
                Sound();
                return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldPage: " + e); return true; }
        }

        static DateTime Written(string file) { string path = Path.Combine(directory, file); return File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue; }
        static void Sound() { WorldSelect.Click(state); }

        static void Back()
        {
            page.SetActive(false);
            WorldSelect.Selection.SetActive((bool)fVisible.GetValue(WorldSelect.Page) && WorldSelect.Page.gameObject.activeInHierarchy);
        }

        // open the world from one of its files: tell the game which file, then let its own "world chosen" run
        static void Load(string file)
        {
            // (if it uses Workshop models you have not got, MissingItems.cs asks first)
            page.SetActive(false);
            if (!MissingItems.Ask(Path.Combine(directory, file), delegate { Open(file); }, delegate { page.SetActive(true); })) Open(file);
        }
        static void Open(string file)
        {
            var worlds = WorldSelect.Worlds(state);
            object world = worlds[index];                           // (a boxed copy: changed, then put back)
            AccessTools.Field(world.GetType(), "filename").SetValue(world, Path.Combine(directory, file).Replace('\\', '/'));
            worlds[index] = world;
            page.SetActive(false);
            WorldSelect.Selection.SetActive(true);
            passing = true;
            try { state.OnSelectWorld(chosen); } finally { passing = false; }
        }

        // a copy of the world's folder under a new name (worlds are named by date and time, as the game names new ones);
        // WorldSelect.MakeWorld sees to it that a copy that fails part-way leaves nothing behind
        static void Duplicate()
        {
            string name = WorldNames.Get(directory);
            string copy = WorldSelect.MakeWorld(delegate (string folder)
            {
                foreach (string file in Directory.GetFiles(directory))
                    if (Array.IndexOf(NotCopied, Path.GetFileName(file)) < 0) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                if (name.Length > 0) WorldNames.Set(folder, name + " copy");
            });
            if (copy == null) return;
            Sound();
            Back();
            WorldSelect.Refresh(state);
        }

        // the world's folder as a zip in the Exported folder (Helpers.cs: Folders)
        static void Export()
        {
            try { Zipped(); }
            catch (Exception e)
            {   // (a full disk, or the zip open in another program: Zip.cs leaves no half-written zip behind)
                Plugin.Log.LogWarning("WorldPage: export of " + directory + ": " + e.Message);
                Popup.Show("Could not export", 4f);
            }
        }
        static void Zipped()
        {
            string folder = Folders.Exported, name = Path.GetFileName(directory.TrimEnd('/', '\\'));
            var files = new List<string>(); var names = new List<string>();
            foreach (string file in Directory.GetFiles(directory))
                if (Array.IndexOf(NotCopied, Path.GetFileName(file)) < 0) { files.Add(file); names.Add(name + "/" + Path.GetFileName(file)); }
            // the zip is called "<the world's name>_<its folder's name>" (just the folder's name if the world has no
            // name): the folder's name is what makes it one of a kind
            string called = WorldNames.Get(directory);
            foreach (char c in Path.GetInvalidFileNameChars()) called = called.Replace(c, ' ');
            called = called.Trim().TrimEnd('.');
            string zip = Path.Combine(folder, (called.Length > 0 ? called + "_" + name : name) + ".zip");
            Zip.Write(zip, files, names);
            Plugin.Log.LogInfo("WorldPage: exported " + zip + " (" + new FileInfo(zip).Length / 1024 + " KB)");
            Sound();
            Popup.Show("Exported to\n" + Folders.Shown + "Exported", 5f);
        }

        /// open a world at once, without its page (`sender` shows the world's picture, as a touched thumbnail would)
        public static void LoadNow(MenuState menuState, GameObject sender)
        {
            passing = true;
            try { menuState.OnSelectWorld(sender); } finally { passing = false; }
        }

        // the game's own "are you sure" page, for this world
        static void Delete()
        {
            fToDelete.SetValue(WorldSelect.Page, directory);
            fVisible.SetValue(WorldSelect.Page, false);
            page.SetActive(false);
            ((GameObject)fConfirmation.GetValue(WorldSelect.Page)).SetActive(true);
            Sound();
        }

        // layout, in the wrist panel's own units (the panel is 1400 wide; the page's middle is the panel's middle)
        const float Top = 180f + PanelSize.Half, Step = 96f;        // the first Load button, and the spacing between them
        const float BinScale = 1.5f;                                // the bin, compared with the size it was on the thumbnails
        const float BackY = 320f;                                   // Back: small, in the top left corner above the picture
        const float ButtonWidth = 660f, ButtonHeight = 80f, ButtonX = 310f;
        const float PictureWidth = 600f, PictureHeight = 338f, PictureX = -360f;
        const float BottomY = -330f;                                // Duplicate and Export (left) and the bin (right corner), along the bottom

        static bool Build()
        {
            if (!WorldSelect.Find()) return false;
            WristMenu menu = WorldSelect.Menu;
            var rt = WorldSelect.NewPage("TinyTownEnhanced World Page");
            page = rt.gameObject;

            // the world's picture on the left
            var pic = new GameObject("Picture", typeof(RectTransform), typeof(RawImage));
            var prt = (RectTransform)pic.transform;
            prt.SetParent(rt, false);
            prt.sizeDelta = new Vector2(PictureWidth, PictureHeight);
            prt.localPosition = new Vector3(PictureX, Top + ButtonHeight * 0.5f - PictureHeight * 0.5f, 0f);      // its top level with the first button's
            picture = pic.GetComponent<RawImage>();
            picture.raycastTarget = false;

            // Rename and Thumbnail, under the picture
            float underPicture = Top + ButtonHeight * 0.5f - PictureHeight - 16f - ButtonHeight * 0.5f;
            MenuPage.Paint(MenuPage.Button(menu, rt, "Rename", 290f, ButtonHeight, PictureX - 155f, underPicture, Rename), MenuPage.Role.Action);
            MenuPage.Paint(MenuPage.Button(menu, rt, "Thumbnail", 290f, ButtonHeight, PictureX + 155f, underPicture, Thumbnail), MenuPage.Role.Action);

            // under that, a line of facts about the world
            facts = new GameObject("Facts", typeof(RectTransform)).AddComponent<Text>();
            facts.transform.SetParent(rt, false);
            facts.rectTransform.sizeDelta = new Vector2(PictureWidth, 44f);
            facts.rectTransform.localPosition = new Vector3(PictureX, Top + ButtonHeight * 0.5f - PictureHeight - 16f - ButtonHeight - 34f, 0f);
            facts.font = menu.title.font; facts.fontSize = 28; facts.color = MenuPage.Ink; facts.alignment = TextAnchor.MiddleCenter; facts.raycastTarget = false;
            facts.resizeTextForBestFit = true; facts.resizeTextMinSize = 14; facts.resizeTextMaxSize = 28;

            // what can be opened, down the right (the words and places are set each time the page is shown)
            loads.Clear();
            foreach (string each in Files)
            {
                string file = each;
                // (the save is the main thing to open; the autosave is the other current one; backups are older choices)
                var role = file == Saves.Manual ? MenuPage.Role.Go : file == Saves.Autosave ? MenuPage.Role.Action : MenuPage.Role.Choice;
                loads.Add(MenuPage.Paint(MenuPage.Button(menu, rt, "Load", ButtonWidth, ButtonHeight, ButtonX, Top, delegate { Load(file); }), role));
            }

            // Back, in the top left corner; Duplicate and Export along the bottom
            MenuPage.Paint(MenuPage.Chevron(MenuPage.Button(menu, rt, "Back", 200f, 60f, -560f, BackY, delegate { Sound(); Back(); })), MenuPage.Role.Quiet);
            MenuPage.Paint(MenuPage.Button(menu, rt, "Duplicate", 290f, ButtonHeight, PictureX - 155f, BottomY, Duplicate), MenuPage.Role.Action);
            MenuPage.Paint(MenuPage.Button(menu, rt, "Export", 290f, ButtonHeight, PictureX + 155f, BottomY, Export), MenuPage.Role.Action);

            // the bin, in the bottom right corner (a copy of the one the thumbnails had; those are hidden)
            GameObject bin = null;
            foreach (Transform sibling in Bins(state))
                if (bin == null) bin = (GameObject)UnityEngine.Object.Instantiate(sibling.gameObject, rt, false);
            if (bin != null)
            {
                bin.name = "Delete World";
                bin.SetActive(true);
                var brt = (RectTransform)bin.transform;
                brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0.5f, 0.5f);
                brt.localPosition = new Vector3(615f, BottomY, 0f); brt.localRotation = Quaternion.identity;
                brt.localScale = brt.localScale * BinScale;
                MenuPage.OnPress(bin, "WorldPage", Delete);
            }
            else
            {   // (no bin found to copy: a plain red button instead)
                var delete = MenuPage.Button(menu, rt, "Delete World", 300f, ButtonHeight, 490f, BottomY, Delete);
                MenuPage.Paint(delete, MenuPage.Role.Danger);
            }
            page.SetActive(false);
            return true;
        }
    }
}
