// Tiny Town Enhanced - fog. World tab > Atmosphere, saved with the world: Fog, Fog Density, Fog Color.
// The game's own shaders ignore Unity's fog, and a screen effect (tried first) blurred the picture and cost frames.
// So this is layered fog: two dozen very faint see-through shells around the viewer, each a little further out. An
// object is seen through every shell that is nearer than it, so the further away it is the more fog colour it picks
// up - which is what fog does. Nothing is copied or blurred, and anti-aliasing is untouched.
//   - the shells fade out above the horizon, so the sky overhead keeps its colours
//   - until a Fog Color is picked, the fog takes the sky's horizon colour
//   - the shells are sized in units of the WORLD, so the fog looks the same however far the world is zoomed
//   - shells that would come closer than arm's length are left out, so the hands and wrist menu stay clear
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyTownEnhanced
{
    public class Fog : IModule
    {
        public string Name { get { return "Fog"; } }
        const int Shells = 24;
        const float Nearest = 1.5f;                                 // metres: nothing closer than this is fogged
        static bool missing, told;
        static GameObject root; static readonly Transform[] shells = new Transform[Shells]; static readonly Material[] materials = new Material[Shells];

        public void Start(Plugin plugin)
        {
            Func<bool> there = () => !missing, on = () => !missing && WorldFile.GetBool("fog");
            WorldTab.AddCheckbox("Atmosphere", "fog", "Fog (Costs FPS)", "Distant parts of the world fade into haze.", () => false, delegate { }, there);
            WorldTab.AddSlider("Atmosphere", "fogDensity", "Fog Density", 1f, 100f, 1f, v => v.ToString("0"), () => 20f, delegate { }, on);
            WorldTab.AddColor("Atmosphere", "fogColor", "Fog Color", () => WorldFile.GetColor("skyHorizon"), delegate { }, on);
            var go = new GameObject("TinyTownEnhanced Fog");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<EveryFrame>();
            root = go;
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        class EveryFrame : MonoBehaviour
        {
            void LateUpdate()
            {
                try { Frame(); }
                catch (Exception e) { if (!told) Plugin.Log.LogWarning("Fog: " + e); told = true; missing = true; Hide(); }
            }
        }

        // what the shells were last set up for: they are only gone through again when one of these has changed (the
        // zoom, the density, the colour or the camera's range), not every frame
        static float setK = -1f, setFar; static Color setColor; static bool shown;

        static void Hide()
        {
            if (!shown) return;
            shown = false; setK = -1f;                              // (so they are set up afresh when the fog comes back)
            foreach (var s in shells) if (s != null && s.gameObject.activeSelf) s.gameObject.SetActive(false);
        }

        static void Frame()
        {
            if (missing) return;
            if (!WorldFile.GetBool("fog")) { Hide(); return; }
            Camera camera = Eye.Camera;                             // (not Camera.main: that searches the whole scene each time)
            if (camera == null) { Hide(); return; }
            if (shells[0] == null && !Build()) return;

            float scale = 1f;
            var world = Singleton<WorldSettings>.Instance;
            if (world != null) scale = Mathf.Max(0.0001f, world.Scale);
            // fog left after distance d: exp(-(k d)^2). The slider is squared, for fine steps at the thin end and
            // really thick fog at the top: 20 clears at about 250 world units, 100 at about 10
            float density = WorldFile.GetFloat("fogDensity");
            float k = density * density * 0.00001f / scale;
            Color c = WorldFile.GetColor(WorldFile.IsSet("fogColor") ? "fogColor" : "skyHorizon");
            root.transform.position = camera.transform.position;
            float far = camera.farClipPlane * 0.9f, before = 1f;
            if (k == setK && far == setFar && c == setColor) return;
            setK = k; setFar = far; setColor = c; shown = true;
            for (int i = 0; i < Shells; i++)
            {
                float radius = (0.2f + 2.0f * i / (Shells - 1)) / k;            // from "barely any fog" out to "nearly all fog"
                float left = Mathf.Exp(-(k * radius) * (k * radius));
                bool use = radius >= Nearest && radius <= far;
                if (shells[i].gameObject.activeSelf != use) shells[i].gameObject.SetActive(use);
                if (!use) continue;                                             // (its share of fog goes to the next shell out)
                shells[i].localScale = new Vector3(radius, radius, radius);
                materials[i].color = new Color(c.r, c.g, c.b, Mathf.Clamp01(1f - left / before));
                before = left;
            }
        }

        static bool Build()
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("UI/Default");
            if (shader == null) { missing = true; Plugin.Log.LogWarning("Fog: no see-through shader in the game; fog is not available"); return false; }

            // a ball seen from inside; full strength up to just above the horizon, fading to nothing higher up
            const int around = 32, up = 16;
            var points = new Vector3[(around + 1) * (up + 1)]; var colours = new Color[points.Length]; var triangles = new int[around * up * 6];
            for (int j = 0, n = 0; j <= up; j++)
                for (int i = 0; i <= around; i++, n++)
                {
                    float lat = Mathf.PI * (j / (float)up - 0.5f), lon = 2f * Mathf.PI * i / around;
                    points[n] = new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));
                    colours[n] = new Color(1f, 1f, 1f, 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.03f, 0.4f, points[n].y)));
                }
            for (int j = 0, t = 0; j < up; j++)
                for (int i = 0; i < around; i++)
                {
                    int a = j * (around + 1) + i, b = a + around + 1;
                    triangles[t++] = a; triangles[t++] = a + 1; triangles[t++] = b; triangles[t++] = a + 1; triangles[t++] = b + 1; triangles[t++] = b;
                }
            var mesh = new Mesh { name = "TinyTownEnhanced Fog" };
            mesh.vertices = points; mesh.colors = colours; mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);

            for (int i = 0; i < Shells; i++)
            {
                var go = new GameObject("Shell " + i);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                materials[i] = new Material(shader) { renderQueue = 3000 };
                if (materials[i].HasProperty("_Cull")) materials[i].SetInt("_Cull", 0);
                r.sharedMaterial = materials[i];
                r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                shells[i] = go.transform;
            }
            return true;
        }
    }
}
