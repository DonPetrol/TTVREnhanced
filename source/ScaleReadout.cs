// Tiny Town Enhanced - the zoom shown in front of you while you change it.
// Whenever the world's scale changes - the two-handed pinch, or the stick - a small readout a little below the middle
// of the view shows it ("World Scale 1.25x"), and goes away a few seconds after the last change. Changing it with the
// World Scale slider on the wrist menu does not bring it up (the slider already shows the number), and neither does a
// world being opened.
using System;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class ScaleReadout : IModule
    {
        public string Name { get { return "ScaleReadout"; } }
        const float Seconds = 2.5f;
        static float last = -1f, lastFrameAt;
        static int shownAs = -1;                // the scale last put in the readout, in hundredths (what its two decimals show)

        public void Start(Plugin plugin)
        {
            // (PlayState.Early runs once a frame, and only while a world is open)
            plugin.Harmony.Patch(AccessTools.Method(typeof(PlayState), "Early"), null, new HarmonyMethod(typeof(ScaleReadout), "Frame"));
        }
        public void SceneLoaded(string scene) { last = -1f; }
        public void Tick() { }

        /// the scale has just been changed by something that shows it itself: do not bring the readout up for it
        public static void Seen()
        {
            var world = Singleton<WorldSettings>.Instance;
            if (world != null) last = world.Scale;
        }

        static void Frame()
        {
            try
            {
                var world = Singleton<WorldSettings>.Instance;
                if (world == null) return;
                float scale = world.Scale;
                // just come into a world (or back from a pause in play): take the scale as it is
                bool fresh = last < 0f || Time.unscaledTime - lastFrameAt > 0.5f;
                lastFrameAt = Time.unscaledTime;
                if (!fresh && Mathf.Abs(scale - last) > 0.0005f)
                {
                    // the words are only made again when the number they show has changed: a zoom changes the scale
                    // every frame, but the two decimals far less often
                    int hundredths = Mathf.RoundToInt(scale * 100f);
                    if (hundredths != shownAs)
                    {
                        shownAs = hundredths;
                        Popup.Note("World Scale " + scale.ToString(scale < 0.1f ? "0.000" : scale < 10f ? "0.00" : "0.0") + "x", Seconds, 0);     // (rank 0: an autosave message showing at the time is left up)
                    }
                }
                else if (fresh) shownAs = -1;
                last = scale;
            }
            catch (Exception) { }
        }
    }
}
