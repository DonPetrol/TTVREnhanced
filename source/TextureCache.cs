// Tiny Town Enhanced - Workshop textures load from a cache instead of being decoded from PNG at every start.
// The first time an item's texture.png is loaded, its decoded pixels are saved next to it as texture.ttpcache.
// From then on the game gets those pixels directly. The cache is ignored (and rewritten) when texture.png changes,
// and it is deleted with the item's folder. Costs about 1-1.4 MB of disk per 512px item.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class TextureCache : IModule
    {
        public string Name { get { return "TextureCache"; } }
        public const string FileName = "texture.ttpcache";
        const int Magic = 0x31435454;       // "TTC1"

        static bool active; static int hits, written;
        static readonly Dictionary<int, byte[]> buffers = new Dictionary<int, byte[]>();   // one reusable buffer per data size

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(TextureCache);
            h.Patch(AccessTools.Method(typeof(MeshDataManager), "InitializeWorkshop"), new HarmonyMethod(me, "Begin"));
            h.Patch(AccessTools.Method(typeof(MeshDataManager), "LoadTexture"), new HarmonyMethod(me, "Before"), new HarmonyMethod(me, "After"));
            h.Patch(AccessTools.Method(typeof(InitState), "CopyWorkshopWorlds"), new HarmonyMethod(me, "End"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Begin() { active = true; hits = written = 0; }
        static void End()
        {
            if (!active) return;
            active = false; buffers.Clear();
            // (the caches are written in the background, so the second number is how many were handed over for writing)
            Plugin.Log.LogInfo("TextureCache: " + hits + " textures from cache, " + written + " decoded from PNG (their caches are being written)");
        }

        // the game asks for a texture: hand over the cached pixels when they match the PNG on disk
        static bool Before(string path, ref Texture2D __result, ref bool __state)
        {
            __state = false;
            if (!active || path == null || !path.EndsWith("texture.png")) return true;
            Texture2D tex = null;
            try
            {
                var png = new FileInfo(path);
                string cache = Path.Combine(png.DirectoryName, FileName);
                if (!png.Exists || !File.Exists(cache)) return true;
                using (var f = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var r = new BinaryReader(f))
                {
                    if (r.ReadInt32() != Magic || r.ReadInt64() != png.Length || r.ReadInt64() != png.LastWriteTimeUtc.Ticks) return true;
                    int w = r.ReadInt32(), h = r.ReadInt32(), format = r.ReadInt32(), len = r.ReadInt32();
                    if (len != RawSize(w, h, (TextureFormat)format) || f.Length - f.Position != len) return true;
                    byte[] buf;
                    if (!buffers.TryGetValue(len, out buf)) buffers[len] = buf = new byte[len];
                    int got = 0, n;
                    while (got < len && (n = f.Read(buf, got, len - got)) > 0) got += n;
                    if (got != len) return true;
                    tex = new Texture2D(w, h, (TextureFormat)format, true);
                    tex.LoadRawTextureData(buf);
                    tex.Apply(false, false);
                }
                __result = tex; __state = true; hits++;
                return false;
            }
            catch (Exception e)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                Plugin.Log.LogWarning("TextureCache: " + path + ": " + e.Message);
                return true;                                    // the game decodes the PNG as usual
            }
        }

        // the game decoded a PNG itself: save the pixels for next time (written in the background)
        static void After(string path, Texture2D __result, bool __state)
        {
            if (!active || __state || __result == null || path == null || !path.EndsWith("texture.png")) return;
            try
            {
                int w = __result.width, h = __result.height, format = (int)__result.format;
                byte[] raw = __result.GetRawTextureData();
                if (raw == null || raw.Length != RawSize(w, h, __result.format)) return;      // a format we don't cache
                var png = new FileInfo(path);
                long length = png.Length, ticks = png.LastWriteTimeUtc.Ticks;
                string cache = Path.Combine(png.DirectoryName, FileName);
                written++;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        string tmp = cache + ".tmp";
                        using (var f = new FileStream(tmp, FileMode.Create))
                        using (var bw = new BinaryWriter(f))
                        {
                            bw.Write(Magic); bw.Write(length); bw.Write(ticks);
                            bw.Write(w); bw.Write(h); bw.Write(format); bw.Write(raw.Length); bw.Write(raw);
                        }
                        if (File.Exists(cache)) File.Delete(cache);
                        File.Move(tmp, cache);
                    }
                    catch (Exception) { }                       // no cache this time; nothing else is affected
                });
            }
            catch (Exception e) { Plugin.Log.LogWarning("TextureCache: " + path + ": " + e.Message); }
        }

        // bytes of an uncompressed texture with all its smaller copies (mipmaps); -1 for other formats
        static int RawSize(int w, int h, TextureFormat format)
        {
            int bpp = format == TextureFormat.RGB24 ? 3 : (format == TextureFormat.ARGB32 || format == TextureFormat.RGBA32) ? 4 : 0;
            if (bpp == 0 || w < 1 || h < 1 || w > 4096 || h > 4096) return -1;
            int total = 0;
            while (true)
            {
                total += w * h * bpp;
                if (w == 1 && h == 1) break;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            return total;
        }
    }
}
