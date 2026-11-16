// Tiny Town Enhanced - more clouds further out, and clouds that move. World tab > Atmosphere, saved with the world:
//   Cloud Distance   1x is the game's own patch of clouds; 3x / 5x / 7x repeat that patch around it in a square
//   Cloud Speed      0 = still (the game's way); otherwise every cloud drifts along and comes round again
//   Cloud Direction  which way they drift
// The game builds all its clouds as one mesh over a fixed rectangle, and never moves them.
//   Distance: that same mesh is drawn again in the neighbouring rectangles (no new cloud meshes are built). The far
//     ones (from the second ring out) draw only every third cloud, which at that distance is not noticed.
//   Movement: the cloud object slides as a whole, and a cloud's points are only rewritten when it hops from one side
//     of the rectangle to the other. So each patch stays put in the world, the clouds drift through it one at a
//     time, and the number of clouds over the world stays the same. With no repeats around it (1x), a cloud also
//     shrinks away as it reaches the edge and grows as it comes in on the other side.
//   The cloud mesh itself is built here instead of by the game (same clouds, same places), so that it can hold more
//   clouds than a standard mesh allows, the clouds can be made bigger, and each cloud's points are known.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyTownEnhanced
{
    public class CloudField : IModule
    {
        public string Name { get { return "CloudField"; } }
        const float Fade = 40f;                                     // how far from the edge a lone patch's clouds start to shrink
        static Sky sky; static GameObject clouds; static MeshFilter filter; static MeshRenderer renderer;
        static float width, length; static Vector3 slid, home;      // how far the clouds have drifted; where the game put the cloud object
        static readonly List<GameObject> copies = new List<GameObject>();
        static readonly List<MeshFilter> copyFilters = new List<MeshFilter>(); static readonly List<MeshRenderer> copyRenderers = new List<MeshRenderer>();
        static int rings = -1;
        static Mesh copiesMesh; static bool copiesVisible;         // what the repeats were last set to show (they are only touched when that changes)
        static bool failed;                                         // an error while moving the clouds: left alone for the rest of this scene
        static readonly System.Reflection.FieldInfo fClouds = AccessTools.Field(typeof(Sky), "clouds"), fMeshes = AccessTools.Field(typeof(Sky), "meshes"),
                                                    fCount = AccessTools.Field(typeof(Sky), "cloudCount");

        // movement: the game's mesh, our moving copy of it, and where each cloud's points are
        static Mesh gameMesh, moving; static Vector3[] still, moved; static Vector3[] centres; static int[] firstPoint; static bool unknown;

        public void Start(Plugin plugin)
        {
            Func<bool> cloudsOn = () => WorldFile.GetBool("clouds");
            plugin.Harmony.Patch(AccessTools.Method(typeof(Sky), "UpdateClouds"), new HarmonyMethod(typeof(CloudField), "BuildClouds"));
            WorldTab.AddSlider("Atmosphere", "cloudDistance", "Cloud Distance (Costs FPS)", 1f, 4f, 1f, v => (2 * Mathf.RoundToInt(v) - 1) + "x", () => 1f, delegate { }, cloudsOn);
            WorldTab.AddSlider("Atmosphere", "cloudSpeed", "Cloud Speed (Costs FPS)", 0f, 30f, 1f, v => v.ToString("0"), () => 0f, delegate { }, cloudsOn);
            WorldTab.AddSlider("Atmosphere", "cloudDirection", "Cloud Direction", 0f, 355f, 5f, v => v.ToString("0") + "°", () => 0f, delegate { }, cloudsOn);
        }
        public void SceneLoaded(string scene) { sky = null; clouds = null; failed = false; DropCopies(); }
        public void Tick() { if (clouds == null) Find(); }

        // (the repeats are destroyed, not just forgotten: one that was left behind would go on being drawn on top of
        //  its replacement)
        static void DropCopies()
        {
            foreach (var copy in copies) if (copy != null) UnityEngine.Object.Destroy(copy);
            copies.Clear(); copyFilters.Clear(); copyRenderers.Clear(); rings = -1; copiesMesh = null;
        }

        static void Find()
        {
            foreach (var s in Resources.FindObjectsOfTypeAll<Sky>()) if (s.gameObject.scene.IsValid()) sky = s;
            if (sky == null) return;
            var go = fClouds.GetValue(sky) as GameObject;
            if (go == null) return;                                 // (the sky has not started yet)
            filter = go.GetComponent<MeshFilter>(); renderer = go.GetComponent<MeshRenderer>();
            width = F("width"); length = F("length");
            if (filter == null || renderer == null || width <= 0f || length <= 0f) return;
            slid = Vector3.zero; DropCopies();
            home = go.transform.localPosition;
            clouds = go;
            if (sky.GetComponent<EveryFrame>() == null) sky.gameObject.AddComponent<EveryFrame>();
        }

        static float F(string field) { return (float)AccessTools.Field(typeof(Sky), field).GetValue(sky); }

        class EveryFrame : MonoBehaviour
        {
            void LateUpdate()
            {
                if (failed || clouds == null) return;
                try { Frame(); }
                // (said once, and then left alone: starting over every time would log this twice a second)
                catch (Exception e) { failed = true; Plugin.Log.LogWarning("CloudField: the clouds are left as they are after an error: " + e); }
            }
        }

        static void Frame()
        {
            int want = Mathf.Clamp(Mathf.RoundToInt(WorldFile.GetFloat("cloudDistance")) - 1, 0, 3);
            if (want != rings) Arrange(want);

            // (a cloud mesh that was not built here cannot be moved)
            Mesh shown = filter.sharedMesh; bool visible = renderer.enabled;
            if (shown != moving && shown != gameMesh) unknown = true;

            float speed = WorldFile.GetFloat("cloudSpeed");
            if (speed > 0f && !unknown && gameMesh != null && visible && clouds.activeInHierarchy)
            {
                float a = WorldFile.GetFloat("cloudDirection") * Mathf.Deg2Rad;
                slid += new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * speed * Time.deltaTime;
                slid.x = Mathf.Repeat(slid.x, width); slid.z = Mathf.Repeat(slid.z, length);
                Slide(want == 0);
                if (shown != moving) filter.sharedMesh = shown = moving;
            }
            else
            {
                if (shown == moving && gameMesh != null) filter.sharedMesh = shown = gameMesh;
                if (slid != Vector3.zero) { slid = Vector3.zero; clouds.transform.localPosition = home; }
                placed = null;
            }

            // the repeats show whatever the middle patch shows (set only when that has changed, not asked every frame)
            if (shown == copiesMesh && visible == copiesVisible) return;
            for (int i = 0; i < copies.Count; i++) { copyFilters[i].sharedMesh = shown; copyRenderers[i].enabled = visible; }
            copiesMesh = shown; copiesVisible = visible;
        }

        // Moving costs very little: the cloud object as a whole slides along (that is one number changing), and a
        // cloud's points are only rewritten when something about that cloud changes - at the moment it hops from one
        // side of the rectangle to the other, or, for a patch on its own, while it is shrinking or growing at the edge.
        // So every patch stays where it is in the world and the clouds drift through it one by one.
        static Vector3[] placed; static float[] sized;              // each cloud as its points were last written: its shift from where the game put it, and its size
        static void Slide(bool shrinkAtEdges)
        {
            float hw = width * 0.5f, hl = length * 0.5f;
            var carried = new Vector3(slid.x - hw, 0f, slid.z - hl);            // how far the cloud object itself has slid
            bool all = placed == null || placed.Length != centres.Length, changed = false;
            bool resize = Time.frameCount % 2 == 0;                              // shrinking and growing at the edge is redrawn every other frame; a hop, at once
            if (all) { placed = new Vector3[centres.Length]; sized = new float[centres.Length]; }
            for (int c = 0; c < centres.Length; c++)
            {
                Vector3 centre = centres[c];
                float x = Mathf.Repeat(centre.x + slid.x + hw, width) - hw, z = Mathf.Repeat(centre.z + slid.z + hl, length) - hl;     // where it is within the rectangle
                var shift = new Vector3(x - centre.x - carried.x, 0f, z - centre.z - carried.z);                                        // less what the sliding object carries it
                float scale = shrinkAtEdges ? Mathf.SmoothStep(0f, 1f, Mathf.Min(hw - Mathf.Abs(x), hl - Mathf.Abs(z)) / Fade) : 1f;
                bool hopped = (shift - placed[c]).sqrMagnitude >= 0.0001f;
                if (!all && !hopped && (scale == sized[c] || !resize)) continue;       // nothing about it has changed (or only its size, and this is not a frame for that)
                placed[c] = shift; sized[c] = scale; changed = true;
                Vector3 to = centre + shift;
                if (scale == 1f) for (int i = firstPoint[c]; i < firstPoint[c + 1]; i++) moved[i] = still[i] + shift;
                else for (int i = firstPoint[c]; i < firstPoint[c + 1]; i++) moved[i] = to + (still[i] - centre) * scale;
            }
            if (changed) moving.vertices = moved;
            // (the object slides in its parent's directions, the points are in its own: turn one into the other)
            Transform t = clouds.transform;
            t.localPosition = home + t.localRotation * Vector3.Scale(t.localScale, carried);
        }

        /// the Cloud Size (percent) the cloud mesh was last built with
        public static float BuiltSize = 100f;
        const int FarRing = 2;                                      // from this ring outwards a repeat draws only a third of the clouds
        static bool split;                                          // the cloud mesh is in its two parts

        // In place of the game's Sky.UpdateClouds: the same clouds in the same places (the game's dice always start from
        // the same number), but each can be made bigger (Cloud Size), the mesh may hold more than 65,000 points (Cloud
        // Amount above about 115%), and which points belong to which cloud is written down for moving them.
        static bool BuildClouds(Sky __instance)
        {
            try
            {
                sky = __instance;
                var go = fClouds.GetValue(sky) as GameObject;
                var meshes = fMeshes.GetValue(sky) as Mesh[];
                if (go == null || meshes == null || meshes.Length == 0) return true;
                int count = (int)fCount.GetValue(sky);
                if (count == 0) { go.SetActive(false); return false; }
                go.SetActive(true);
                float w = F("width"), l = F("length"), height = F("cloudHeight"), spread = F("cloudHeightRandomization") * height, min = F("cloudMinScale"), max = F("cloudMaxScale");
                float size = WorldFile.GetFloat("cloudSize");
                if (size <= 0f) size = 100f;

                var where = new Vector3[count]; var first = new int[count + 1]; var cornersOf = new int[count];
                var cornersIn = new Dictionary<Mesh, int>();
                var parts = new CombineInstance[count];
                var state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(1760772747);
                int points = 0;
                for (int i = 0; i < count; i++)
                {
                    int which = UnityEngine.Random.Range(0, meshes.Length - 1);
                    float y = height + UnityEngine.Random.Range(-spread, spread);
                    where[i] = new Vector3(UnityEngine.Random.Range(-w * 0.5f, w * 0.5f), y, UnityEngine.Random.Range(-l * 0.5f, l * 0.5f));
                    Quaternion turn = Quaternion.Euler(-90f, 0f, 0f) * Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                    float scale = UnityEngine.Random.Range(min, max) * size / 100f;
                    parts[i] = new CombineInstance { mesh = meshes[which], transform = Matrix4x4.TRS(where[i], turn, new Vector3(scale, scale, scale)) };
                    first[i] = points; points += meshes[which].vertexCount;
                    int c;
                    if (!cornersIn.TryGetValue(meshes[which], out c)) cornersIn[meshes[which]] = c = meshes[which].triangles.Length;
                    cornersOf[i] = c;
                }
                first[count] = points;
                UnityEngine.Random.state = state;

                var mesh = new Mesh { name = "TinyTownEnhanced clouds" };
                if (points > 65000) mesh.indexFormat = IndexFormat.UInt32;
                mesh.CombineMeshes(parts);

                // split into two parts: every third cloud, and the rest. The far repeats only draw the first part.
                int[] all = mesh.triangles; int corners = 0;
                foreach (int n in cornersOf) corners += n;
                if (corners == all.Length)
                {
                    var some = new List<int>(all.Length / 3 + 3); var rest = new List<int>(all.Length);
                    for (int i = 0, at = 0; i < count; at += cornersOf[i], i++)
                    {
                        var into = i % 3 == 0 ? some : rest;
                        for (int k = 0; k < cornersOf[i]; k++) into.Add(all[at + k]);
                    }
                    mesh.subMeshCount = 2;
                    mesh.SetTriangles(some, 0); mesh.SetTriangles(rest, 1);
                }
                var shown = go.GetComponent<MeshRenderer>();
                if (shown.sharedMaterials.Length != mesh.subMeshCount) { var m = shown.sharedMaterial; shown.sharedMaterials = mesh.subMeshCount == 2 ? new[] { m, m } : new[] { m }; }
                split = mesh.subMeshCount == 2; rings = -1;                     // (the repeats are set up again to match)
                var old = gameMesh;
                go.GetComponent<MeshFilter>().sharedMesh = mesh;

                gameMesh = mesh; centres = where; firstPoint = first; BuiltSize = size;
                still = mesh.vertices; moved = new Vector3[still.Length];
                if (moving != null) UnityEngine.Object.Destroy(moving);
                moving = UnityEngine.Object.Instantiate(mesh);
                moving.name = "TinyTownEnhanced moving clouds";
                moving.MarkDynamic();
                moving.bounds = new Bounds(new Vector3(0f, height, 0f), new Vector3(w * 2f + 400f, spread * 2f + 400f, l * 2f + 400f));
                placed = null;
                unknown = still.Length != points;
                if (old != null) UnityEngine.Object.Destroy(old);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("CloudField: could not build the clouds (the game will build its own): " + e);
                unknown = true;
                return true;
            }
        }

        // the repeats around the game's own patch: (2 x rings + 1) squared patches in all
        static void Arrange(int want)
        {
            DropCopies();
            for (int x = -want; x <= want; x++)
                for (int z = -want; z <= want; z++)
                {
                    if (x == 0 && z == 0) continue;
                    var copy = new GameObject("TinyTownEnhanced Clouds " + x + " " + z);
                    copy.transform.SetParent(clouds.transform, false);
                    copy.transform.localPosition = new Vector3(x * width, 0f, z * length);
                    var f = copy.AddComponent<MeshFilter>(); f.sharedMesh = filter.sharedMesh;
                    var r = copy.AddComponent<MeshRenderer>();
                    Material m = renderer.sharedMaterial;
                    bool far = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) >= FarRing;
                    r.sharedMaterials = split && !far ? new[] { m, m } : new[] { m };       // (one material: only the mesh's first part is drawn)
                    r.shadowCastingMode = ShadowCastingMode.Off; r.enabled = renderer.enabled;
                    copies.Add(copy); copyFilters.Add(f); copyRenderers.Add(r);
                }
            rings = want;
        }
    }
}
