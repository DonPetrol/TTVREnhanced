// Tiny Town Enhanced - stars. World tab > Atmosphere, saved with the world: Stars, Star Amount, Star Brightness,
// Star Size, Star Variation, Star Color, Stars All Round (below the horizon too).
// The stars are small squares scattered over a dome that stays centred on the viewer, all in one mesh (one
// thing to draw). The dome follows the viewer's position but turns with the world. They are drawn after the sky and the world, so the world and the clouds hide the stars behind
// them, and before the fog, so fog dims the ones near the horizon. They do not move or twinkle.
// The Night preset and the Time of Day slider switch them on (WorldOptions.cs); "starFade" is how far Time of Day
// has faded them in, and is not shown as a setting.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyTownEnhanced
{
    public class Stars : IModule
    {
        public string Name { get { return "Stars"; } }
        const int Most = 3000;                                      // stars at Star Amount 100%
        static bool missing, told;
        static GameObject dome; static Material material; static Mesh mesh; static int made = -1; static float madeSize, madeVariation; static bool madeBelow;

        public void Start(Plugin plugin)
        {
            Func<bool> there = () => !missing, on = () => !missing && WorldFile.GetBool("stars");
            WorldTab.AddCheckbox("Atmosphere", "stars", "Stars", "Stars fill the sky.", () => false, delegate { }, there);
            WorldTab.AddCheckbox("Atmosphere", "starsBelow", "Stars All Round", "Stars below the horizon too.", () => false, delegate { }, on);
            WorldTab.AddSlider("Atmosphere", "starAmount", "Star Amount", 10f, 100f, 10f, v => v.ToString("0") + "%", () => 50f, delegate { }, on);
            WorldTab.AddSlider("Atmosphere", "starBrightness", "Star Brightness", 10f, 100f, 10f, v => v.ToString("0") + "%", () => 100f, delegate { }, on);
            // Size: every star, equally. Variation: how much the sizes differ from star to star (0: all the same).
            WorldTab.AddSlider("Atmosphere", "starSize", "Star Size", 50f, 400f, 10f, v => v.ToString("0") + "%", () => 100f, delegate { }, on);
            WorldTab.AddSlider("Atmosphere", "starVariation", "Star Variation", 0f, 200f, 10f, v => v.ToString("0") + "%", () => 100f, delegate { }, on);
            WorldTab.AddColor("Atmosphere", "starColor", "Star Color", () => Color.white, delegate { }, on);
            WorldFile.AddFloat("starFade", () => 1f, delegate { });
            dome = new GameObject("TinyTownEnhanced Stars");
            UnityEngine.Object.DontDestroyOnLoad(dome);
            dome.AddComponent<EveryFrame>();
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        class EveryFrame : MonoBehaviour
        {
            void LateUpdate()
            {
                try { Frame(); }
                catch (Exception e) { if (!told) Plugin.Log.LogWarning("Stars: " + e); told = true; missing = true; Show(false); }
            }
        }

        static MeshRenderer drawn; static float setRadius = -1f; static Color setColor = new Color(-1f, -1f, -1f, -1f);
        static void Show(bool on) { if (drawn != null && drawn.enabled != on) drawn.enabled = on; }

        static void Frame()
        {
            if (missing) return;
            if (!WorldFile.GetBool("stars")) { Show(false); return; }
            Camera camera = Eye.Camera;                             // (not Camera.main: that searches the whole scene each time)
            if (camera == null) { Show(false); return; }
            if (material == null && !Build()) return;

            int count = Mathf.RoundToInt(Most * Mathf.Clamp01(WorldFile.GetFloat("starAmount") / 100f));
            float size = WorldFile.GetFloat("starSize") / 100f, variation = WorldFile.GetFloat("starVariation") / 100f;
            bool below = WorldFile.GetBool("starsBelow");
            if (count != made || size != madeSize || variation != madeVariation || below != madeBelow) Scatter(count, size, variation, below);
            Show(true);
            float radius = camera.farClipPlane * 0.8f;
            dome.transform.position = camera.transform.position;
            // the stars belong to the world's sky: when the world is turned (the two-handed rotate), they turn with it
            var world = Singleton<WorldSettings>.Instance;
            if (world != null && world.DisplayWorld != null) dome.transform.rotation = world.DisplayWorld.rotation;
            // (the size and the colour are only handed to the engine when they have changed, not every frame)
            if (radius != setRadius) { setRadius = radius; dome.transform.localScale = new Vector3(radius, radius, radius); }
            Color tint = WorldFile.GetColor("starColor");
            tint.a = Mathf.Clamp01(WorldFile.GetFloat("starBrightness") / 100f * WorldFile.GetFloat("starFade"));
            if (tint != setColor) { setColor = tint; material.color = tint; }
        }

        static bool Build()
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("UI/Default");
            if (shader == null) { missing = true; Plugin.Log.LogWarning("Stars: no see-through shader in the game; stars are not available"); return false; }
            material = new Material(shader) { renderQueue = 2990 };         // (just before the fog, which is 3000)
            mesh = new Mesh { name = "TinyTownEnhanced Stars" };
            dome.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = dome.AddComponent<MeshRenderer>();
            r.sharedMaterial = material; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            drawn = r;
            return true;
        }

        // the same stars every time (fixed dice), over the whole sky down to a little below the horizon;
        // sizes and strengths vary, most of them small and faint
        // With "Stars All Round" the rest of the sphere, underneath, gets stars as well, as thickly as the top: they are
        // extra stars with dice of their own, so the ones above stay exactly where they were.
        static void Scatter(int upper, float size, float variation, bool below)
        {
            var dice = new System.Random(20261002);
            int count = upper + (below ? Mathf.RoundToInt(upper * 0.9f / 1.1f) : 0);
            var points = new Vector3[count * 4]; var colours = new Color[count * 4]; var triangles = new int[count * 6];
            for (int i = 0; i < count; i++)
            {
                if (i == upper) dice = new System.Random(20261006);
                float y = i < upper ? -0.1f + 1.1f * (float)dice.NextDouble() : -1f + 0.9f * (float)dice.NextDouble(), turn = 2f * Mathf.PI * (float)dice.NextDouble();
                float flat = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                Vector3 at = new Vector3(flat * Mathf.Cos(turn), y, flat * Mathf.Sin(turn));
                float big = (float)dice.NextDouble(); big = big * big * big;                    // few big ones
                // (at 100% variation a star is 0.6 to 2.1 times the middle size; at 0% they are all the middle size)
                float half = 0.0015f * size * Mathf.Max(0.15f, Mathf.LerpUnclamped(1f, 0.6f + 1.47f * big, variation)), strength = 0.35f + 0.65f * Mathf.Max(big, (float)dice.NextDouble() * 0.6f);
                Vector3 side = Vector3.Cross(at, Vector3.up).normalized * half, upward = Vector3.Cross(side, at).normalized * half;
                int p = i * 4, t = i * 6;
                points[p] = at - side - upward; points[p + 1] = at - side + upward; points[p + 2] = at + side + upward; points[p + 3] = at + side - upward;
                for (int k = 0; k < 4; k++) colours[p + k] = new Color(1f, 1f, 1f, strength);
                triangles[t] = p; triangles[t + 1] = p + 1; triangles[t + 2] = p + 2; triangles[t + 3] = p; triangles[t + 4] = p + 2; triangles[t + 5] = p + 3;
            }
            mesh.Clear();
            mesh.vertices = points; mesh.colors = colours; mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            made = upper; madeSize = size; madeVariation = variation; madeBelow = below;
        }
    }
}
