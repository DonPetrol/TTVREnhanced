// Tiny Town Enhanced - the wrist menu panel is a little taller.
// The panel was 1400 x 900 (a 150 header, a 112 tab bar, and the page between them). It is made taller by Extra,
// growing upwards from its bottom edge. The header is moved up with the top edge; the pages stay centred, so the
// game's own pages simply have a little more margin above and below, while the pages this mod lays out (MenuPage.cs)
// start their rows that much higher and put their page arrows that much lower.
using System;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class PanelSize : IModule
    {
        public string Name { get { return "PanelSize"; } }
        public const float Extra = 120f;            // added height, in the panel's own units (about one row of a MenuPage, which is 112)
        public const float Half = Extra * 0.5f;     // how far the top and bottom edges of a page's area moved

        static bool done;

        public void Start(Plugin plugin)
        {
            var me = new HarmonyMethod(typeof(PanelSize), "Grow");
            foreach (var m in new[] { "Awake", "ResetForPlay", "ResetForMainMenu" })       // whichever comes first
                plugin.Harmony.Patch(AccessTools.Method(typeof(WristMenu), m), null, me);
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Grow(WristMenu __instance)
        {
            if (done) return;
            try
            {
                var panel = __instance.GetComponent<RectTransform>();
                panel.sizeDelta = new Vector2(panel.sizeDelta.x, panel.sizeDelta.y + Extra);

                // the header is fastened to the panel's middle, which has just moved up by half the extra height
                var header = panel.Find("Background/Header") as RectTransform;
                if (header != null) header.anchoredPosition += new Vector2(0f, Half);

                // the box that senses a hand over the panel covers the new height too
                var box = __instance.GetComponent<BoxCollider>();
                if (box != null)
                {
                    box.size = new Vector3(box.size.x, box.size.y + Extra, box.size.z);
                    box.center = new Vector3(box.center.x, box.center.y + Half, box.center.z);
                }
                done = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("PanelSize: " + e.Message); }
        }
    }
}
