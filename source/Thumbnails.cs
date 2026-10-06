// Tiny Town Enhanced - a world's thumbnail can be one of your photos.
// "Thumbnail" on a world's page (WorldPage.cs) shows the photos taken with the in-game camera, newest first; the one
// you touch becomes the world's picture. The game makes a new picture of its own every time a world is saved, so
// the chosen one is also kept in the world's folder as "thumbnail.jpg" and put back after each save (Saves.cs).
// "Automatic" goes back to the game's own picture (from the next save on).
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public static class Thumbnails
    {
        public const string FileName = "thumbnail.jpg";
        const int PerPage = 6; const float BackY = 320f, BottomY = -330f;
        static GameObject page; static MenuPage.Pager pager; static Text none;
        static readonly List<string> photos = new List<string>(); static readonly List<GameObject> tiles = new List<GameObject>(); static readonly List<RawImage> pictures = new List<RawImage>();
        static MenuState state; static string directory; static Action closed;

        /// the game has just saved the world in this folder (and made its own picture): the chosen one goes back
        public static void Keep(string worldDirectory)
        {
            try
            {
                string chosen = Path.Combine(worldDirectory, FileName);
                if (File.Exists(chosen)) File.Copy(chosen, Path.Combine(worldDirectory, "preview.jpg"), true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Thumbnails: " + e.Message); }
        }

        /// show the photos in place of World Select's other pages; `whenClosed` runs if Back is touched
        public static void Open(MenuState menuState, string worldDirectory, Action whenClosed)
        {
            if (!WorldSelect.Find()) return;
            state = menuState; directory = worldDirectory; closed = whenClosed;
            if (page == null) Build();
            // each photo has a small copy beside it ("preview_..."), which is what is shown and used
            photos.Clear();
            try { photos.AddRange(Directory.GetFiles(FileUtils.GetPhotoLibraryDirectory(), "preview_*.jpg")); } catch (Exception) { }
            var taken = new Dictionary<string, DateTime>();         // (each file's date asked for once, not at every comparison)
            foreach (string photo in photos) taken[photo] = File.GetLastWriteTime(photo);
            photos.Sort((a, b) => taken[b].CompareTo(taken[a]));
            page.SetActive(true);
            pager.Refresh();
        }

        static void Sound() { WorldSelect.Click(state); }

        static void Fill()
        {
            for (int i = 0; i < PerPage; i++)
            {
                bool there = pager.First + i < photos.Count;
                if (pictures[i].texture != null) { UnityEngine.Object.Destroy(pictures[i].texture); pictures[i].texture = null; }
                tiles[i].SetActive(there);
                if (there) pictures[i].texture = WorldSelect.LoadPicture(state, photos[pager.First + i]);
            }
            none.gameObject.SetActive(photos.Count == 0);
        }

        // the page is left: the pictures it was showing are let go of
        static void Close()
        {
            foreach (var picture in pictures) if (picture.texture != null) { UnityEngine.Object.Destroy(picture.texture); picture.texture = null; }
            page.SetActive(false);
        }

        // back to World Select, with the list read again so that the new picture shows
        static void Done()
        {
            Close();
            WorldSelect.Selection.SetActive(true);
            WorldSelect.Refresh(state);
        }

        static void Pick(int tile)
        {
            if (pager.First + tile >= photos.Count) return;
            try
            {
                Sound();
                File.Copy(photos[pager.First + tile], Path.Combine(directory, FileName), true);
                Keep(directory);
                Done();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Thumbnails: " + e.Message); }
        }

        static void Automatic()
        {
            try
            {
                Sound();
                string chosen = Path.Combine(directory, FileName);
                if (File.Exists(chosen)) File.Delete(chosen);
                Done();
                Popup.Show("The game's own picture will be back\nthe next time this world is saved", 4f);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Thumbnails: " + e.Message); }
        }

        static void Build()
        {
            WristMenu menu = WorldSelect.Menu;
            var rt = WorldSelect.NewPage("TinyTownEnhanced Thumbnails");
            page = rt.gameObject;
            MenuPage.Paint(MenuPage.Chevron(MenuPage.Button(menu, rt, "Back", 200f, 60f, -560f, BackY, delegate { Sound(); Close(); if (closed != null) closed(); })), MenuPage.Role.Quiet);
            tiles.Clear(); pictures.Clear();
            for (int i = 0; i < PerPage; i++)
            {
                int place = i; GameObject tile;
                pictures.Add(CreateWorld.Tile(rt, i, delegate { Pick(place); }, out tile));
                tiles.Add(tile);
            }
            pager = new MenuPage.Pager(menu, rt, BottomY, PerPage, () => photos.Count, Fill, Sound);
            MenuPage.Paint(MenuPage.Button(menu, rt, "Automatic", 260f, 64f, -530f, BottomY, Automatic), MenuPage.Role.Quiet);
            none = MenuPage.Label(rt, 1000f, 160f, 0f, 0f, 44);
            none.text = "No photos yet.\nTake one with the camera while in a world.";
            page.SetActive(false);
        }
    }
}
