// Tiny Town Enhanced - shared by everything this mod adds to World Select (WorldPage, WorldNames, CreateWorld,
// MissingItems, WorldList): where the game's World Select page is, how to make a page of our own beside it, how to
// read the game's list of worlds, and how to make a new world's folder safely.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class WorldSelect : IModule
    {
        public string Name { get { return "WorldSelect"; } }
        public static WristWorldSelectPage Page;        // the game's World Select tab
        public static WristMenu Menu;                   // the wrist menu it is on
        public static GameObject Selection;             // the part of it with the thumbnails (the other part is "delete this world?")
        static readonly List<GameObject> pages = new List<GameObject>();       // the pages made with NewPage

        public void Start(Plugin plugin)
        {
            // another tab is opened, or the menu put away: every page of ours goes too (the game shows its own again when the tab comes back)
            plugin.Harmony.Patch(AccessTools.Method(typeof(WristWorldSelectPage), "Deactivate"), null, new HarmonyMethod(typeof(WorldSelect), "Closed"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Closed() { foreach (var page in pages) if (page != null) page.SetActive(false); }

        /// find them (again, if the menu has been rebuilt); false if they are not there
        public static bool Find()
        {
            if (Page != null && Menu != null && Selection != null) return true;
            Page = UnityEngine.Object.FindObjectOfType<WristWorldSelectPage>();
            if (Page == null) foreach (var s in Resources.FindObjectsOfTypeAll<WristWorldSelectPage>()) if (s.gameObject.scene.IsValid()) Page = s;
            Menu = Page != null ? Page.GetComponentInParent<WristMenu>() : null;
            Selection = Page != null ? (GameObject)AccessTools.Field(typeof(WristWorldSelectPage), "selectionPage").GetValue(Page) : null;
            return Menu != null && Selection != null;
        }

        /// A page of our own in the same place as the thumbnails, to show instead of them; it is put away by itself
        /// when World Select is left. Whatever the World Select page's own scale, this one is in the wrist panel's
        /// units: the panel is 1400 wide, and the page's middle is the panel's middle.
        public static RectTransform NewPage(string name)
        {
            var rt = Place(name, Selection.transform.parent, Selection.transform.localPosition);
            pages.RemoveAll(page => page == null);
            pages.Add(rt.gameObject);
            return rt;
        }

        /// a place on the World Select page itself, measured in panel units like NewPage's: what is put there comes and
        /// goes with the thumbnails
        public static RectTransform OnSelection(string name) { return Place(name, Selection.transform, Vector3.zero); }

        static RectTransform Place(string name, Transform parent, Vector3 at)
        {
            var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.localPosition = at; rt.localRotation = Quaternion.identity;
            float panel = Menu.transform.lossyScale.x, around = parent.lossyScale.x;
            rt.localScale = Vector3.one * (around > 0f ? panel / around : 1f);
            return rt;
        }

        /// the menu's click, as the game's own buttons make it
        public static void Click(MenuState state) { try { state.soundManager.Play(SoundManager.SoundEffect.BUTTON_CLICK_POSITIVE); } catch (Exception) { } }

        /// the folder for a new world: named by the date and time, as the game names the worlds it makes
        public static string NewWorldFolder() { return FileUtils.GetSaveDirectory() + "/" + string.Format("{0:d_M_yyyy_HH_mm_ss}", DateTime.Now); }

        /// Make a new world: `fill` is given an empty folder to put the world's files in, and the folder then becomes
        /// a world (named by the date and time). Gives back the world's folder, or null if it could not be made (a
        /// short message has then been shown).
        /// The files are first put in a folder beside the game's Worlds folder and only moved in when all are there:
        /// the game stops listing worlds altogether if it meets a world folder without a "preview.jpg", which is what
        /// a copy that failed half-way (a full disk, a file in use) would leave behind.
        public static string MakeWorld(Action<string> fill)
        {
            string directory = NewWorldFolder();
            if (Directory.Exists(directory)) { Popup.Show("Try again in a second", 3f); return null; }
            string making = Path.Combine(Path.GetDirectoryName(FileUtils.GetSaveDirectory()), "TTVREnhanced new world");
            try
            {
                if (Directory.Exists(making)) Directory.Delete(making, true);       // (left by a game that was closed part-way)
                Directory.CreateDirectory(making);
                fill(making);
                if (!File.Exists(Path.Combine(making, "preview.jpg"))) throw new Exception("the world has no picture");
                Directory.Move(making, directory);
                return directory;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("WorldSelect: a new world could not be made: " + e.Message);
                try { if (Directory.Exists(making)) Directory.Delete(making, true); } catch (Exception) { }
                Popup.Show("Could not make the world", 3f);
                return null;
            }
        }

        // ---- the game's list of worlds (private to the game, and each entry a small record of a private kind).
        // The fields are looked up once: they are read for every world each time the list is sorted.
        static readonly Type M = typeof(MenuState), World = AccessTools.Inner(typeof(MenuState), "World");
        static readonly FieldInfo fWorlds = AccessTools.Field(M, "worlds"), fName = AccessTools.Field(World, "name"),
                                  fDirectory = AccessTools.Field(World, "directory"), fPreview = AccessTools.Field(World, "preview");
        static readonly MethodInfo loadImage = AccessTools.Method(M, "LoadImage"), updateButtons = AccessTools.Method(M, "UpdateButtons"),
                                   updateArrows = AccessTools.Method(M, "UpdateNavigationButtons");
        /// the game's thumbnails: those of the first page of World Select, and those of every other page
        public static readonly FieldInfo FirstPage = AccessTools.Field(M, "firstPageButtons"), NextPage = AccessTools.Field(M, "nextPageButtons");

        public static IList Worlds(MenuState state) { return (IList)fWorlds.GetValue(state); }
        public static string FolderName(object world) { return (string)fName.GetValue(world); }
        public static string Folder(object world) { return (string)fDirectory.GetValue(world); }
        public static Texture2D Picture(object world) { return (Texture2D)fPreview.GetValue(world); }
        /// every thumbnail of World Select, first page and other pages together
        public static List<RawImage> Thumbs(MenuState state)
        {
            var all = new List<RawImage>((RawImage[])FirstPage.GetValue(state));
            all.AddRange((RawImage[])NextPage.GetValue(state));
            return all;
        }

        /// a picture file as a texture, read by the game's own routine
        public static Texture LoadPicture(MenuState state, string file) { return (Texture)loadImage.Invoke(state, new object[] { file }); }

        /// the game reads the worlds from disk again and lays World Select out afresh (thumbnails and page arrows)
        public static void Refresh(MenuState state) { state.ForceReloadWorldUI(null); }
        /// only the thumbnails are laid out again (the list itself has not changed)
        public static void Redraw(MenuState state) { updateButtons.Invoke(state, null); }
        /// the thumbnails and the page arrows are laid out again (the list is in a new order, but was not read again)
        public static void Relay(MenuState state) { updateButtons.Invoke(state, null); updateArrows.Invoke(state, null); }
    }
}
