// Tiny Town Enhanced - faster loading of Workshop models at start-up.
// The game reads and decodes every model file one after another. This module reads and decodes them all in advance
// on every CPU core, then hands each one to the game when it asks. The game's own loading code still runs unchanged,
// so items end up exactly the same. A model file is only accepted when its parts fit together (see Decode): one that
// does not would break the world's renderer the first time it is drawn.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class FastLoad : IModule
    {
        public string Name { get { return "FastLoad"; } }

        static readonly Dictionary<string, MeshData> ready = new Dictionary<string, MeshData>();
        static string subscribedDir;

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(FastLoad);
            h.Patch(AccessTools.Method(typeof(MeshDataManager), "InitializeWorkshop"), new HarmonyMethod(me, "BeforeInitialize") { priority = Priority.First });
            h.Patch(AccessTools.Method(typeof(MeshDataSerializer), "Deserialize"), new HarmonyMethod(me, "DeserializePrefix"));
            // loading is over when the game goes on to its next step: anything decoded and not asked for is let go
            h.Patch(AccessTools.Method(typeof(InitState), "CopyWorkshopWorlds"), new HarmonyMethod(me, "Finished"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // just before the game starts loading Workshop items: decode every data.bytes on all cores
        static void BeforeInitialize(SteamWorkshop.WorkshopItemInfo[] itemInfoArray)
        {
            string dir = FileUtils.GetWorkshopSubscribedDirectory();
            var names = new List<string>(); var files = new List<string>();
            foreach (var info in itemInfoArray)
            {
                if (info == null || info.isWorld) continue;
                names.Add(info.id.ToString());
                files.Add(Path.Combine(Path.Combine(dir, info.directory), "data.bytes"));     // the same path the game builds
            }
            var results = new MeshData[files.Count];
            int next = -1;
            int threads = Math.Max(1, Math.Min(Environment.ProcessorCount, files.Count));
            var workers = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                workers[t] = new Thread(delegate ()
                {
                    int i;
                    while ((i = Interlocked.Increment(ref next)) < files.Count)
                    {
                        try { results[i] = Read(names[i], files[i]); }
                        catch (Exception) { }       // left for the game's own loader, which reports it
                    }
                });
                workers[t].IsBackground = true; workers[t].Start();
            }
            foreach (var t in workers) t.Join();
            lock (ready)
            {
                ready.Clear();
                for (int i = 0; i < files.Count; i++) if (results[i] != null) ready[files[i]] = results[i];
            }
        }

        // the game asks for a model: give it the one decoded earlier. A Workshop file that wasn't decoded is read here
        // with the same safe reader, never by the game's own (see Safety.cs). Built-in models go through the game's code.
        static bool DeserializePrefix(string name, string filename, AssetBundle assetBundle, ref MeshData __result)
        {
            if (assetBundle != null || filename == null) return true;
            lock (ready)
            {
                MeshData m;
                if (ready.TryGetValue(filename, out m))
                {
                    ready.Remove(filename);
                    __result = m;
                    return false;
                }
            }
            if (!IsWorkshopFile(filename)) return true;
            try { __result = Decode(name, File.ReadAllBytes(filename)); }
            catch (Exception e) { __result = null; Plugin.Log.LogWarning("FastLoad: " + filename + " is not a valid model: " + e.Message); }
            return false;
        }

        static bool IsWorkshopFile(string file)
        {
            try
            {
                // (with the separator on the end, so that a folder whose name merely starts the same is not taken for it)
                if (subscribedDir == null) subscribedDir = Path.GetFullPath(FileUtils.GetWorkshopSubscribedDirectory()).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
                return Path.GetFullPath(file).StartsWith(subscribedDir, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }     // not known to be a Workshop file: the game's own loader has it (a built-in model must never be lost here)
        }

        static MeshData Read(string name, string file) { return Decode(name, File.ReadAllBytes(file)); }

        /// the same conversion as the game's MeshDataSerializer.Deserialize, but only model data is accepted
        public static MeshData Decode(string name, byte[] bytes)
        {
            SerializableMeshData s;
            using (var ms = new MemoryStream(bytes))
                s = (SerializableMeshData)Safety.Formatter().Deserialize(ms);
            if (s.vertices == null || s.normals == null || s.uvs == null || s.colors == null || s.triangles == null || s.height == null || s.height.Length < 4)
                throw new Exception("incomplete model data");
            // The parts must fit together: the renderer copies one normal, uv and colour for every vertex without
            // checking, so a file with fewer of any of them would stop every rebuild of the cell it is placed in.
            int count = s.vertices.Length / 3;
            if (s.vertices.Length % 3 != 0 || s.normals.Length != count * 3 || s.uvs.Length != count * 2 || s.colors.Length != count * 4 || s.triangles.Length % 3 != 0)
                throw new Exception("model data whose parts do not fit together");
            for (int i = 0; i < s.triangles.Length; i++)
                if (s.triangles[i] < 0 || s.triangles[i] >= count) throw new Exception("model data with a triangle outside its vertices");
            var m = new MeshData();
            m.name = name;
            m.vertices = new Vector3[s.vertices.Length / 3];
            for (int i = 0; i < m.vertices.Length; i++) m.vertices[i] = new Vector3(s.vertices[i * 3], s.vertices[i * 3 + 1], s.vertices[i * 3 + 2]);
            m.normals = new Vector3[s.normals.Length / 3];
            for (int i = 0; i < m.normals.Length; i++) m.normals[i] = new Vector3(s.normals[i * 3], s.normals[i * 3 + 1], s.normals[i * 3 + 2]);
            m.uvs = new Vector2[s.uvs.Length / 2];
            for (int i = 0; i < m.uvs.Length; i++) m.uvs[i] = new Vector2(s.uvs[i * 2], s.uvs[i * 2 + 1]);
            m.colors = new Color[s.colors.Length / 4];
            for (int i = 0; i < m.colors.Length; i++) m.colors[i] = new Color(s.colors[i * 4], s.colors[i * 4 + 1], s.colors[i * 4 + 2], s.colors[i * 4 + 3]);
            m.triangles = (int[])s.triangles.Clone();
            m.bounds = new Bounds(new Vector3(s.x, s.y, s.z), new Vector3(s.sx, s.sy, s.sz));
            m.materialIndex = s.materialIndex;
            m.category = s.category;
            m.filter = s.filter;
            m.type = s.type;
            m.height = new Vector4(s.height[0], s.height[1], s.height[2], s.height[3]);
            m.generateSides = s.generateSides;
            m.groundTestOverlap = s.groundTestOverlap;
            m.snapBoostDistance = s.snapBoostDistance;
            return m;
        }

        static void Finished() { lock (ready) ready.Clear(); }
    }
}
