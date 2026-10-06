// Tiny Town Enhanced - the main menu says which game this is.
// Where the game greets you with "Welcome!" (the title of the first page of its help and tutorial pages, and any
// other writing that says exactly that), it says "Tiny Town VR Enhanced" and the mod's version instead, so that it
// can be seen at a glance that the mod is running, and which one.
using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class Welcome : IModule
    {
        public string Name { get { return "Welcome"; } }
        const string Theirs = "Welcome!";
        static string Ours { get { return "Tiny Town VR Enhanced " + Plugin.Version; } }

        public void Start(Plugin plugin)
        {
            // (whenever the wrist menu is set up for the main menu or for a world: by then the pages exist)
            foreach (string m in new[] { "ResetForMainMenu", "ResetForPlay" })
                plugin.Harmony.Patch(AccessTools.Method(typeof(WristMenu), m), null, new HarmonyMethod(typeof(Welcome), "Change"));
        }
        public void SceneLoaded(string scene) { Change(); }
        public void Tick() { }

        static void Change()
        {
            try
            {
                // the list of titles the help pages show in turn (the page writes the current one out again each time)
                var fTitles = AccessTools.Field(typeof(WristHelpPage), "titles");
                foreach (var page in Resources.FindObjectsOfTypeAll<WristHelpPage>())
                {
                    var titles = fTitles.GetValue(page) as string[];
                    for (int i = 0; titles != null && i < titles.Length; i++) if (titles[i] == Theirs) titles[i] = Ours;
                }
                // and anything already written on screen (ours is longer: it is drawn smaller if it does not fit)
                foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
                    if (text.text == Theirs)
                    {
                        text.text = Ours;
                        text.resizeTextForBestFit = true; text.resizeTextMinSize = 10; text.resizeTextMaxSize = Mathf.Max(10, text.fontSize);
                    }
                foreach (var text in Resources.FindObjectsOfTypeAll<TextMesh>()) if (text.text == Theirs) text.text = Ours;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Welcome: " + e.Message); }
        }
    }
}
