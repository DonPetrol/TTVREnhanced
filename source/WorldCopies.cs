// Tiny Town Enhanced - small shared routines about world folders and files: is a world still exactly the original it
// was copied from (a Workshop download, or one of the game's ready-made worlds), are two files the same, and what
// the start of a world's file says (the models it uses, and how many objects it has).
using System;
using System.Collections.Generic;
using System.IO;

namespace TinyTownEnhanced
{
    public static class WorldCopies
    {
        public const string WorkshopPrefix = "wi_world_";       // the folder of a Workshop world: wi_world_<Steam's number>
        public const string SubscribedFile = "subscribed.txt";  // in a downloaded world's folder: when it was subscribed to

        /// (read side by side a piece at a time, stopping at the first difference: neither file is held whole)
        public static bool SameFiles(string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using (var x = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var y = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] p = new byte[65536], q = new byte[65536];
                while (true)
                {
                    int n = Fill(x, p), m = Fill(y, q);
                    if (n != m) return false;
                    if (n == 0) return true;
                    for (int i = 0; i < n; i++) if (p[i] != q[i]) return false;
                }
            }
        }
        static int Fill(Stream from, byte[] into)
        {
            int got = 0;
            while (got < into.Length) { int read = from.Read(into, got, into.Length - got); if (read <= 0) break; got += read; }
            return got;
        }

        /// The names of the models the world in this file uses, and how many objects it has: the file starts with its
        /// version, those names, and then that number. Throws if the file cannot be read as a world.
        public static List<string> Models(string file, out int objects)
        {
            using (var reader = new BinaryReader(File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                reader.ReadInt32();                                     // the file's version
                int count = reader.ReadInt32();
                if (count < 0 || count > 100000) throw new Exception("not a world file");
                var names = new List<string>(count);
                for (int i = 0; i < count; i++) names.Add(reader.ReadString());
                objects = reader.ReadInt32();
                return names;
            }
        }

        /// how many objects the world in this file has (-1 if it cannot be read)
        public static int ObjectCount(string file)
        {
            try { int objects; Models(file, out objects); return objects; }
            catch (Exception) { return -1; }
        }

        /// Is the world in `directory` still what the game copied there from `original` (a save file)? Never, once it
        /// has an autosave, a backup or a name. With `remember`, a world once found untouched stays so even if the
        /// original changes later (a Workshop world updated by its author), for as long as it gains no files.
        public static bool Untouched(string directory, string original, bool remember)
        {
            try
            {
                if (Directory.GetDirectories(directory).Length > 0) return false;
                foreach (string file in Directory.GetFiles(directory))
                {
                    string name = Path.GetFileName(file);
                    // ("played" only says the world was opened: WorldList.cs)
                    if (name != "data" && name != "preview.jpg" && name != "original" && name != WorldSort.PlayedFile) return false;
                }
                string key = Path.GetFileName(directory.TrimEnd('/', '\\'));
                bool found = Remembered.Contains(key);       // (asked first in every case: asking is also what reads the file of earlier answers)
                if (remember && found) return true;

                // (comparing reads both files: the answer is kept until either file changes, and a yes is kept from
                // one run of the game to the next - without that every ready-made world was read through at each start)
                string data = Path.Combine(directory, "data");
                if (!File.Exists(data) || !File.Exists(original)) return false;
                string stamp = new FileInfo(data).Length + " " + File.GetLastWriteTimeUtc(data).Ticks + " " + File.GetLastWriteTimeUtc(original).Ticks;
                KeyValuePair<string, bool> known;
                if (!compared.TryGetValue(data, out known) || known.Key != stamp)
                {
                    compared[data] = known = new KeyValuePair<string, bool>(stamp, SameFiles(data, original));
                    if (known.Value) unwritten = true;
                }
                if (known.Value && remember && Remembered.Add(key)) unwritten = true;
                return known.Value;
            }
            catch (Exception) { return false; }
        }
        static readonly Dictionary<string, KeyValuePair<string, bool>> compared = new Dictionary<string, KeyValuePair<string, bool>>();

        /// Write down what Untouched has found out since the last time, if anything. (Called once after a whole list
        /// of worlds has been gone through - CreateWorld.cs - rather than the file being written again for each world.)
        public static void Remember()
        {
            if (!unwritten) return;
            unwritten = false;
            try
            {
                var lines = new List<string>(Remembered);
                foreach (var each in compared) if (each.Value.Value) lines.Add(each.Key + "\t" + each.Value.Key);
                File.WriteAllLines(RememberedFile, lines.ToArray());
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorldCopies: " + e.Message); }
        }
        static bool unwritten;

        // A small file beside the mod's settings. A plain line is the name of a world folder found untouched; a line
        // with a tab in it is a save file found the same as its original, and what the two files were like then
        // (their size and dates: if either has changed since, they are compared again).
        static string RememberedFile { get { return Path.Combine(BepInEx.Paths.ConfigPath, "TinyTownEnhanced.untouched.txt"); } }
        static HashSet<string> remembered;
        static HashSet<string> Remembered
        {
            get
            {
                if (remembered == null)
                {
                    remembered = new HashSet<string>();
                    try
                    {
                        if (File.Exists(RememberedFile))
                            foreach (string line in File.ReadAllLines(RememberedFile))
                            {
                                int tab = line.IndexOf('\t');
                                if (tab > 0) compared[line.Substring(0, tab)] = new KeyValuePair<string, bool>(line.Substring(tab + 1), true);
                                else if (line.Trim().Length > 0) remembered.Add(line.Trim());
                            }
                    }
                    catch (Exception) { }
                }
                return remembered;
            }
        }
    }
}
