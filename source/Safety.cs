// Tiny Town Enhanced - only real models and worlds are accepted from the Workshop.
// The game reads a model file with .NET's BinaryFormatter, which will build ANY object a file asks for; a crafted
// Workshop item could use that to run code on your PC. Here the reader is limited to the one type a model file is
// made of, and a download is checked before anything is written to disk.
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

namespace TinyTownEnhanced
{
    public static class Safety
    {
        public class Blocked : Exception { public Blocked(string why) : base(why) { } }

        /// a reader that refuses every type except the model data itself
        public static BinaryFormatter Formatter() { return new BinaryFormatter { Binder = new ModelOnly() }; }

        class ModelOnly : SerializationBinder
        {
            public override Type BindToType(string assemblyName, string typeName)
            {
                if (typeName == "SerializableMeshData") return typeof(SerializableMeshData);
                if (typeName == "MeshDataType") return typeof(MeshDataType);
                // plain numbers, text and arrays of them (normally not even asked about)
                switch (typeName)
                {
                    case "System.Single": return typeof(float);
                    case "System.Int32": return typeof(int);
                    case "System.Boolean": return typeof(bool);
                    case "System.String": return typeof(string);
                    case "System.Single[]": return typeof(float[]);
                    case "System.Int32[]": return typeof(int[]);
                }
                throw new SerializationException("type not allowed in a model file: " + typeName);
            }
        }

        /// Checks an unpacked download. Returns the files to save (only the ones the game uses); throws Blocked otherwise.
        public static List<KeyValuePair<string, byte[]>> Check(List<KeyValuePair<string, byte[]>> files, bool world)
        {
            var keep = new List<KeyValuePair<string, byte[]>>();
            if (world)
            {
                byte[] data = Find(files, "data"), preview = Find(files, "preview.jpg");
                if (data == null || data.Length == 0) throw new Blocked("it has no world data");
                if (!IsImage(preview)) throw new Blocked("its preview isn't an image");
                keep.Add(new KeyValuePair<string, byte[]>("data", data));
                keep.Add(new KeyValuePair<string, byte[]>("preview.jpg", preview));
            }
            else
            {
                byte[] model = Find(files, "data.bytes"), texture = Find(files, "texture.png");
                if (model == null) throw new Blocked("it has no model file");
                try { FastLoad.Decode("check", model); }
                catch (Exception) { throw new Blocked("its model file isn't valid model data"); }
                if (!IsImage(texture)) throw new Blocked("its texture isn't an image");
                keep.Add(new KeyValuePair<string, byte[]>("data.bytes", model));
                keep.Add(new KeyValuePair<string, byte[]>("texture.png", texture));
            }
            return keep;
        }

        static byte[] Find(List<KeyValuePair<string, byte[]>> files, string name)
        {
            foreach (var kv in files)
            {
                string n = kv.Key.Replace('\\', '/');
                n = n.Substring(n.LastIndexOf('/') + 1);
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return null;
        }

        // PNG or JPEG, by the first bytes
        static bool IsImage(byte[] b)
        {
            if (b == null || b.Length < 8) return false;
            return (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) || (b[0] == 0xFF && b[1] == 0xD8);
        }
    }
}
