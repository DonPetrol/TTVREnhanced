// Tiny Town Enhanced - a comfort vignette: the edges of the view darken while you are being moved.
// Settings > Accessibility: Movement Vignette (off by default), Vignette Strength (how far it closes in), Vignette
// Color (black, or a soft colour taken from the sky), and whether
// turning and scaling bring it up as well.
// It is a black ring fastened to the headset a little in front of the eyes, clear in the middle; it closes in from
// beyond the edge of the view as you start to move and opens again when you stop.
// The ring is two pieces: a soft see-through edge, and the solid black outside it. The solid piece is drawn before
// the world and marks itself as the nearest thing, so the graphics card skips the world behind it: while the
// vignette is up there is less to draw, not more. "Being moved" means the world moving, turning or
// changing size around you - by the sticks, by grabbing, by drift - which is measured from the world itself each
// frame, so every way of moving counts and moving your own head does not. A snap turn is over in one frame and is
// left alone: nothing is measured in the frame it happens in.
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VRTK;

namespace TinyTownEnhanced
{
    public class Vignette : IModule
    {
        public string Name { get { return "Vignette"; } }
        static ConfigEntry<bool> on, turning, scaling; static ConfigEntry<float> strength; static ConfigEntry<int> colour;
        const float SkyShade = 0.55f;                           // the "Sky" colour is the world's horizon colour, this much darker
        static Material edge, solid; static Mesh edgeMesh, solidMesh;     // made once and kept, even if the ring itself has to be made again
        static bool unavailable, warned;                        // it cannot be drawn in this game (until the game is restarted); a fault has been written to the log

        // how fast counts as "fully moving" (it comes up in proportion below that), and what is too slow to count at all
        const float FullSpeed = 0.5f, SlowSpeed = 0.04f;        // metres a second, as you see them
        const float FullTurn = 60f, SlowTurn = 4f, SnapTurn = 10f;   // degrees a second; more than SnapTurn degrees in one frame is a snap turn
        const float FullScale = 0.5f, SlowScale = 0.04f;        // how many times its size the world changes by in a second
        const float FadeIn = 0.15f, FadeOut = 0.4f;             // seconds
        const float OpenAngle = 62f, WeakAngle = 46f, StrongAngle = 22f;   // degrees from straight ahead that stay clear: when open, and when closed in at the weakest and strongest setting
        const float Soft = 1.4f;                                // the dark edge fades in over this much of the clear part's radius
        const int Segments = 48;

        static GameObject ring; static Feed feed;
        static Matrix4x4 lastWorld; static Quaternion lastTurn; static float lastScale, lastAt, shown;

