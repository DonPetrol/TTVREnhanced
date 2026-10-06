// Tiny Town Enhanced - no more 256-item limit for Workshop models.
// The game keeps Workshop textures in one stack per texture size (128, 256, 512px). A model finds its layer through
// an 8-bit vertex colour value, so a stack can only hold 256 layers and the game silently drops every item after that.
// This module loads the Workshop items itself (same steps as the game's MeshDataManager.InitializeWorkshop) but starts
// a new stack, with its own material, whenever one is full:
//   - the first stack of each size uses the game's own material slots 4, 5 and 6, exactly as before
//   - every extra stack gets a new slot from 19 upwards (0-18 are the game's), added to the game's material lists
// Saved worlds don't store slot numbers, so nothing in a save changes.
// Slots from 64 up belong to model looks (Looks.cs), so there is room for 45 extra stacks; a model that would need
// one more is not loaded, and a message says so.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class MoreItems : IModule
    {
        public string Name { get { return "MoreItems"; } }
        const int StackSize = 256;          // layers one stack can address
        const int GameSlots = 19;           // material slots the game itself uses (0-18)
        static readonly int[] Sizes = { 128, 256, 512 };

        class Stack { public int SizeIndex, Slot; public List<MeshData> Meshes = new List<MeshData>(); public List<Texture2D> Textures = new List<Texture2D>(); public Texture2DArray Array; }
        static readonly List<Stack> extraStacks = new List<Stack>();     // stacks beyond the first of each size

        // the game's own (private) steps and tables used here, looked up once
        static readonly Type T = typeof(MeshDataManager);
        static readonly FieldInfo fData = AccessTools.Field(T, "data");
        static readonly MethodInfo loadTexture = AccessTools.Method(T, "LoadTexture"), makeArray = AccessTools.Method(T, "CreateWorkshopTextureArray"),
                                   setLayer = AccessTools.Method(T, "UpdateColorArray"), makeMesh = AccessTools.Method(T, "CreateMeshAndPrefab"),
                                   addToCategory = AccessTools.Method(T, "AddToCategory");
        static readonly Type cellType = AccessTools.Inner(typeof(SceneGraph), "Cell");
        static readonly FieldInfo fBatches = AccessTools.Field(cellType, "batches");

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony;
            var prefix = new HarmonyMethod(typeof(MoreItems), "InitializePrefix");
            prefix.priority = Priority.Last;                              // after FastLoad has decoded the models
            h.Patch(AccessTools.Method(typeof(MeshDataManager), "InitializeWorkshop"), prefix);
            // the world renderer keeps a table with one entry per material slot, fixed at 19: make room for ours
            h.Patch(AccessTools.GetDeclaredConstructors(cellType)[0], null, new HarmonyMethod(typeof(MoreItems), "CellCreated"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static bool InitializePrefix(SteamWorkshop.WorkshopItemInfo[] itemInfoArray, ref IEnumerator __result)
        {
            __result = Load(itemInfoArray);
            return false;
        }

        static IEnumerator Load(SteamWorkshop.WorkshopItemInfo[] items)
        {
            var mgr = MeshDataManager.GetInstance();
            var data = (Dictionary<string, MeshData>)fData.GetValue(mgr);

            // 1) read every item and sort it into stacks of up to 256, per texture size (in the game's order)
            var stacks = new List<Stack>[3];
            for (int i = 0; i < 3; i++) stacks[i] = new List<Stack>();
            string dir = FileUtils.GetWorkshopSubscribedDirectory();
            int loaded = 0, skipped = 0, noRoom = 0, extra = 0;         // extra: stacks beyond the first of each size so far
            foreach (var info in items)
            {
                if (info == null || info.isWorld) continue;
                string folder = Path.Combine(dir, info.directory);
                string name = info.id.ToString();
                MeshData mesh = MeshDataSerializer.Deserialize(name, Path.Combine(folder, "data.bytes"), null);
                if (mesh == null) { Notices.Error("Workshop item \"" + info.title + "\" was not loaded: it isn't a valid model. Unsubscribe from it on Steam."); skipped++; continue; }
                mesh.displayName = info.title;
                string texFile = Path.Combine(folder, "texture.png");
                var tex = (Texture2D)loadTexture.Invoke(mgr, new object[] { texFile });
                if (tex == null) { Debug.Log("Could not find workshop texture: " + texFile); skipped++; continue; }
                int s = Array.IndexOf(Sizes, tex.width);
                if (s < 0) { Plugin.Log.LogWarning("MoreItems: " + info.title + " has a " + tex.width + "px texture (must be 128, 256 or 512), skipped"); UnityEngine.Object.Destroy(tex); skipped++; continue; }
                var list = stacks[s];
                if (list.Count == 0 || list[list.Count - 1].Meshes.Count >= StackSize)
                {
                    // a further stack needs a slot below the ones of model looks; without one the model is left out
                    // (before it is entered anywhere), rather than its stack taking over a look's slot
                    if (list.Count > 0 && GameSlots + extra >= Looks.FirstSlot) { UnityEngine.Object.Destroy(tex); noRoom++; skipped++; continue; }
                    if (list.Count > 0) extra++;
                    list.Add(new Stack { SizeIndex = s });
                }
                var stack = list[list.Count - 1];
                stack.Meshes.Add(mesh); stack.Textures.Add(tex);
                data[name] = mesh;
                addToCategory.Invoke(mgr, new object[] { name, "Workshop" });
                loaded++;
            }
            if (noRoom > 0) Notices.Error(noRoom + " Workshop models were not loaded: there are more than the game can hold (" + (Looks.FirstSlot - GameSlots + 3) * StackSize + " at most, fewer if their texture sizes are uneven).");
            if (!Headset.Idle) yield return null;

            // 2) one texture array per stack; the first of each size goes where the game expects it
            extraStacks.Clear();
            var first = new Texture2DArray[3];
            for (int s = 0; s < 3; s++)
                for (int k = 0; k < stacks[s].Count; k++)
                {
                    var st = stacks[s][k];
                    st.Array = (Texture2DArray)makeArray.Invoke(mgr, new object[] { st.Textures });
                    if (k == 0) { st.Slot = 4 + s; first[s] = st.Array; }
                    else { st.Slot = GameSlots + extraStacks.Count; extraStacks.Add(st); }
                    foreach (var t in st.Textures) UnityEngine.Object.Destroy(t);     // copied into the array: free the originals
                    st.Textures = null;
                    if (!Headset.Idle) yield return null;
                }
            AccessTools.Field(T, "workshopTextures").SetValue(mgr, first);

            // 3) each model: its material slot, its layer within the stack, then the game's own mesh + collider step
            for (int s = 0; s < 3; s++)
                foreach (var st in stacks[s])
                    for (int m = 0; m < st.Meshes.Count; m++)
                    {
                        var mesh = st.Meshes[m];
                        mesh.materialIndex = st.Slot;
                        setLayer.Invoke(mgr, new object[] { mesh, (float)m / st.Meshes.Count });
                        makeMesh.Invoke(mgr, new object[] { mesh });
                    }

            AddMaterials();
            yield return null;
            Plugin.Log.LogInfo("MoreItems: " + loaded + " Workshop models loaded (" + stacks[0].Count + " x 128px, " + stacks[1].Count + " x 256px, " + stacks[2].Count
                               + " x 512px stacks; " + extraStacks.Count + " extra), " + skipped + " skipped");
        }

        // ---- models added while the game is running (MissingItems.cs: items subscribed to from inside the game).
        // They go into new stacks of their own, so nothing already loaded changes; the next start packs everything
        // together as usual. What could not be added is written to the log.
        public static int Added;
        public static IEnumerator AddLive(List<ulong> ids)
        {
            Added = 0;
            var mgr = MeshDataManager.GetInstance();
            var data = (Dictionary<string, MeshData>)fData.GetValue(mgr);
            string dir = FileUtils.GetWorkshopSubscribedDirectory();
            var stacks = new List<Stack>();
            foreach (ulong id in ids)
            {
                string name = id.ToString(), folder = Path.Combine(dir, "wi_" + id);
                if (data.ContainsKey(name)) continue;                    // already loaded
                if (!File.Exists(Path.Combine(folder, "data.bytes"))) { Plugin.Log.LogWarning("LiveItems: " + id + " has not been downloaded (no " + folder + "\\data.bytes" + (Directory.Exists(Path.Combine(dir, "wi_world_" + id)) ? "; it is a world, not a model" : "") + ")"); continue; }
                string title = name;
                try { title = JsonUtility.FromJson<SteamWorkshop.WorkshopItemInfo>(File.ReadAllText(Path.Combine(folder, "metadata.json"))).title; } catch (Exception) { }
                MeshData mesh = MeshDataSerializer.Deserialize(name, Path.Combine(folder, "data.bytes"), null);
                if (mesh == null) { Plugin.Log.LogWarning("LiveItems: " + id + " \"" + title + "\" is not a valid model"); continue; }
                mesh.displayName = title;
                var tex = (Texture2D)loadTexture.Invoke(mgr, new object[] { Path.Combine(folder, "texture.png") });
                int s = tex != null ? Array.IndexOf(Sizes, tex.width) : -1;
                if (s < 0) { Plugin.Log.LogWarning("LiveItems: " + id + " \"" + title + "\" has " + (tex == null ? "no texture" : "a " + tex.width + "px texture")); if (tex != null) UnityEngine.Object.Destroy(tex); continue; }
                Stack stack = stacks.Find(st => st.SizeIndex == s && st.Meshes.Count < StackSize);
                if (stack == null)
                {
                    // (no slot left below the ones of model looks: the model is left out, as at start-up)
                    if (GameSlots + extraStacks.Count + stacks.Count >= Looks.FirstSlot)
                    {
                        Notices.Error("Workshop item \"" + title + "\" was not loaded: there are more Workshop models than the game can hold.");
                        UnityEngine.Object.Destroy(tex); continue;
                    }
                    stacks.Add(stack = new Stack { SizeIndex = s });
                }
                stack.Meshes.Add(mesh); stack.Textures.Add(tex);
                data[name] = mesh;
                addToCategory.Invoke(mgr, new object[] { name, "Workshop" });
                Added++;
            }
            if (stacks.Count == 0) yield break;
            yield return null;

            foreach (var st in stacks)
            {
                st.Array = (Texture2DArray)makeArray.Invoke(mgr, new object[] { st.Textures });
                st.Slot = GameSlots + extraStacks.Count; extraStacks.Add(st);
                foreach (var t in st.Textures) UnityEngine.Object.Destroy(t);
                st.Textures = null;
                for (int m = 0; m < st.Meshes.Count; m++)
                {
                    var mesh = st.Meshes[m];
                    mesh.materialIndex = st.Slot;
                    setLayer.Invoke(mgr, new object[] { mesh, (float)m / st.Meshes.Count });
                    makeMesh.Invoke(mgr, new object[] { mesh });
                }
                yield return null;
            }

            AddMaterials();

            // the renderer's cells that already exist (in use, or put by for reuse) get room for the new slots too
            GrowCells();

            // the build menu's Workshop row is made again (the step the game runs for each row when its filter changes)
            foreach (var inv in Resources.FindObjectsOfTypeAll<Inventory>())
                try
                {
                    var I = typeof(Inventory);
                    var categories = AccessTools.Field(I, "categories").GetValue(inv) as IList;
                    if (categories == null) continue;                    // not set up yet: it will list the models when it is
                    bool found = false;
                    foreach (object category in categories)
                    {
                        var C = category.GetType();
                        if ((string)AccessTools.Field(C, "name").GetValue(category) != "Workshop") continue;
                        found = true;
                        List<string> names = mgr.GetNamesInCategory("Workshop", (string)AccessTools.Field(I, "filter").GetValue(inv));
                        int perGroup = inv.numCols - 1, inGroup = inv.numRows * perGroup, columns = Mathf.CeilToInt((float)names.Count / inGroup) * perGroup;
                        AccessTools.Field(C, "meshData").SetValue(category, AccessTools.Method(I, "CreateMeshDataArray").Invoke(inv, new object[] { names, perGroup, inGroup, columns }));
                        AccessTools.Field(C, "position").SetValue(category, 0f);
                        AccessTools.Field(C, "minPositionX").SetValue(category, 0f);
                        AccessTools.Field(C, "maxPositionX").SetValue(category, columns * inv.colWidth);
                        AccessTools.Field(C, "numMeshCols").SetValue(category, columns);
                    }
                    if (!found) Plugin.Log.LogWarning("LiveItems: build menu \"" + inv.name + "\" has no Workshop row (the models can be used in the world, but not picked, until the game restarts)");
                }
                catch (Exception e) { Plugin.Log.LogWarning("LiveItems: build menu: " + e); }
        }

        // every renderer cell inside a list, table, stack or set of them
        static IEnumerable<object> Cells(object o, Type cellType)
        {
            if (o == null) yield break;
            if (o.GetType() == cellType) { yield return o; yield break; }
            IEnumerable inside = o is IDictionary ? ((IDictionary)o).Values : o as IEnumerable;
            if (inside == null) yield break;
            foreach (object item in inside) foreach (object cell in Cells(item, cellType)) yield return cell;
        }

        // give every extra stack a material (a copy of the game's Workshop material for that size) in each list the game draws from
        static void AddMaterials()
        {
            if (extraStacks.Count == 0) return;
            foreach (var g in Resources.FindObjectsOfTypeAll<SceneGraph>()) g.materials = Extend(g.materials);
            foreach (var inv in Resources.FindObjectsOfTypeAll<Inventory>()) inv.materials = Extend(inv.materials);
            var field = AccessTools.Field(typeof(PaintingProxies), "materials");
            foreach (var p in Resources.FindObjectsOfTypeAll<PaintingProxies>())
            {
                var arr = field.GetValue(p) as Material[];
                if (arr != null) field.SetValue(p, Extend(new List<Material>(arr)).ToArray());
            }
        }

        static List<Material> Extend(List<Material> mats)
        {
            if (mats == null || mats.Count < 7) return mats;             // not a list with the Workshop slots
            // each stack's material goes in the stack's own slot (slots further up, of model looks, are left as they are)
            foreach (var st in extraStacks)
            {
                while (mats.Count <= st.Slot) mats.Add(null);
                if (mats[st.Slot] != null && mats[st.Slot].mainTexture == st.Array) continue;      // (set up before)
                var m = new Material(mats[4 + st.SizeIndex]);
                m.name = "Workshop " + Sizes[st.SizeIndex] + "px stack (slot " + st.Slot + ")";
                m.mainTexture = st.Array;
                m.SetFloat("_TextureCount", st.Array.depth);
                mats[st.Slot] = m;
            }
            return mats;
        }

        /// how many material slots there are: the game's, the extra texture stacks', and (from 64) those of model looks
        public static int Slots { get { return Math.Max(GameSlots + extraStacks.Count, Looks.Capacity > 0 ? Looks.FirstSlot + Looks.Capacity : 0); } }

        /// every cell the renderer already has (in use, or put by for reuse) is given room for all the slots; how many needed it
        public static int GrowCells()
        {
            int grown = 0;
            foreach (var g in Resources.FindObjectsOfTypeAll<SceneGraph>())
                foreach (string field in new[] { "gridCells", "freeCells", "cellPool", "flaggedCells" })
                {
                    var f = AccessTools.Field(typeof(SceneGraph), field);
                    if (f == null) continue;
                    foreach (object cell in Cells(f.GetValue(g), cellType))
                    {
                        int before = ((List<Batch>[])fBatches.GetValue(cell)).Length;
                        CellCreated(cell);
                        if (((List<Batch>[])fBatches.GetValue(cell)).Length > before) grown++;
                    }
                }
            return grown;
        }

        // runs after the renderer creates one of its grid cells
        static void CellCreated(object __instance)
        {
            int want = Slots;
            if (want <= GameSlots) return;
            var f = fBatches;
            var old = (List<Batch>[])f.GetValue(__instance);
            if (old.Length >= want) return;
            var bigger = new List<Batch>[want];
            for (int i = 0; i < want; i++) bigger[i] = i < old.Length ? old[i] : new List<Batch>();
            f.SetValue(__instance, bigger);
        }
    }
}
