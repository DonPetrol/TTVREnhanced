// Tiny Town Enhanced - worlds open faster.
// The game builds a world 300 objects at a time, one group per frame, while the screen is faded to black. A world of
// 45,000 objects needed 155 frames (4.4 s) for 1.6 s of actual work. This module lets the game's own loading routine
// (SceneGraph.Load) do several of its steps in each frame, up to a time budget, so the same work fits in far fewer
// frames. Nothing about what is loaded changes.
using System.Collections;
using System.Diagnostics;
using HarmonyLib;

namespace TinyTownEnhanced
{
    public class FastWorldLoad : IModule
    {
        public string Name { get { return "FastWorldLoad"; } }
        const double BudgetMs = 50;         // loading work per frame (the screen is black while this runs)

        public void Start(Plugin plugin)
        {
            plugin.Harmony.Patch(AccessTools.Method(typeof(SceneGraph), "Load"), null, new HarmonyMethod(typeof(FastWorldLoad), "Wrap"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Wrap(ref IEnumerator __result) { __result = Hurry(__result); }

        static IEnumerator Hurry(IEnumerator inner)
        {
            var sw = new Stopwatch();
            while (true)
            {
                sw.Reset(); sw.Start();
                object wait;
                do
                {
                    if (!inner.MoveNext()) yield break;
                    wait = inner.Current;
                }
                while (wait == null && sw.Elapsed.TotalMilliseconds < BudgetMs);    // "wait one frame" steps are run together
                yield return wait;
            }
        }
    }
}
