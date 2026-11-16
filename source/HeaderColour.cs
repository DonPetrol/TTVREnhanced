// Tiny Town Enhanced - the mod's blue (#00C6FF, the colour of the "Enhanced" box on the main menu) for the menus'
// headers: the wrist menu's (the bar along the top with the page's name in it), on every page, and the main menu's
// "Welcome!" board beside the town. Settings > Video: "Blue Header" (on by default); off puts the game's own colours back.
// A header is the picture its title sits on. The game also draws a strip of the same colour along the bottom of the wrist
// menu's panel ("BottomRow"), as a separate picture, so every plain picture of the header's colour is painted - not
// the game's buttons, several of which are that colour too, and never a pale one (the page behind the words). The game
// never changes these colours itself: they are painted each time a menu is set up, and when the setting changes.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class HeaderColour : IModule
    {
        public string Name { get { return "HeaderColour"; } }
        static ConfigEntry<bool> on;

        // one menu's pictures to paint, each with the game's own colour (to put back when the setting is turned off)
        class Painted
        {
            public readonly string What; public bool Told; public bool Looked;
            Color? own;         // the header's colour as the game made it (kept: when looked at again, the header may be blue already)
            public readonly Dictionary<Graphic, Color> Was = new Dictionary<Graphic, Color>();
            public Painted(string what) { What = what; }

            /// what was found is still there (a menu made anew needs looking at again)
            public bool Fresh() { foreach (var kv in Was) if (kv.Key == null) { Was.Clear(); return false; } return Was.Count > 0; }

            /// the picture behind `title` (and every plain picture of its colour under `root`)
            public void Find(Text title, Transform root)
            {
                Graphic header = null;
                for (Transform t = title != null ? title.transform.parent : null; t != null && t != root && header == null; t = t.parent)
                    foreach (var g in t.GetComponents<Graphic>()) if (!(g is Text)) { header = g; break; }
                if (header != null && !own.HasValue) own = header.color;
                Color colour = own ?? Color.white;
                if (header == null || (colour.r > 0.85f && colour.g > 0.85f && colour.b > 0.85f))
                {
                    Tell(true, "header not found" + (header != null ? " ('" + header.name + "' is pale)" : ""));
                    own = null;
                    return;
                }
                foreach (var g in root.GetComponentsInChildren<Graphic>(true))
                    if (!(g is Text) && Close(g.color, colour) && g.GetComponent<Selectable>() == null && g.GetComponent<PushButton>() == null) Was[g] = g.color;
                var names = new List<string>(); foreach (var kv in Was) names.Add(kv.Key.name);
                Tell(false, "painting " + string.Join(", ", names.ToArray()));
            }
            public void Tell(bool warn, string words)
            {
                if (Told) return;
                Told = true;
                if (warn) Plugin.Log.LogWarning("HeaderColour (" + What + "): " + words); else Plugin.Log.LogDebug("HeaderColour (" + What + "): " + words);
            }
            public void Apply() { foreach (var kv in Was) if (kv.Key != null) kv.Key.color = on.Value ? Colours.Accent : kv.Value; }
        }
        static readonly Painted wrist = new Painted("wrist menu"), board = new Painted("Welcome! board");

        public void Start(Plugin plugin)
        {
            on = plugin.Config.Bind("Video", "BlueHeader", true, "The menus' headers are the mod's blue instead of the game's own colour.");
            SettingsTabs.AddCheckbox("Video", "Blue Header", "The wrist menu's header in Tiny Town Enhanced's blue.", on, delegate (bool v) { wrist.Apply(); board.Apply(); });
            var me = typeof(HeaderColour);
            foreach (string m in new[] { "ResetForMainMenu", "ResetForPlay" })
                plugin.Harmony.Patch(AccessTools.Method(typeof(WristMenu), m), postfix: new HarmonyMethod(me, "Wrist"));
            // (the board: looked for once each time the main menu is loaded, painted again each time the game places the
            //  menu's parts round you)
            plugin.Harmony.Patch(AccessTools.Method(typeof(MenuState), "LoadMenuWorld"), postfix: new HarmonyMethod(me, "MenuLoading"));
            plugin.Harmony.Patch(AccessTools.Method(typeof(MenuState), "PositionTitle"), postfix: new HarmonyMethod(me, "Board"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Wrist(WristMenu __instance)
        {
            try
            {
                if (__instance == null || __instance.title == null) return;
                if (!wrist.Fresh()) wrist.Find(__instance.title, __instance.transform);
                wrist.Apply();
            }
            catch (Exception e) { Plugin.Log.LogWarning("HeaderColour: " + e.Message); }
        }

        static void MenuLoading() { board.Looked = false; Board(); }
        static void Board()
        {
            try
            {
                // The board is found by its title, "Welcome!", anywhere outside the wrist menu (it is not the game's
                // popupHelpCanvas, which holds only two white pictures). The search goes through every text loaded, so
                // it is done at most once each time the main menu is loaded.
                if (!board.Fresh() && !board.Looked)
                {
                    board.Looked = true;
                    foreach (var t in Resources.FindObjectsOfTypeAll<Text>())
                        if (t != null && t.text != null && t.text.Trim() == "Welcome!" && t.gameObject.scene.IsValid() && t.GetComponentInParent<WristMenu>() == null)
                        {
                            var canvas = t.GetComponentInParent<Canvas>();
                            board.Find(t, canvas != null ? canvas.rootCanvas.transform : t.transform.root);
                            break;
                        }
                    if (board.Was.Count == 0) board.Tell(true, "not found");
                }
                board.Apply();
            }
            catch (Exception e) { Plugin.Log.LogWarning("HeaderColour: " + e.Message); }
        }

        static bool Close(Color a, Color b) { return Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f && Mathf.Abs(a.a - b.a) < 0.02f; }
    }
}
