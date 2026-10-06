// Tiny Town Enhanced - placing or picking up objects near heavy models no longer stutters.
// The game draws the world as a grid of cells. Whenever anything in a cell changes it rebuilds the whole cell:
// every model in it is copied, vertex by vertex, into combined meshes. With 30 heavy Workshop models in a cell that
// took about 0.2 s on every single edit.
// This module replaces that rebuild (SceneGraph.BatchCell) with the same steps, except that a big model keeps the
// mesh that was built for it for as long as it stays where it is. Only new, moved or small models are copied again.
// The meshes themselves are made by the game's own code (Batch.Add), so they are identical to before.
//
// It also makes those meshes the right size. The game only uses meshes with room for at least 15,000 vertices,
// however little goes into them, and about half of that room stays empty. Here each cell gets, per material, as few
// meshes as possible, each just big enough for its contents: less memory and fewer draw calls.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyTownEnhanced
{
    public class FastRebuild : IModule
    {
        public string Name { get { return "FastRebuild"; } }
        // "Big" = a model the game could not combine with another one of its size anyway (more than half of a
        // standard combined mesh), so keeping its mesh costs no extra memory.
        const int BigVertices = 7500, BigIndices = 13500;

        class Pin { public Batch Batch; public MeshData Data; public Matrix4x4 Matrix; public int Material; }
        static readonly Dictionary<object, Dictionary<int, Pin>> pinned = new Dictionary<object, Dictionary<int, Pin>>();   // per cell: collider id -> its kept mesh

        static FieldInfo fBatches, fX, fY, fZ, fPool, fMask, fGrid;
        static MethodInfo mBucket, mRelease;
        static bool failed;
        // the renderer's mesh pool and search mask, read once per renderer instead of for every cell
        static SceneGraph cachedGraph; static BatchPool pool; static int mask;

        public void Start(Plugin plugin)
        {
            var g = typeof(SceneGraph); var cell = AccessTools.Inner(g, "Cell");
            fBatches = AccessTools.Field(cell, "batches"); fX = AccessTools.Field(cell, "x"); fY = AccessTools.Field(cell, "y"); fZ = AccessTools.Field(cell, "z");
            fPool = AccessTools.Field(g, "batchPool"); fMask = AccessTools.Field(g, "overlapMask"); fGrid = AccessTools.Field(g, "gridCells");
            mBucket = AccessTools.Method(g, "ComputeBucketIndex"); mRelease = AccessTools.Method(g, "ReleaseCell");
            plugin.Harmony.Patch(AccessTools.Method(g, "BatchCell"), new HarmonyMethod(typeof(FastRebuild), "Prefix"));
            // a cell handed back to the game's pool forgets its kept meshes (the game reuses them for other things)
            plugin.Harmony.Patch(AccessTools.Method(cell, "Clear"), null, new HarmonyMethod(typeof(FastRebuild), "CellCleared"));
            plugin.Harmony.Patch(AccessTools.Method(typeof(BatchPool), "ReturnBatch"), new HarmonyMethod(typeof(FastRebuild), "ReturnPrefix"));
            plugin.Harmony.Patch(AccessTools.Method(g, "Awake"), null, new HarmonyMethod(typeof(FastRebuild), "GraphAwake"));
            var merge = plugin.Config.Bind("Video", "MergedMeshes", true, "Double the size of the world's mesh grid: fewer, larger meshes.");
            SettingsTabs.AddCheckbox("Video", "Larger Mesh Grid", "Doubles the world's mesh grid. Can improve FPS, but may stutter more when editing objects.",
                                     merge, delegate (bool on) { SetCellScale(on ? 2 : 1); });
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // ---- bigger grid cells ("Larger Mesh Grid" in Settings > Video, on by default)
        // Every mesh is one more thing for the processor to hand to the graphics card, for each eye and each shadow
        // layer, and that hand-over is what limits the frame rate in big worlds. Making the grid cells twice as wide
        // merges four cells' worth of small models into each mesh: far fewer meshes to hand over, at the price of
        // more to copy again when something in a cell changes. (3x and 4x were tried: no extra frame rate, and a
        // clear pause on every edit.)
        public static int CellScale = 1;
        static float baseCellSize;
        static SceneGraph world;

        static void GraphAwake(SceneGraph __instance)
        {
            world = __instance; baseCellSize = __instance.cellSize;
            __instance.cellSize = baseCellSize * CellScale;
            Plugin.Log.LogInfo("FastRebuild: grid cells " + baseCellSize + " -> " + __instance.cellSize + " world units (Larger Mesh Grid " + (CellScale > 1 ? "on" : "off") + ")");
        }

        public static void SetCellScale(int scale)
        {
            scale = Mathf.Clamp(scale, 1, 2);
            if (scale == CellScale && (world == null || Mathf.Approximately(world.cellSize, baseCellSize * scale))) return;
            CellScale = scale;
            if (world == null || baseCellSize <= 0f) return;                // applied when the world's renderer starts
            try
            {
                // hand every cell back (its meshes go with it), change the grid, and have the whole world built again
                var grid = (Array)fGrid.GetValue(world);
                foreach (IList bucket in grid)
                {
                    var cells = new object[bucket.Count]; bucket.CopyTo(cells, 0);
                    bucket.Clear();
                    foreach (var c in cells) mRelease.Invoke(world, new object[] { c });
                }
                pinned.Clear();
                world.cellSize = baseCellSize * scale;
                world.ForceFullRebuild();
                Plugin.Log.LogInfo("FastRebuild: Larger Mesh Grid " + (scale > 1 ? "on" : "off") + ", grid cells now " + world.cellSize + " world units; rebuilding the world");
            }
            catch (Exception e) { Plugin.Log.LogWarning("FastRebuild: could not change the grid: " + e.Message); }
        }

        static void CellCleared(object __instance) { pinned.Remove(__instance); }

        static bool Prefix(SceneGraph __instance, object cell)
        {
            if (failed) return true;
            try { Rebuild(__instance, cell); return false; }
            catch (Exception e)
            {
                // go back to the game's own rebuild for good. It starts by handing back the meshes still listed in the
                // cell; the ones this rebuild had taken out of the cell's lists (or just made) and not yet put back
                // are in nobody's list any more, so they are destroyed here - otherwise they would stay on screen.
                failed = true; pinned.Clear();
                try { Abandon(cell); } catch (Exception) { }
                Plugin.Log.LogError("FastRebuild switched off: " + e);
                return true;
            }
        }

        static void Abandon(object cell)
        {
            var inCell = new HashSet<Batch>();
            foreach (var list in (List<Batch>[])fBatches.GetValue(cell)) foreach (var b in list) inCell.Add(b);
            foreach (var b in loose) if (!inCell.Contains(b)) ReturnPrefix(b);      // (destroys it if it is still one of ours)
            loose.Clear();
        }

        // ---- right-sized meshes
        const int MaxVertices = 400000;                 // per mesh; meshes over 65,000 vertices use the large (32-bit) format
        static readonly HashSet<Batch> mine = new HashSet<Batch>();     // meshes made here, not taken from the game's pool

        struct Item { public MeshData Data; public Matrix4x4 Matrix; }

        // working lists of one rebuild, kept and emptied instead of being made anew for every cell (a rebuild of the
        // whole world goes through thousands of cells; rebuilds only ever run one at a time, on the main thread)
        static readonly HashSet<Batch> present = new HashSet<Batch>(), kept = new HashSet<Batch>(), oldBatches = new HashSet<Batch>();
        static readonly List<Batch> loose = new List<Batch>();          // our meshes this rebuild took out of the cell or made (see Abandon)
        static List<Batch>[] spare = new List<Batch>[0];                // right-sized meshes set aside for reuse, per material
        static List<Item>[] small = new List<Item>[0];                  // small models, per material

        /// false: ground pieces are combined into meshes of their own, which cast no shadows (set by WallOptions,
        /// which then calls RebuildWorld).
        public static bool GroundShadows = true;
        static void Shadows(Batch b, bool on) { b.go.GetComponent<MeshRenderer>().shadowCastingMode = on ? ShadowCastingMode.On : ShadowCastingMode.Off; }

        /// throw away what is kept and build every cell again (after a change to how meshes are grouped)
        public static void RebuildWorld()
        {
            pinned.Clear();
            if (world != null) world.ForceFullRebuild();
        }

        static Batch Make(int vertices, int indices, Material material, bool shadows)
        {
            var b = new Batch(vertices, (indices + 2) / 3);
            if (vertices > 65000) b.mesh.indexFormat = IndexFormat.UInt32;
            b.GameObject().SetActive(true);
            mine.Add(b); loose.Add(b);
            b.SetMaterial(material);
            if (!shadows) Shadows(b, false);        // (a new mesh casts shadows unless told otherwise)
            return b;
        }

        // exactly the same values, compared without the hidden allocation Matrix4x4.Equals(object) makes
        static bool Same(ref Matrix4x4 a, ref Matrix4x4 b)
        {
            return a.m00 == b.m00 && a.m01 == b.m01 && a.m02 == b.m02 && a.m03 == b.m03 && a.m10 == b.m10 && a.m11 == b.m11 && a.m12 == b.m12 && a.m13 == b.m13
                && a.m20 == b.m20 && a.m21 == b.m21 && a.m22 == b.m22 && a.m23 == b.m23 && a.m30 == b.m30 && a.m31 == b.m31 && a.m32 == b.m32 && a.m33 == b.m33;
        }

        // the game's pool must never keep a right-sized mesh: it could not reuse it, so it would pile up
        static bool ReturnPrefix(Batch batch)
        {
            if (!mine.Remove(batch)) return true;
            UnityEngine.Object.Destroy(batch.mesh);
            UnityEngine.Object.Destroy(batch.go);
            return false;
        }

        // a little spare room, so adding one more small object to a cell doesn't mean a new mesh
        static int Room(int need) { return need + Math.Max(256, Math.Min(need / 8, 2048)); }

        static void Rebuild(SceneGraph graph, object cell)
        {
            var batches = (List<Batch>[])fBatches.GetValue(cell);
            int cx = (int)fX.GetValue(cell), cy = (int)fY.GetValue(cell), cz = (int)fZ.GetValue(cell);
            if (!ReferenceEquals(graph, cachedGraph))
            {
                pool = (BatchPool)fPool.GetValue(graph); mask = (int)fMask.GetValue(graph);
                cachedGraph = graph;
            }
            var materials = graph.materials;
            float size = graph.cellSize, half = size * 0.5f;

            // the working lists, emptied (and given room for every material slot the cell has)
            present.Clear(); kept.Clear(); oldBatches.Clear(); loose.Clear();
            if (spare.Length < batches.Length) { Array.Resize(ref spare, batches.Length); Array.Resize(ref small, batches.Length); }
            for (int i = 0; i < spare.Length; i++)
            {
                if (spare[i] != null) spare[i].Clear();
                if (small[i] != null) small[i].Clear();
            }

            // 1) empty the cell, but hold on to the meshes kept for big models, and set aside right-sized ones for reuse
            Dictionary<int, Pin> old;
            if (!pinned.TryGetValue(cell, out old)) old = null;
            if (old != null) foreach (var p in old.Values) oldBatches.Add(p.Batch);
            for (int i = 0; i < batches.Length; i++)
            {
                foreach (var b in batches[i])
                    if (old != null && oldBatches.Contains(b)) { present.Add(b); loose.Add(b); }
                    else if (mine.Contains(b)) { (spare[i] ?? (spare[i] = new List<Batch>())).Add(b); loose.Add(b); }
                    else pool.ReturnBatch(b);
                batches[i].Clear();
            }

            // 2) everything whose centre is inside this cell (the same search the game does)
            float minX = cx * size - half, maxX = minX + size, minZ = cz * size - half, maxZ = minZ + size;
            Vector3 centre = new Vector3(minX + half, cy * size, minZ + half);
            var hits = Physics.OverlapBox(centre, new Vector3(half * 1.01f, 10000f, half * 1.01f), Quaternion.identity, mask);
            var manager = MeshDataManager.GetInstance();
            Dictionary<int, Pin> now = null;                        // (made only if the cell has a big model)
            int count = 0;
            foreach (var c in hits)
            {
                Vector3 p = c.bounds.center;
                if (p.x < minX || p.x >= maxX || p.z < minZ || p.z >= maxZ || p.y < -10000f || p.y >= 10000f) continue;
                GameObject go = c.gameObject;
                MeshData data = manager.GetMeshData(go.name);
                if (data == null) continue;
                Matrix4x4 m = go.transform.localToWorldMatrix;
                m.m03 -= centre.x; m.m13 -= centre.y; m.m23 -= centre.z;
                int mi = Looks.SlotOfBox(go, data.materialIndex);     // (its usual material, unless the model has a look of its own: Looks.cs)
                count++;

                if (data.vertices.Length > BigVertices || data.triangles.Length > BigIndices)
                {
                    int id = go.GetInstanceID();
                    if (now == null) now = new Dictionary<int, Pin>();
                    else if (now.ContainsKey(id)) continue;
                    Pin pin;
                    if (old != null && old.TryGetValue(id, out pin) && present.Contains(pin.Batch) && pin.Data == data && pin.Material == mi && Same(ref pin.Matrix, ref m))
                    {
                        old.Remove(id);                              // unchanged: its mesh stays exactly as it is
                    }
                    else
                    {
                        // (a big ground piece follows the "ground shadows" setting like the small ones in step 3)
                        bool shadows = Looks.CastsShadow(mi) && (GroundShadows || data.type != MeshDataType.Ground);
                        pin = new Pin { Batch = Make(data.vertices.Length, data.triangles.Length, materials[mi], shadows), Data = data, Matrix = m, Material = mi };
                        pin.Batch.Add(data, m);
                    }
                    now[id] = pin; kept.Add(pin.Batch); batches[mi].Add(pin.Batch);
                    continue;
                }
                (small[mi] ?? (small[mi] = new List<Item>())).Add(new Item { Data = data, Matrix = m });
            }

            // 3) small models: combined per material
            for (int mi = 0; mi < batches.Length; mi++)
            {
                var items = small[mi];
                if (items == null || items.Count == 0) continue;
                // right-sized: as few meshes as possible, each just big enough for what goes into it
                // (with ground shadows off, ground pieces go last and start a mesh of their own)
                int firstGround = items.Count;
                if (!GroundShadows)
                {
                    var sorted = new List<Item>(items.Count);
                    foreach (var it in items) if (it.Data.type != MeshDataType.Ground) sorted.Add(it);
                    firstGround = sorted.Count;
                    foreach (var it in items) if (it.Data.type == MeshDataType.Ground) sorted.Add(it);
                    items = sorted;
                }
                int from = 0;
                while (from < items.Count)
                {
                    int v = 0, ix = 0, to = from;
                    while (to < items.Count && (to == from || (to != firstGround && v + items[to].Data.vertices.Length <= MaxVertices)))
                    {
                        v += items[to].Data.vertices.Length; ix += items[to].Data.triangles.Length; to++;
                    }
                    bool shadows = from < firstGround && Looks.CastsShadow(mi);
                    Batch b = null;
                    var mineSpare = spare[mi];
                    if (mineSpare != null)
                        for (int i = 0; i < mineSpare.Count; i++)
                        {
                            var s = mineSpare[i];
                            if (s.VertexCount >= v && s.TriangleCount >= ix && s.VertexCount <= Room(v) + 4096 && s.TriangleCount <= Room(ix) + 12288)
                            {
                                b = s; mineSpare.RemoveAt(i); b.Clear();
                                Shadows(b, shadows);                // (a reused mesh may have been built the other way)
                                break;
                            }
                        }
                    if (b == null) b = Make(Room(v), Room(ix), materials[mi], shadows);
                    for (int i = from; i < to; i++) b.Add(items[i].Data, items[i].Matrix);
                    batches[mi].Add(b);
                    from = to;
                }
                small[mi].Clear();
            }

            // 4) what is no longer needed: meshes of big models that have gone or moved, and unused right-sized ones
            //    (ReturnPrefix destroys the ones made here instead of letting the game's pool keep them)
            if (old != null)
                foreach (var p in old.Values)
                    if (present.Contains(p.Batch) && !kept.Contains(p.Batch)) pool.ReturnBatch(p.Batch);
            for (int i = 0; i < spare.Length; i++)
                if (spare[i] != null) { foreach (var b in spare[i]) pool.ReturnBatch(b); spare[i].Clear(); }
            loose.Clear();                                          // every mesh is back in the cell's lists or gone

            if (count > 0)
            {
                if (now != null) pinned[cell] = now; else pinned.Remove(cell);
                var bounds = new Bounds(Vector3.zero, new Vector3(size, size, size));
                for (int i = 0; i < batches.Length; i++)
                    foreach (var b in batches[i])
                    {
                        Transform t = b.go.transform;
                        t.localPosition = centre; t.localRotation = Quaternion.identity; t.localScale = Vector3.one;
                        if (!present.Contains(b)) b.mesh.bounds = bounds;          // a kept mesh keeps its real bounds
                        b.Finalize(cx, cy, cz, i);                                 // uploads only meshes that changed
                    }
                return;
            }

            // nothing left in this cell: take it out of the grid, as the game does
            pinned.Remove(cell);
            var grid = (Array)fGrid.GetValue(graph);
            var bucket = (IList)grid.GetValue((int)mBucket.Invoke(graph, new object[] { cx, cy, cz }));
            for (int k = 0; k < bucket.Count; k++)
            {
                object o = bucket[k];
                if ((int)fX.GetValue(o) == cx && (int)fY.GetValue(o) == cy && (int)fZ.GetValue(o) == cz) { bucket.RemoveAt(k); break; }
            }
            mRelease.Invoke(graph, new object[] { cell });
        }
    }
}
