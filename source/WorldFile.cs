// Tiny Town Enhanced - settings that belong to a world and are saved inside the world's own file.
// A world is saved as one file ("data"). The game reads the parts it knows and stops, so anything after them is
// ignored by an unmodified game: such a world still loads, is still accepted by the Workshop, and simply behaves as
// it always did. This module keeps a small block of "name=value" lines there:
//     ...the game's own data...  |  the lines (UTF-8)  |  their length (4 bytes)  |  "TTPW1"
// Only settings that differ from the game's own values are written. The block is added whenever the game builds a world's
// bytes (saving and the automatic backup both do), and read back when a world has finished loading. A world saved
// by an unmodified game loses the block (the game rewrites the whole file); it then uses the game's values again.
// If a world's block is there but cannot be read (the file is busy, say), the world opens with the game's values and
// the block is written back exactly as it was with the next save, rather than being lost.
//
// A module adds a world setting with one call in its Start, giving the game's own value and what to do with a value:
//     WorldFile.AddBool("clouds", () => true, on => { ...make it so... });
//     WorldFile.AddFloat("light", () => 1f, v => { ...make it so... });
//     WorldFile.AddColor("sun", () => Color.white, c => { ...make it so... });      (stored as RRGGBB)
// and reads / changes it with GetBool / SetBool / GetFloat / SetFloat / GetColor / SetColor. Reset puts settings
// back to the game's values.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class WorldFile : IModule
    {
        public string Name { get { return "WorldFile"; } }
        static readonly byte[] Mark = Encoding.ASCII.GetBytes("TTPW1");
        // (There is no upper limit on the block's size: a limit that reading enforced and saving did not would silently
        //  drop every setting of a world that had grown past it. The length only has to fit inside the file.)

        // a setting: the game's own value (asked for each time, because some are only known once the scene is up),
        // what to do with a value, and - while the open world has changed it - the changed value, ready to use
        class Option
        {
            public string Key; public char Kind;                    // 'b'ool, 'f'loat, 'c'olour
            public Func<bool> GameBool; public Func<float> GameFloat; public Func<Color> GameColor;
            public Action<bool> ApplyBool; public Action<float> ApplyFloat; public Action<Color> ApplyColor;
            public bool Set; public bool Bool; public float Float; public Color Color;

            public void Take(string text)                           // a changed value, as written in the world file
            {
                Set = true;
                if (Kind == 'b') Bool = text == "1"; else if (Kind == 'f') Float = P(text); else Color = FromHex(text);
            }
            public bool IsGameValue(string text)
            {
                if (Kind == 'b') return (text == "1") == GameBool();
                if (Kind == 'f') return Mathf.Abs(P(text) - GameFloat()) < 0.0001f;
                return Hex(GameColor()) == text;
            }
            public void Apply()
            {
                if (Kind == 'b') ApplyBool(Set ? Bool : GameBool());
                else if (Kind == 'f') ApplyFloat(Set ? Float : GameFloat());
                else ApplyColor(Set ? Color : GameColor());
            }
        }
        static readonly List<Option> options = new List<Option>();                               // in the order they were added
        static readonly Dictionary<string, Option> byKey = new Dictionary<string, Option>();
        // the open world's changed settings as text: what is written to the file (this also carries along settings a
        // newer or older version of the mod wrote that this one does not know)
        static readonly Dictionary<string, string> values = new Dictionary<string, string>();

        static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        static float P(string s) { float v; return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f; }

        static void Add(Option o) { options.Add(o); byKey[o.Key] = o; }
        public static void AddBool(string key, Func<bool> gameValue, Action<bool> apply) { Add(new Option { Key = key, Kind = 'b', GameBool = gameValue, ApplyBool = apply }); }
        public static void AddFloat(string key, Func<float> gameValue, Action<float> apply) { Add(new Option { Key = key, Kind = 'f', GameFloat = gameValue, ApplyFloat = apply }); }
        public static void AddColor(string key, Func<Color> gameValue, Action<Color> apply) { Add(new Option { Key = key, Kind = 'c', GameColor = gameValue, ApplyColor = apply }); }

        static string Hex(Color c)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f).ToString("X2") + Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f).ToString("X2") + Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f).ToString("X2");
        }
        static Color FromHex(string s)
        {
            int n;
            if (s == null || s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return Color.white;
            return new Color(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f, 1f);
        }

        static Option Find(string key) { Option o; return byKey.TryGetValue(key, out o) ? o : null; }

        /// run just before a world's settings are written with a save: for a setting that is only worked out then
        public static event Action BeforeSave;
        /// a setting kept as plain text, for a module that looks after its meaning itself (it is not counted as a change to the world)
        public static string GetText(string key) { string text; return values.TryGetValue(key, out text) ? text : null; }
        /// (a value is one line: text with a line break in it is refused, and spaces at its two ends are not kept)
        public static void SetText(string key, string text) { if (text == null) values.Remove(key); else if (Storable(key, text)) values[key] = text; }
        /// run when a world has been loaded and its settings read
        public static event Action AfterLoad;

        // The block is "key=value" lines, so a key with '=' in it or a line break anywhere would come back as something
        // else (or as two settings) when the world is next opened. Such a write is refused, with a warning.
        static readonly char[] LineBreaks = { '\n', '\r' };
        static bool Storable(string key, string value)
        {
            if (!string.IsNullOrEmpty(key) && key.IndexOf('=') < 0 && key.IndexOfAny(LineBreaks) < 0 && (value == null || value.IndexOfAny(LineBreaks) < 0)) return true;
            Plugin.Log.LogWarning("WorldFile: the setting '" + key + "' was not stored: its name or value cannot be written as one line");
            return false;
        }

        static void Set(string key, string value)
        {
            if (!Storable(key, value)) return;
            Unsaved.Mark();
            var o = Find(key);
            if (o == null) { values[key] = value; return; }
            if (o.IsGameValue(value)) { values.Remove(key); o.Set = false; }      // back at the game's own value: nothing to store
            else { values[key] = value; o.Take(value); }
            Run(o);
        }

        // the text in "values" has changed wholesale (a world was read, emptied or reset): bring the settings in line
        static void Sync()
        {
            foreach (var o in options)
            {
                string text;
                if (values.TryGetValue(o.Key, out text)) o.Take(text); else o.Set = false;
            }
        }

        /// has this setting been changed for the open world (false: it is at the game's own value)
        public static bool IsSet(string key) { return values.ContainsKey(key); }
        // (reading a setting is cheap: these are called every frame by the fog, the stars and the clouds)
        public static bool GetBool(string key) { var o = Find(key); return o != null && (o.Set ? o.Bool : o.GameBool()); }
        public static float GetFloat(string key) { var o = Find(key); return o == null ? 0f : (o.Set ? o.Float : o.GameFloat()); }
        public static Color GetColor(string key) { var o = Find(key); return o == null ? Color.white : (o.Set ? o.Color : o.GameColor()); }
        /// change a setting of the open world; it is stored the next time the world is saved
        public static void SetBool(string key, bool value) { Set(key, value ? "1" : "0"); }
        public static void SetFloat(string key, float value) { Set(key, F(value)); }
        public static void SetColor(string key, Color value) { Set(key, Hex(value)); }

        /// put settings back to the game's own values (all of them if no keys are given)
        public static void Reset(params string[] keys)
        {
            Unsaved.Mark();
            if (keys == null || keys.Length == 0) { values.Clear(); kept = null; retry = null; }     // (everything: also a block that was only being kept)
            else foreach (string key in keys) values.Remove(key);
            ApplyAll();
        }

        public void Start(Plugin plugin)
        {
            var me = typeof(WorldFile);
            var serializer = AccessTools.Inner(typeof(SceneGraph), "Serializer");
            plugin.Harmony.Patch(AccessTools.Method(serializer, "SaveToByteArray"), null, new HarmonyMethod(me, "Saving"));
            plugin.Harmony.Patch(AccessTools.Method(typeof(SceneGraph), "Load"), null, new HarmonyMethod(me, "Loading"));
            plugin.Harmony.Patch(AccessTools.Method(typeof(SceneGraph), "Clear"), null, new HarmonyMethod(me, "Cleared"));
        }
        public void SceneLoaded(string scene) { }
        // (a block that could not be read when the world opened is asked for again, a few times: see Read)
        public void Tick()
        {
            if (retry == null) return;
            try
            {
                kept = RawBlock(retry); retry = null;
                if (kept != null) Plugin.Log.LogWarning("WorldFile: the world's settings have now been read; they will be written back unchanged with the next save");
            }
            catch (Exception) { if (--retries <= 0) retry = null; }
        }

        static void Run(Option o)
        {
            try { o.Apply(); }
            catch (Exception e) { Plugin.Log.LogWarning("WorldFile: " + o.Key + ": " + e.Message); }
        }

        static void ApplyAll() { Sync(); foreach (var o in options) Run(o); }

        // the world was emptied (a new world, or just before another is loaded): back to the game's values
        static void Cleared() { values.Clear(); kept = null; retry = null; ApplyAll(); }

        // A world whose block is there but could not be read or understood: its bytes, exactly as they were in the
        // file ("kept"), or - while even those could not be got - the file to ask again ("retry"). Such a world is
        // shown with the game's values, and a save writes the kept block back as it was instead of a new one, so one
        // failed read does not cost the world its settings.
        static byte[] kept; static string retry; static int retries;

        // the game has built the world's bytes: add our block to the end (only if something was changed)
        static void Saving(ref byte[] __result)
        {
            try
            {
                if (__result == null) return;
                if (BeforeSave != null) try { BeforeSave(); } catch (Exception e) { Plugin.Log.LogWarning("WorldFile: " + e.Message); }
                retry = null;                                               // (the file is being written anew: nothing left in it to ask for)
                byte[] all;
                if (kept != null)
                {
                    all = new byte[__result.Length + kept.Length];
                    Buffer.BlockCopy(__result, 0, all, 0, __result.Length);
                    Buffer.BlockCopy(kept, 0, all, __result.Length, kept.Length);
                    __result = all;
                    return;
                }
                if (values.Count == 0) return;
                var sb = new StringBuilder();
                foreach (var kv in values) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                byte[] lines = Encoding.UTF8.GetBytes(sb.ToString());
                all = new byte[__result.Length + lines.Length + 4 + Mark.Length];
                Buffer.BlockCopy(__result, 0, all, 0, __result.Length);
                Buffer.BlockCopy(lines, 0, all, __result.Length, lines.Length);
                Buffer.BlockCopy(BitConverter.GetBytes(lines.Length), 0, all, __result.Length + lines.Length, 4);
                Buffer.BlockCopy(Mark, 0, all, all.Length - Mark.Length, Mark.Length);
                __result = all;
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldFile: could not add the world's settings: " + e.Message); }
        }

        // a world is being loaded: once the game has finished, read our block (if the file has one)
        static void Loading(string filename, ref IEnumerator __result) { __result = Then(__result, filename); }

        static IEnumerator Then(IEnumerator inner, string filename)
        {
            while (inner.MoveNext()) yield return inner.Current;
            Read(filename);
        }

        // (one Read call may hand over fewer bytes than asked for: keep asking until they are all there)
        static void ReadAll(Stream f, byte[] into)
        {
            for (int at = 0; at < into.Length;)
            {
                int n = f.Read(into, at, into.Length - at);
                if (n <= 0) throw new EndOfStreamException("the file ended early");
                at += n;
            }
        }

        // the file's block exactly as it is stored (lines, length, mark), or null if the file has none
        static byte[] RawBlock(string filename)
        {
            if (!File.Exists(filename)) return null;
            using (var f = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int tail = Mark.Length + 4;
                if (f.Length <= tail) return null;
                var end = new byte[tail];
                f.Seek(-tail, SeekOrigin.End); ReadAll(f, end);
                for (int i = 0; i < Mark.Length; i++) if (end[4 + i] != Mark[i]) return null;
                int length = BitConverter.ToInt32(end, 0);
                if (length < 0 || (long)length + tail > f.Length)
                {
                    Plugin.Log.LogWarning("WorldFile: the world's settings in " + filename + " are damaged (their length does not fit the file) and were left out");
                    return null;
                }
                var block = new byte[length + tail];
                f.Seek(-block.Length, SeekOrigin.End); ReadAll(f, block);
                return block;
            }
        }

        static void Read(string filename)
        {
            values.Clear(); kept = null; retry = null;
            byte[] block = null;
            try { block = RawBlock(filename); }
            catch (Exception e)
            {
                retry = filename; retries = 20;
                Notices.Error("This world's saved settings (sky, lighting, looks...) could not be read: " + e.Message + ". It is shown without them.");
            }
            if (block != null)
                try
                {
                    foreach (string line in Encoding.UTF8.GetString(block, 0, block.Length - Mark.Length - 4).Split('\n'))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq)] = line.Substring(eq + 1).Trim();
                    }
                    Plugin.Log.LogInfo("WorldFile: " + values.Count + " world setting(s) read from " + filename);
                }
                catch (Exception e)
                {
                    values.Clear(); kept = block;
                    Notices.Error("This world's saved settings could not be understood: " + e.Message + ". It is shown without them; they stay in its file as they are.");
                }
            ApplyAll();
            if (AfterLoad != null) try { AfterLoad(); } catch (Exception e) { Plugin.Log.LogWarning("WorldFile: " + e.Message); }
        }
    }
}
