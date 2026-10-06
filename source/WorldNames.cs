// Tiny Town Enhanced - worlds can be given a name.
// The game only knows a world by its folder, which is named after the date it was made. A name typed here is kept
// in "name.txt" in the world's folder (so it goes along when the world is duplicated or exported), and is shown
// along the bottom of the world's thumbnail in World Select and of its picture on the world's page.
// A name too long for the thumbnail is first drawn smaller, and past that is cut short with "...".
// Typing is done on a copy of the keyboard from the wrist menu's speech bubble page (WorldPage.cs has the button).
using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class WorldNames : IModule
    {
        public string Name { get { return "WorldNames"; } }
        const string FileName = "name.txt";
        const int Longest = 40;                                     // characters in a name
        // the caption is laid out as if the picture were 400 wide, and scaled to whatever width it really is
        const float CaptionWidth = 400f, CaptionHeight = 50f, Padding = 12f; const int LargestFont = 32, SmallestFont = 22;

        public void Start(Plugin plugin)
        {
            plugin.Harmony.Patch(AccessTools.Method(typeof(MenuState), "UpdateButtons"), null, new HarmonyMethod(typeof(WorldNames), "Thumbnails"));
            plugin.Harmony.Patch(AccessTools.Method(typeof(MenuState), "LoadWorlds"), new HarmonyMethod(typeof(WorldNames), "Forget"));
        }
        public void SceneLoaded(string scene) { namePage = null; }
        public void Tick() { }

        // ---- the names
        // (names are asked for every time the thumbnails are laid out: each is read from disk once, until it is changed
        // or the game reads its list of worlds again)
        static readonly Dictionary<string, string> known = new Dictionary<string, string>();
        static void Forget() { known.Clear(); }

        public static string Get(string directory)
        {
            string name;
            if (!known.TryGetValue(directory, out name)) known[directory] = name = Read(directory);
            return name;
        }

        static string Read(string directory)
        {
            try
            {
                string file = Path.Combine(directory, FileName);
                if (File.Exists(file)) return FirstLine(file);
                // one of the game's own copies of a Workshop world, not named since: its Workshop title
                string folder = Path.GetFileName(directory.TrimEnd('/', '\\'));
                return folder.StartsWith(WorldCopies.WorkshopPrefix) ? CreateWorld.WorkshopTitle(folder) : "";
            }
            catch (Exception) { return ""; }
        }
        // A name is the file's first line, and no longer than a name typed here can be. (The file may have come out
        // of somebody else's zip: only its start is read, since a caption is fitted a letter at a time, and a name
        // of thousands of letters would hold the menu up every time the thumbnails are laid out.)
        static string FirstLine(string file)
        {
            var start = new char[4 * Longest]; int got;
            using (var reader = new StreamReader(file)) got = reader.Read(start, 0, start.Length);
            string name = new string(start, 0, Math.Max(0, got)).TrimStart();
            int ends = name.IndexOfAny(new[] { '\r', '\n' });
            if (ends >= 0) name = name.Substring(0, ends);
            name = name.Trim();
            return name.Length > Longest ? name.Substring(0, Longest).TrimEnd() : name;
        }
        public static void Set(string directory, string name)
        {
            Forget();
            string file = Path.Combine(directory, FileName); name = (name ?? "").Trim();
            if (name.Length == 0) { if (File.Exists(file)) File.Delete(file); }
            else File.WriteAllText(file, name);
        }

        // ---- the captions
        // whenever the game lays out the thumbnails (each thumbnail's picture is named after its world's folder)
        static void Thumbnails(MenuState __instance)
        {
            try
            {
                foreach (RawImage thumb in WorldSelect.Thumbs(__instance))
                    Caption(thumb, thumb.texture != null ? Get(thumb.texture.name) : "");
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldNames: " + e.Message); }
        }

        /// a name along the bottom of a picture (nothing at all for an empty name)
        public static void Caption(RawImage picture, string name)
        {
            Transform holder = picture.transform.Find("TinyTownEnhanced Name");
            if (string.IsNullOrEmpty(name)) { if (holder != null) holder.gameObject.SetActive(false); return; }
            Text text;
            if (holder == null)
            {
                var rt = (RectTransform)new GameObject("TinyTownEnhanced Name", typeof(RectTransform)).transform;
                rt.SetParent(picture.transform, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;          // fastened to the bottom left corner
                rt.sizeDelta = new Vector2(CaptionWidth, CaptionHeight);
                rt.anchoredPosition = Vector2.zero;
                var strip = new GameObject("Strip", typeof(RectTransform)).AddComponent<Image>();      // a dark strip so the words can be read
                strip.transform.SetParent(rt, false);
                Fill(strip.rectTransform, 0f);
                strip.color = new Color(0f, 0f, 0f, 0.5f); strip.raycastTarget = false;
                text = new GameObject("Text", typeof(RectTransform)).AddComponent<Text>();
                text.transform.SetParent(rt, false);
                Fill(text.rectTransform, Padding);
                text.font = MenuPage.GameFont(); text.color = Color.white; text.alignment = TextAnchor.MiddleLeft; text.raycastTarget = false;
                text.horizontalOverflow = HorizontalWrapMode.Overflow; text.verticalOverflow = VerticalWrapMode.Overflow;
                holder = rt;
            }
            else text = holder.GetComponentInChildren<Text>(true);
            float width = picture.rectTransform.rect.width;
            holder.localScale = Vector3.one * (width > 0f ? width / CaptionWidth : 1f);
            holder.gameObject.SetActive(true);

            // the largest size at which it fits; at the smallest size, as many letters as fit and "..."
            float room = CaptionWidth - 2f * Padding; string shown = name; int size = LargestFont;
            while (size > SmallestFont && Wide(text, shown, size) > room) size -= 2;
            if (Wide(text, shown, size) > room)
            {
                int keep = name.Length;
                while (keep > 1 && Wide(text, name.Substring(0, keep).TrimEnd() + "...", size) > room) keep--;
                shown = name.Substring(0, keep).TrimEnd() + "...";
            }
            text.fontSize = size; text.text = shown;
        }

        static float Wide(Text text, string words, int size)
        {
            text.fontSize = size;
            TextGenerationSettings settings = text.GetGenerationSettings(Vector2.zero);
            return text.cachedTextGeneratorForLayout.GetPreferredWidth(words, settings) / text.pixelsPerUnit;
        }

        static void Fill(RectTransform r, float inset) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = new Vector2(inset, 0f); r.offsetMax = new Vector2(-inset, 0f); }

        // ---- the page for typing a name: Cancel, the name so far, Done, and the keyboard
        static GameObject namePage; static Text typed; static string typing, editing; static Action after; static MenuState sounds;

        /// show the typing page in place of World Select's other pages; `whenClosed` runs when it closes, saved or
        /// not. False if the keyboard could not be had.
        public static bool Edit(MenuState state, string directory, Action whenClosed)
        {
            if (namePage == null && !Build()) return false;
            sounds = state; editing = directory; typing = Get(directory); after = whenClosed;
            Show();
            namePage.SetActive(true);
            return true;
        }

        static void Show() { typed.text = typing.Length > 0 ? typing + "_" : "Type a name_"; typed.color = typing.Length > 0 ? ink : faint; }
        static Color ink, faint;

        static void Finish(bool save)
        {
            try { if (save) Set(editing, typing); } catch (Exception e) { Plugin.Log.LogWarning("WorldNames: " + e.Message); }
            namePage.SetActive(false);
            if (after != null) after();
        }

        static void Key(KeyCode code, bool capital)
        {
            WorldSelect.Click(sounds);
            if (code == KeyCode.Return) { Finish(true); return; }
            if (code == KeyCode.Backspace) { if (typing.Length > 0) typing = typing.Substring(0, typing.Length - 1); }
            else if (typing.Length < Longest) typing += TouchKeyboard.KeyCodeToString(code, capital);
            Show();
        }

        static bool Build()
        {
            if (!WorldSelect.Find()) return false;
            WristMenu menu = WorldSelect.Menu;
            var original = menu.speechPage != null ? (TouchKeyboard)AccessTools.Field(typeof(WristSpeechPage), "keyboard").GetValue(menu.speechPage) : null;
            if (original == null) return false;
            var rt = WorldSelect.NewPage("TinyTownEnhanced World Name");
            namePage = rt.gameObject;

            // the keyboard: a copy of the speech bubble page's, in the same place on the panel and the same size
            var keys = (GameObject)UnityEngine.Object.Instantiate(original.gameObject, rt, false);
            keys.name = "Keyboard";
            keys.transform.position = original.transform.position; keys.transform.rotation = original.transform.rotation;
            float mine = rt.lossyScale.x, theirs = original.transform.parent.lossyScale.x;
            keys.transform.localScale = original.transform.localScale * (mine > 0f ? theirs / mine : 1f);
            keys.SetActive(true);
            var keyboard = keys.GetComponent<TouchKeyboard>();
            keyboard.onKeyPressed = new TouchKeyboard.OnKeyPressed();      // (the copy still told the speech bubble page about its keys)
            keyboard.onKeyPressed.AddListener(Key);

            // along the top: Cancel, the name, Done
            const float y = 320f;                                       // (just under the header; the panel is 1400 wide)
            ink = MenuPage.Ink; faint = new Color(ink.r, ink.g, ink.b, 0.45f);
            typed = new GameObject("Name", typeof(RectTransform)).AddComponent<Text>();
            typed.transform.SetParent(rt, false);
            typed.rectTransform.sizeDelta = new Vector2(820f, 70f);
            typed.rectTransform.localPosition = new Vector3(0f, y, 0f);
            typed.font = MenuPage.GameFont(); typed.fontSize = 52; typed.alignment = TextAnchor.MiddleCenter; typed.raycastTarget = false;
            typed.resizeTextForBestFit = true; typed.resizeTextMinSize = 24; typed.resizeTextMaxSize = 52;
            MenuPage.Paint(MenuPage.Button(menu, rt, "Cancel", 200f, 60f, -560f, y, delegate { Finish(false); }), MenuPage.Role.Quiet);
            MenuPage.Paint(MenuPage.Button(menu, rt, "Done", 200f, 60f, 560f, y, delegate { Finish(true); }), MenuPage.Role.Go);
            namePage.SetActive(false);
            return true;
        }
    }
}
