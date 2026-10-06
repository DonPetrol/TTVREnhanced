// Tiny Town Enhanced - a placed model can be given a look of its own: smoothness, metallic, a colour tint, a glow,
// transparency, "unlit" (full brightness whatever the light) and whether it casts a shadow.
// (EditTab.cs is the wrist menu page that changes them; this file is how they are drawn and saved.)
//
// How it is drawn. The game draws models grouped by "material slot": slots 0-18 are its own, and MoreItems.cs adds
// more for extra Workshop texture stacks. A model with a look is simply put in a different slot, one made for that
// look (slots from 64 up), and everything else - the grid of cells, the merged meshes, shadows - works as before:
//   - only smoothness / metallic changed: a copy of the model's usual material with those two numbers changed. It
//     shares the game's texture stack, so it costs no texture memory.
//   - tint, glow, transparency or unlit: the game's own shader has none of these, so the look uses Unity's Standard
//     shader (which the game also ships) with the model's texture copied out of the stack into a texture of its
//     own. That copy takes memory (once for each kind of model), which is why the Edit page marks these "Costs FPS".
//   - no shadow: a slot of its own as well, and FastRebuild.cs builds that slot's meshes as casting none.
// Models with the same look share a slot; a slot nobody uses any more is used again for the next new look.
// A model that had a look keeps the (empty) note of it for as long as it exists: taking a look away only empties it.
//
// How it is saved. With the world's settings (WorldFile.cs), as one line "looks": for each model with a look, the
// number the game gives that model in the save, and the values. An unmodified game ignores it.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class Looks : IModule
    {
        public string Name { get { return "Looks"; } }
        public const int FirstSlot = 64, Step = 32;     // where the looks' slots start, and how many are made room for at a time
        public static int Capacity;                     // look slots there is room for so far (MoreItems.cs sizes the renderer's tables by it)
        public static int Count;                        // models that have a look at the moment (kept in step in Write and Release only)
        const float RedrawAfter = 0.08f;                // seconds: a slider being dragged redraws the model this often at most

        /// the values of a look
        public struct Values
        {
            public float Smooth, Metal; public Color Tint, Glow;
            public float Alpha;         // 1 solid .. 0 fully see-through
            public bool Unlit;          // drawn at full brightness whatever the world's light
            public bool GlowMap;        // it glows in its own colours (its texture), not in the one Glow colour
            public float GlowStrength;  // how strongly, when it glows in its own colours (1: as bright as the texture is)
            /// true when it gives off light of its own, one way or the other
            public bool Glows { get { return GlowMap ? Tenths(GlowStrength) > 0 : Hex(Glow) != "000000"; } }
            public bool NoShadow;       // casts no shadow
            /// true when the game's own shader can draw it (see the top of the file)
            public bool Plain { get { return Hex(Tint) == "FFFFFF" && !Glows && Q(Alpha) == 100 && !Unlit; } }
        }

        /// on a model that has a look, or has had one (then Slot is -1 and Key is null)
        public class Look : MonoBehaviour
        {
            public Values V; public int Slot = -1; public string Key; public int World;
            void OnDestroy() { Release(this); }
        }

        class SlotInfo { public int Index, Users; }
        static readonly Dictionary<string, SlotInfo> byKey = new Dictionary<string, SlotInfo>();
        static readonly Queue<KeyValuePair<int, float>> spare = new Queue<KeyValuePair<int, float>>();     // slots nobody uses, and when each was let go
        const float RestSeconds = 1f;
        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();   // a model's own texture, by model name
        static readonly List<Material> made = new List<Material>();     // each look slot's material (by slot - FirstSlot): made once, changed for each look that uses the slot
        static int next, world;                                         // the next unused slot; which world the looks belong to
        static SceneGraph graph; static Shader standard;

        static readonly Type serializer = AccessTools.Inner(typeof(SceneGraph), "Serializer");
        static readonly FieldInfo fSaved = AccessTools.Field(serializer, "savedObjectCount");
        static readonly FieldInfo fFreeCells = AccessTools.Field(typeof(SceneGraph), "freeCells");
        static readonly MethodInfo mFlag = AccessTools.Method(typeof(SceneGraph), "FlagCellsForUpdate");
        static readonly List<string> saving = new List<string>();
        static Dictionary<int, Transform> loaded;
        static GameObject batching;

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(Looks); var g = typeof(SceneGraph);
            h.Patch(AccessTools.Method(g, "Clear"), null, new HarmonyMethod(me, "Cleared"));
            // a model being carried is drawn by the game's own routine: tell it the slot
            // (both are undone in a "finalizer", which also runs when the game's routine fails part-way: the model's
            //  usual slot must be put back whatever happens, because every model of that kind shares it)
            h.Patch(AccessTools.Method(g, "RecursivelyBatchSubGraph"), new HarmonyMethod(me, "Batching"), null, null, new HarmonyMethod(me, "BatchingDone"), null);
            h.Patch(AccessTools.Method(AccessTools.Inner(g, "Cell"), "Add"), new HarmonyMethod(me, "AddBefore"), null, null, new HarmonyMethod(me, "AddDone"), null);
            // saving and loading
            h.Patch(AccessTools.Method(serializer, "SaveToByteArray"), new HarmonyMethod(me, "SaveStarts"));
            h.Patch(AccessTools.Method(serializer, "SaveObjectV1"), new HarmonyMethod(me, "SavingObject"));
            h.Patch(AccessTools.Method(g, "Load"), null, new HarmonyMethod(me, "Loading") { priority = Priority.First });    // (first: it must hold the game's own routine, not another module's wrapper)
            // Object Copying: the game sets its ghosts out again, then makes the copy and puts it in the world
            h.Patch(AccessTools.Method(typeof(PaintingProxies), "SetObject"), new HarmonyMethod(me, "CopyComing"));
            h.Patch(AccessTools.Method(g, "AttachNode"), null, new HarmonyMethod(me, "Attached"));
            WorldFile.BeforeSave += delegate { WorldFile.SetText("looks", saving.Count > 0 ? string.Join(";", saving.ToArray()) : null); };
            WorldFile.AfterLoad += ApplyLoaded;
        }
        public void SceneLoaded(string scene) { graph = null; }
        public void Tick() { }

        // ---- reading and changing a model's look
        static SceneGraph Graph()
        {
            if (graph == null) foreach (var g in Resources.FindObjectsOfTypeAll<SceneGraph>()) if (g.gameObject.scene.IsValid() && g.materials != null && g.materials.Count >= 7) graph = g;
            return graph;
        }

        static MeshData Model(GameObject node) { return node != null ? MeshDataManager.GetInstance().GetMeshData(node.name) : null; }

        /// what the game itself draws this model with
        public static Values Usual(GameObject node) { return Usual(Model(node)); }
        static Values Usual(MeshData data)
        {
            var v = new Values { Smooth = 0.5f, Metal = 0f, Tint = Color.white, Glow = Color.black, Alpha = 1f, GlowStrength = 1f };
            try
            {
                var g = Graph();
                Material usual = data != null && g != null && data.materialIndex < g.materials.Count ? g.materials[data.materialIndex] : null;
                if (usual != null && usual.HasProperty("_Glossiness")) { v.Smooth = usual.GetFloat("_Glossiness"); v.Metal = usual.GetFloat("_Metallic"); }
            }
            catch (Exception) { }
            return v;
        }

        public static Values Read(GameObject node)
        {
            var look = node != null ? node.GetComponent<Look>() : null;
            return look != null && look.Slot >= 0 ? look.V : Usual(node);
        }

        /// give a model a look (values that are the game's own take the look away again); `redraw` false when many are set at once
        public static void Write(GameObject node, Values v, bool redraw = true)
        {
            try
            {
                MeshData data = Model(node);
                if (data == null || Graph() == null) return;
                var look = node.GetComponent<Look>();
                // (a note left over from a world that has been closed: its slot went with that world)
                if (look != null && look.World != world) { look.Key = null; look.Slot = -1; look.World = world; }
                string key = KeyOf(v, Usual(data), data);
                if (key == null)
                {
                    // The look is taken away, but its note stays on the model, empty. (It used to be destroyed, which
                    // only happens at the end of the frame: a look given again within that frame went onto the dying
                    // note and was lost with it, and the count of looks went wrong.)
                    if (look == null || look.Key == null) return;
                    Release(look); look.Slot = -1;
                }
                else
                {
                    if (look == null) { look = node.AddComponent<Look>(); look.World = world; }
                    look.V = v;
                    if (key != look.Key)
                    {
                        SlotInfo slot;
                        if (!byKey.TryGetValue(key, out slot)) slot = Make(key, v, data);     // (before the old one is let go, so that it is not handed straight back)
                        slot.Users++;
                        if (look.Key == null) Count++; else Free(look.Key);                    // (counted only once it really has a slot)
                        look.Key = key; look.Slot = slot.Index;
                    }
                }
                Unsaved.Mark();
                if (redraw) Redraw(node);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Looks: " + e); }
        }

        public static void Clear(GameObject node) { if (node != null) Write(node, Usual(node)); }

        /// the slot a model is drawn in: its look's, or the usual one
        public static int SlotOf(GameObject node, int usual)
        {
            if (Count == 0) return usual;
            // (what the renderer finds in a cell is the model's selection box, a child of the model itself)
            var look = node.GetComponent<Look>();
            if (look == null && node.GetComponent<MeshCollider>() == null && node.transform.parent != null) look = node.transform.parent.GetComponent<Look>();
            return look != null && look.Slot >= 0 ? look.Slot : usual;
        }

        /// the same for what the renderer finds in a cell (FastRebuild.cs, once for every model on every rebuild):
        /// always a model's selection box, so the look is asked of its parent straight away
        public static int SlotOfBox(GameObject box, int usual)
        {
            if (Count == 0) return usual;
            Transform parent = box.transform.parent;
            var look = parent != null ? parent.GetComponent<Look>() : null;
            return look != null && look.Slot >= 0 ? look.Slot : usual;
        }

        // ---- slots
        static int Q(float v) { return Mathf.RoundToInt(Mathf.Clamp01(v) * 100f); }
        public const float MaxGlow = 3f;
        static int Tenths(float v) { return Mathf.RoundToInt(Mathf.Clamp(v, 0f, MaxGlow) * 10f); }
        // slots whose models cast no shadow (FastRebuild.cs asks as it builds each mesh)
        static readonly HashSet<int> shadowless = new HashSet<int>();
        public static bool CastsShadow(int slot) { return shadowless.Count == 0 || !shadowless.Contains(slot); }
        static string Hex(Color c) { return Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f).ToString("X2") + Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f).ToString("X2") + Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f).ToString("X2"); }
        static Color FromHex(string s)
        {
            int n;
            if (s == null || s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return Color.white;
            return new Color(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f, 1f);
        }

        // what tells one look from another: null for the game's own look. Without a tint or glow the model's usual
        // slot is part of it (the copy shares that slot's texture stack); with one, the model itself is (its texture).
        static string KeyOf(Values v, Values usual, MeshData data)
        {
            string shadow = v.NoShadow ? "|n" : "";
            if (v.Plain) return Q(v.Smooth) == Q(usual.Smooth) && Q(v.Metal) == Q(usual.Metal) && !v.NoShadow ? null : "p|" + data.materialIndex + "|" + Q(v.Smooth) + "|" + Q(v.Metal) + shadow;
            return "s|" + data.name + "|" + Q(v.Smooth) + "|" + Q(v.Metal) + "|" + Hex(v.Tint) + "|" + Hex(v.Glow) + "|" + Q(v.Alpha) + (v.Unlit ? "|u" : "") + (v.GlowMap ? "|g" + Tenths(v.GlowStrength) : "") + shadow;
        }

        static SlotInfo Make(string key, Values v, MeshData data)
        {
            var g = Graph();
            // a slot let go of a little while ago, if there is one; otherwise a new one
            int index = spare.Count > 0 && Time.unscaledTime - spare.Peek().Value > RestSeconds ? spare.Dequeue().Key : FirstSlot + next++;
            int place = index - FirstSlot;
            if (place >= Capacity)
            {
                Capacity += Step;
                MoreItems.GrowCells();
            }

            // A slot keeps the one material it was first given, for good: the next look to use the slot changes that
            // material instead of replacing it. (Meshes already drawn hold on to their material; one that was thrown
            // away under them would show as bright pink.)
            Material usual = g.materials[data.materialIndex];
            while (made.Count <= place) made.Add(null);
            Material m = made[place];
            if (m == null) made[place] = m = new Material(usual);
            if (v.Plain)
            {
                m.shader = usual.shader;
                m.CopyPropertiesFromMaterial(usual);
                m.renderQueue = usual.renderQueue;
                m.SetFloat("_Glossiness", Mathf.Clamp01(v.Smooth)); m.SetFloat("_Metallic", Mathf.Clamp01(v.Metal));
            }
            else
            {
                if (standard == null) standard = Shader.Find("Standard");
                m.shader = standard;
                m.shaderKeywords = new string[0];
                Texture own = TextureOf(data, usual);
                bool glows = v.Glows, clear = Q(v.Alpha) < 100;
                float alpha = Mathf.Clamp01(v.Alpha);
                m.mainTexture = own;
                if (v.Unlit)
                {
                    // no light on it at all: black to the light, and its own picture (tinted) as its glow
                    m.color = new Color(0f, 0f, 0f, alpha);
                    m.SetTexture("_EmissionMap", own);
                    m.SetColor("_EmissionColor", new Color(v.Tint.r, v.Tint.g, v.Tint.b, 1f));
                    m.EnableKeyword("_EMISSION"); m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); m.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
                    m.SetFloat("_SpecularHighlights", 0f); m.SetFloat("_GlossyReflections", 0f);
                    m.SetFloat("_Glossiness", 0f); m.SetFloat("_Metallic", 0f);
                }
                else
                {
                    m.color = new Color(v.Tint.r, v.Tint.g, v.Tint.b, alpha);
                    // The glow is the shader's "emission": light the surface gives off whatever falls on it. Either one
                    // colour over the whole model (Glow Color), or - Glow From Texture - the model's own picture, so
                    // that every part glows in its own colour, as strongly as Glow Strength says (and tinted by Tint,
                    // like the model itself, so the glow and the surface stay the same colour).
                    float strength = Mathf.Clamp(v.GlowStrength, 0f, MaxGlow);
                    m.SetTexture("_EmissionMap", glows && v.GlowMap ? own : null);
                    if (glows) m.EnableKeyword("_EMISSION");
                    m.SetColor("_EmissionColor", !glows ? Color.black : v.GlowMap ? new Color(v.Tint.r * strength, v.Tint.g * strength, v.Tint.b * strength, 1f) : new Color(v.Glow.r, v.Glow.g, v.Glow.b, 1f));
                    m.SetFloat("_SpecularHighlights", 1f); m.SetFloat("_GlossyReflections", 1f);
                    m.SetFloat("_Glossiness", Mathf.Clamp01(v.Smooth)); m.SetFloat("_Metallic", Mathf.Clamp01(v.Metal));
                }
                // see-through: blended over what is behind it, drawn after everything solid
                m.SetFloat("_Mode", clear ? 2f : 0f);
                m.SetInt("_SrcBlend", (int)(clear ? UnityEngine.Rendering.BlendMode.SrcAlpha : UnityEngine.Rendering.BlendMode.One));
                m.SetInt("_DstBlend", (int)(clear ? UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : UnityEngine.Rendering.BlendMode.Zero));
                m.SetInt("_ZWrite", clear ? 0 : 1);
                if (clear) m.EnableKeyword("_ALPHABLEND_ON");
                m.renderQueue = clear ? 3000 : -1;
            }
            m.name = "TinyTownEnhanced look " + key;
            if (v.NoShadow) shadowless.Add(index); else shadowless.Remove(index);
            foreach (var each in Resources.FindObjectsOfTypeAll<SceneGraph>())
            {
                if (each.materials == null || each.materials.Count < 7) continue;
                while (each.materials.Count <= index) each.materials.Add(null);
                each.materials[index] = m;
            }
            var slot = new SlotInfo { Index = index };
            byKey[key] = slot;
            return slot;
        }

        static void Free(string key)
        {
            SlotInfo slot;
            if (key == null || !byKey.TryGetValue(key, out slot)) return;
            if (--slot.Users > 0) return;
            byKey.Remove(key);
            spare.Enqueue(new KeyValuePair<int, float>(slot.Index, Time.unscaledTime));     // (rested before it is used again: meshes drawn with it may still be on screen for a moment)
        }

        static void Release(Look look)
        {
            if (look.World != world || look.Key == null) return;     // (a model of a world that has been closed: its slots went with it)
            Free(look.Key); look.Key = null;
            Count = Mathf.Max(0, Count - 1);
        }

        // the model's texture as a texture of its own, copied (on the graphics card) out of the stack it lives in. A
        // model says which layer of the stack is its own through a number on its vertices.
        static Texture TextureOf(MeshData data, Material usual)
        {
            Texture2D own;
            if (textures.TryGetValue(data.name, out own) && own != null) return own;
            var stack = usual.mainTexture as Texture2DArray;
            if (stack == null) return usual.mainTexture;
            float mark = data.colors != null && data.colors.Length > 0 ? data.colors[0].a : 0f;
            float count = usual.HasProperty("_TextureCount") ? usual.GetFloat("_TextureCount") : 0f;
            int layer = Mathf.RoundToInt(mark * (count > 0f ? count : 255f));
            if (layer >= stack.depth) layer = Mathf.RoundToInt(mark * stack.depth);
            layer = Mathf.Clamp(layer, 0, stack.depth - 1);
            own = new Texture2D(stack.width, stack.height, stack.format, true) { name = "TinyTownEnhanced " + data.name, wrapMode = stack.wrapMode, filterMode = stack.filterMode, anisoLevel = stack.anisoLevel };
            for (int mip = 0; mip < own.mipmapCount; mip++) Graphics.CopyTexture(stack, layer, mip, own, 0, mip);
            textures[data.name] = own;
            return own;
        }

        // the world was emptied: every look and its slot goes (the models themselves are being destroyed by the game)
        static void Cleared()
        {
            world++;
            foreach (var slot in byKey.Values) spare.Enqueue(new KeyValuePair<int, float>(slot.Index, 0f));
            byKey.Clear(); Count = 0; changed.Clear(); loaded = null; shadowless.Clear();
            foreach (var t in textures.Values) if (t != null) UnityEngine.Object.Destroy(t);
            textures.Clear();
        }

        // ---- drawing
        static readonly HashSet<GameObject> changed = new HashSet<GameObject>();
        static bool waiting;

        static void Redraw(GameObject node)
        {
            changed.Add(node);
            if (!waiting) { waiting = true; Plugin.Instance.StartCoroutine(RedrawSoon()); }
        }

        static IEnumerator RedrawSoon()
        {
            yield return new WaitForSeconds(RedrawAfter);
            waiting = false;
            try
            {
                var g = Graph();
                var carried = fFreeCells.GetValue(g) as IDictionary;
                foreach (GameObject node in changed)
                {
                    if (node == null) continue;
                    if (carried != null && carried.Contains(node)) g.RebatchDetachedNode(node);       // in the hand: its own little mesh
                    else mFlag.Invoke(g, new object[] { node });                                      // in the world: its cell is built again
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Looks: " + e.Message); }
            changed.Clear();
        }

        static void Batching(GameObject node) { batching = node; }
        // (forgotten again when the routine is done with the model, so that nothing built later is taken for it)
        static void BatchingDone() { batching = null; }
        static void AddBefore(MeshData data, ref int __state)
        {
            __state = -1;
            if (Count == 0 || data == null || batching == null || batching.name != data.name) return;
            var look = batching.GetComponent<Look>();
            if (look == null || look.Slot < 0) return;
            __state = data.materialIndex; data.materialIndex = look.Slot;        // (for the length of this one call)
        }
        static void AddDone(MeshData data, int __state) { if (__state >= 0 && data != null) data.materialIndex = __state; }

        // ---- copies. The game's Object Copying makes copies of the model you last put down, knowing only which
        // model it was. `LastHeld` is that model (EditTab.cs sets it as models are picked up); a new model put into
        // the world at the place the ghosts were just set out from, in the same frame, is a copy of it.
        public static GameObject LastHeld;
        static int copyFrame = -1; static Vector3 copyPlace;
        static void CopyComing(Vector3 position) { copyFrame = Time.frameCount; copyPlace = position; }
        static void Attached(GameObject node, GameObject parent)
        {
            try
            {
                if (copyFrame != Time.frameCount || parent != null || node == null || LastHeld == null || node == LastHeld) return;
                var from = LastHeld.GetComponent<Look>();
                if (from == null || from.Slot < 0 || node.GetComponent<Look>() != null) return;
                if (Model(node) != Model(LastHeld) || (node.transform.position - copyPlace).sqrMagnitude > 0.000001f) return;
                Write(node, from.V);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Looks: " + e.Message); }
        }

        // ---- saving: the game numbers the models as it writes them
        static void SaveStarts() { saving.Clear(); }
        static void SavingObject(object __instance, GameObject node)
        {
            // (every model is asked, whatever Count says: a miscount must never cost a world its looks)
            var look = node.GetComponent<Look>();
            if (look == null || look.Slot < 0) return;
            saving.Add((int)fSaved.GetValue(__instance) + ":" + Q(look.V.Smooth) + ":" + Q(look.V.Metal) + ":" + Hex(look.V.Tint) + ":" + Hex(look.V.Glow)
                       + ":" + Tenths(look.V.GlowStrength) + ":" + Q(look.V.Alpha) + ":" + ((look.V.Unlit ? 1 : 0) + (look.V.NoShadow ? 2 : 0) + (look.V.GlowMap ? 4 : 0)));
        }

        // ---- loading: the game's loading routine keeps a table of model number -> model while it works; it is read
        // from the routine when it has finished, and used once the world's settings have been read
        static void Loading(ref IEnumerator __result) { __result = Watch(__result); }
        static IEnumerator Watch(IEnumerator game)
        {
            while (game.MoveNext()) yield return game.Current;
            loaded = null;
            bool found = false;
            foreach (FieldInfo f in game.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.FieldType == typeof(Dictionary<int, Transform>)) { found = true; loaded = f.GetValue(game) as Dictionary<int, Transform>; }
            // (the table is simply empty when there was no world file to read: only its absence is worth a line)
            if (!found) Plugin.Log.LogWarning("Looks: the loading routine's table of models was not found (looks saved with a world cannot be put back)");
        }

        static void ApplyLoaded()
        {
            string text = WorldFile.GetText("looks");
            if (string.IsNullOrEmpty(text) || loaded == null) { loaded = null; return; }
            int done = 0, lost = 0;
            foreach (string each in text.Split(';'))
            {
                string[] p = each.Split(':'); int id, s, m; Transform model;
                if (p.Length < 5 || !int.TryParse(p[0], out id) || !int.TryParse(p[1], out s) || !int.TryParse(p[2], out m)) continue;
                if (!loaded.TryGetValue(id, out model) || model == null) { lost++; continue; }
                // (the last three were added later: a world saved before them has solid, lit models that cast shadows.
                //  The first of the three is Glow Strength in tenths, used when the model glows in its own colours.
                //  A value that cannot be read keeps its default: TryParse would otherwise leave 0, a fully see-through model.)
                int alpha = 100, flags = 0, glow = 10, read;         // (glow: Glow Strength in tenths)
                if (p.Length >= 8) { if (int.TryParse(p[5], out read)) glow = read; if (int.TryParse(p[6], out read)) alpha = read; if (int.TryParse(p[7], out read)) flags = read; }
                Write(model.gameObject, new Values { Smooth = s / 100f, Metal = m / 100f, Tint = FromHex(p[3]), Glow = FromHex(p[4]), Alpha = alpha / 100f, Unlit = (flags & 1) != 0, NoShadow = (flags & 2) != 0, GlowMap = (flags & 4) != 0, GlowStrength = glow / 10f }, false);
                done++;
            }
            loaded = null;
            if (done > 0) Graph().ForceFullRebuild();
            Plugin.Log.LogInfo("Looks: " + done + " model(s) given their saved look, in " + byKey.Count + " slots" + (lost > 0 ? "; " + lost + " not found" : ""));
        }
    }
}