        public void Start(Plugin plugin)
        {
            on = plugin.Config.Bind("Accessibility", "Vignette", false, "Darken the edges of the view while you are being moved (a comfort option).");
            strength = plugin.Config.Bind("Accessibility", "VignetteStrength", 0.5f, "How far the vignette closes in (0.1 to 1).");
            turning = plugin.Config.Bind("Accessibility", "VignetteTurning", true, "Smooth turning brings the vignette up too.");
            scaling = plugin.Config.Bind("Accessibility", "VignetteScaling", true, "Changing the world's scale brings the vignette up too.");
            SettingsTabs.AddCheckbox("Accessibility", "Movement Vignette", "The edges of the view darken while you are moving.", on, null);
            SettingsTabs.AddSlider("Accessibility", "Vignette Strength", strength, 0.1f, 1f, 0.1f, v => Mathf.RoundToInt(v * 100f) + "%", null, () => on.Value);
            colour = plugin.Config.Bind("Accessibility", "VignetteColour", 0, "The vignette's colour: 0 black, 1 a soft colour taken from the world's sky.");
            SettingsTabs.AddChoice("Accessibility", "Vignette Color", new[] { "Black", "Sky" }, colour, null, () => on.Value);
            SettingsTabs.AddCheckbox("Accessibility", "Vignette on Turning", "Smooth turning brings the vignette up too.", turning, null, () => on.Value);
            SettingsTabs.AddCheckbox("Accessibility", "Vignette on Scaling", "Changing the world's scale brings the vignette up too.", scaling, null, () => on.Value);
            // (PlayState.Early runs once a frame, and only while a world is open)
            plugin.Harmony.Patch(AccessTools.Method(typeof(PlayState), "Early"), null, new HarmonyMethod(typeof(Vignette), "Frame"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Frame()
        {
            if (!on.Value || unavailable) { if (ring != null && ring.activeSelf) ring.SetActive(false); shown = 0f; lastAt = 0f; return; }
            try
            {
                var world = Singleton<WorldSettings>.Instance;
                Camera eye = Eye.Camera;
                if (world == null || world.DisplayWorld == null || eye == null) return;
                Transform display = world.DisplayWorld;
                float now = Time.unscaledTime, dt = now - lastAt;
                float wanted = 0f;
                if (lastAt > 0f && dt > 0.0001f && dt < 0.5f)
                {
                    float angle = Quaternion.Angle(lastTurn, display.rotation);
                    if (angle >= SnapTurn)
                    {
                        // a snap turn: the whole frame is left out (the vignette stays as it is). A turn about anything
                        // but your head also carries the world at your head a long way in that one frame, which would
                        // otherwise count as moving very fast.
                        wanted = shown;
                    }
                    else
                    {
                        // how far the piece of world that is at your head has been carried since the last frame
                        Vector3 head = eye.transform.position;
                        Vector3 before = lastWorld.MultiplyPoint3x4(display.InverseTransformPoint(head));
                        wanted = Rate((head - before).magnitude / dt, SlowSpeed, FullSpeed);
                        if (turning.Value) wanted = Mathf.Max(wanted, Rate(angle / dt, SlowTurn, FullTurn));
                        if (scaling.Value && lastScale > 0f)
                            wanted = Mathf.Max(wanted, Rate(Mathf.Abs(Mathf.Log(display.lossyScale.x / lastScale)) / dt, SlowScale, FullScale));
                    }
                }
                lastWorld = display.localToWorldMatrix; lastTurn = display.rotation; lastScale = display.lossyScale.x; lastAt = now;

                shown = Mathf.MoveTowards(shown, wanted, Mathf.Min(dt, 0.1f) / (wanted > shown ? FadeIn : FadeOut));
                if (shown <= 0.001f) { if (ring != null && ring.activeSelf) ring.SetActive(false); return; }
                if (ring == null && !Build(eye)) return;
                if (!ring.activeSelf) ring.SetActive(true);
                feed.At = now;

                // closing in from the open angle (out of sight) to the one for this strength as it comes up
                float distance = Mathf.Max(0.25f, eye.nearClipPlane * 2f);
                float clear = Mathf.Lerp(OpenAngle, Mathf.Lerp(WeakAngle, StrongAngle, Mathf.Clamp01(strength.Value)), shown);
                float radius = distance * Mathf.Tan(clear * Mathf.Deg2Rad);
                ring.transform.localPosition = new Vector3(0f, 0f, distance);
                ring.transform.localScale = new Vector3(radius, radius, 1f);
                Color shade = Color.black;
                if (colour.Value == 1) { shade = WorldFile.GetColor("skyHorizon") * SkyShade; shade.a = 1f; }
                if (edge.color != shade) { edge.color = shade; solid.color = shade; }
            }
            // (this runs every frame: something that keeps going wrong is written to the log once, not 72 times a second)
            catch (Exception e) { if (!warned) { warned = true; Plugin.Log.LogWarning("Vignette: " + e); } }
        }

        static float Rate(float value, float slow, float full) { return Mathf.Clamp01((value - slow) / (full - slow)); }

        // a flat ring facing you, in two pieces: the soft edge (see-through at radius 1 - scaled to fit - and black at
        // Soft), and the solid black from there outwards, wide enough to fill the rest of the view
        static bool Build(Camera eye)
        {
            Shader soft = Shader.Find("Sprites/Default");
            // (the option itself is left as the player set it: it is only this run of the game that goes without)
            if (soft == null) { Plugin.Log.LogWarning("Vignette: no see-through shader in the game"); unavailable = true; return false; }
            try
            {
                ring = new GameObject("TinyTownEnhanced Vignette");
                Transform head = VRTK_DeviceFinder.HeadsetTransform();
                ring.transform.SetParent(head != null ? head : eye.transform, false);
                ring.transform.localRotation = Quaternion.identity;
                feed = ring.AddComponent<Feed>();

                // (the materials and the two meshes are kept when the ring has gone with its headset and is made again)
                if (edge == null) edge = new Material(soft) { color = Color.black, renderQueue = 3900 };      // after the world and its fog, before the messages
                Piece("Edge", 1f, Soft, 0f, 1f, edge, ref edgeMesh);
                if (solid == null)
                {
                    // the solid piece: the engine's plain-colour shader, set to cover what is behind it and drawn first of all
                    Shader plain = Shader.Find("Hidden/Internal-Colored");
                    if (plain != null)
                    {
                        solid = new Material(plain) { color = Color.black, renderQueue = 1000 };
                        solid.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One); solid.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                        solid.SetInt("_ZWrite", 1); solid.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                    }
                    else solid = new Material(soft) { color = Color.black, renderQueue = 3900 };      // (no plain shader: drawn over the world instead)
                }
                Piece("Outside", Soft, 40f, 1f, 1f, solid, ref solidMesh);
                return true;
            }
            catch (Exception)
            {
                // half a ring is no use, and would fail again every frame: it is taken away and not tried again
                if (ring != null) UnityEngine.Object.Destroy(ring);
                ring = null; unavailable = true;
                throw;
            }
        }

        // a band of the ring between two radii, with how black it is at each
        static void Piece(string name, float inner, float outer, float innerAlpha, float outerAlpha, Material material, ref Mesh mesh)
        {
            if (mesh == null) mesh = Band(name, inner, outer, innerAlpha, outerAlpha);
            var piece = new GameObject(name);
            piece.transform.SetParent(ring.transform, false);
            piece.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = piece.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
        }

        static Mesh Band(string name, float inner, float outer, float innerAlpha, float outerAlpha)
        {
            var points = new Vector3[2 * Segments]; var colours = new Color[points.Length]; var triangles = new int[Segments * 6];
            for (int s = 0; s < Segments; s++)
            {
                float a = s * Mathf.PI * 2f / Segments; var direction = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                points[s] = direction * inner; points[Segments + s] = direction * outer;
                colours[s] = new Color(1f, 1f, 1f, innerAlpha); colours[Segments + s] = new Color(1f, 1f, 1f, outerAlpha);      // (the colour itself is the material's)
                int n = (s + 1) % Segments, t = s * 6;
                triangles[t] = s; triangles[t + 1] = Segments + s; triangles[t + 2] = n;
                triangles[t + 3] = n; triangles[t + 4] = Segments + s; triangles[t + 5] = Segments + n;
            }
            var mesh = new Mesh { name = "TinyTownEnhanced Vignette " + name, vertices = points, colors = colours, triangles = triangles };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100f);    // (never left out for being "out of view")
            return mesh;
        }

        // the ring is only looked after while a world is open: left to itself (the world has been quit) it puts itself away
        class Feed : MonoBehaviour
        {
            public float At;
            void LateUpdate() { if (Time.unscaledTime - At > 0.25f) { shown = 0f; lastAt = 0f; gameObject.SetActive(false); } }
        }
    }
}
