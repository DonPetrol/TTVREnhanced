// Tiny Town Enhanced - writes and reads .zip files (this old .NET has no zip support of its own).
// Writing: each file is compressed where the game's runtime can do so, and stored as it is where it cannot; either
// way any zip program opens the result. The zip is written under a temporary name and only given its real name when
// it is complete.
// Reading: only the files asked for are read from the zip (nothing else in it is unpacked or even loaded), each is
// checked against the checksum the zip keeps for it, and there are limits on how big they may be.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TinyTownEnhanced
{
    public static class Zip
    {
        /// write the given files into one zip; names are the paths inside the zip (with / between folders)
        public static void Write(string zipPath, List<string> files, List<string> names)
        {
            // (under another name until it is finished: a zip that could not be completed is not left looking like a good one)
            string writing = zipPath + ".tmp";
            try
            {
                using (var output = new FileStream(writing, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(output))
                {
                    var directory = new MemoryStream(); var d = new BinaryWriter(directory);
                    for (int i = 0; i < files.Count; i++)
                    {
                        byte[] raw = File.ReadAllBytes(files[i]), name = Encoding.UTF8.GetBytes(names[i]);
                        byte[] packed = Deflate(raw);
                        bool stored = packed == null || packed.Length >= raw.Length;
                        if (stored) packed = raw;
                        uint crc = Crc(raw), offset = (uint)output.Position;
                        DateTime t = File.GetLastWriteTime(files[i]);
                        ushort time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
                        ushort date = (ushort)((Math.Max(0, t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
                        ushort method = (ushort)(stored ? 0 : 8);

                        w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write(method); w.Write(time); w.Write(date);
                        w.Write(crc); w.Write((uint)packed.Length); w.Write((uint)raw.Length); w.Write((ushort)name.Length); w.Write((ushort)0);
                        w.Write(name); w.Write(packed);

                        d.Write(0x02014b50u); d.Write((ushort)20); d.Write((ushort)20); d.Write((ushort)0x0800); d.Write(method); d.Write(time); d.Write(date);
                        d.Write(crc); d.Write((uint)packed.Length); d.Write((uint)raw.Length); d.Write((ushort)name.Length); d.Write((ushort)0); d.Write((ushort)0);
                        d.Write((ushort)0); d.Write((ushort)0); d.Write(0u); d.Write(offset);
                        d.Write(name);
                    }
                    w.Flush();
                    uint start = (uint)output.Position; byte[] all = directory.ToArray();
                    w.Write(all);
                    w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)files.Count); w.Write((ushort)files.Count);
                    w.Write((uint)all.Length); w.Write(start); w.Write((ushort)0);
                }
                if (File.Exists(zipPath)) File.Delete(zipPath);
                File.Move(writing, zipPath);
            }
            catch (Exception)
            {
                try { if (File.Exists(writing)) File.Delete(writing); } catch (Exception) { }
                throw;
            }
        }

        /// The files in a zip, by their path inside it: all of them, none larger than `largest` bytes unpacked.
        public static List<KeyValuePair<string, byte[]>> Read(string zipPath, int largest) { return Read(zipPath, largest, long.MaxValue, null); }

        /// The files in a zip that `wanted` says yes to (it is asked with each file's path inside the zip; null: all
        /// of them), by that path. Reads zips made here and ordinary ones (files stored as they are, or compressed
        /// the usual way). Only the wanted files are read from the disk, so whatever else is in the zip costs nothing.
        /// Throws, with a few words that can be shown, if the zip is damaged, if a wanted file is password-protected,
        /// does not match its checksum, unpacks to more than `largest` bytes, or if together they come to more than `most`.
        public static List<KeyValuePair<string, byte[]>> Read(string zipPath, int largest, long most, Predicate<string> wanted)
        {
            var files = new List<KeyValuePair<string, byte[]>>();
            using (var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // the list of contents is at the end of the file: find where it says so
                long length = zip.Length;
                byte[] tail = Bytes(zip, Math.Max(0L, length - 22 - 65535), (int)Math.Min(length, 22L + 65535L));
                int end = -1;
                for (int i = tail.Length - 22; i >= 0; i--)
                    if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) { end = i; break; }
                if (end < 0) throw new Exception("not a zip file");
                int count = BitConverter.ToUInt16(tail, end + 10);
                long listSize = BitConverter.ToUInt32(tail, end + 12), listAt = BitConverter.ToUInt32(tail, end + 16);
                if (listAt + listSize > length || listSize > LargestList) throw new Exception("damaged zip file");
                byte[] list = Bytes(zip, listAt, (int)listSize);

                int at = 0; long total = 0;
                for (int n = 0; n < count; n++)
                {
                    if (at + 46 > list.Length || BitConverter.ToUInt32(list, at) != 0x02014b50u) throw new Exception("damaged zip file");
                    int flags = BitConverter.ToUInt16(list, at + 8), method = BitConverter.ToUInt16(list, at + 10);
                    uint crc = BitConverter.ToUInt32(list, at + 16);
                    long packed = BitConverter.ToUInt32(list, at + 20), size = BitConverter.ToUInt32(list, at + 24);
                    int nameLength = BitConverter.ToUInt16(list, at + 28), extra = BitConverter.ToUInt16(list, at + 30), comment = BitConverter.ToUInt16(list, at + 32);
                    long local = BitConverter.ToUInt32(list, at + 42);
                    if (at + 46 + nameLength > list.Length) throw new Exception("damaged zip file");
                    string name = Encoding.UTF8.GetString(list, at + 46, nameLength);
                    at += 46 + nameLength + extra + comment;
                    if (name.EndsWith("/") || (wanted != null && !wanted(name))) continue;        // a folder, or not asked for

                    if ((flags & 1) != 0) throw new Exception("it is password-protected");
                    if (method != 0 && method != 8) throw new Exception("it is compressed in a way that cannot be read here");
                    // (the sizes are the zip's own word for it: checked before anything of that size is made room for)
                    total += size;
                    if (size > largest || packed > (long)largest + 65536 || total > most) throw new Exception("the world in it is too large");
                    if (method == 0 && packed != size) throw new Exception("damaged zip file");
                    if (local + 30 > length) throw new Exception("damaged zip file");
                    byte[] head = Bytes(zip, local, 30);
                    if (BitConverter.ToUInt32(head, 0) != 0x04034b50u) throw new Exception("damaged zip file");
                    long data = local + 30 + BitConverter.ToUInt16(head, 26) + BitConverter.ToUInt16(head, 28);
                    if (data + packed > length) throw new Exception("damaged zip file");
                    byte[] bytes = Bytes(zip, data, (int)packed);
                    if (method == 8) bytes = Inflate(bytes, (int)size);
                    // (a file that is not what was zipped would otherwise become a world that cannot be opened)
                    if (Crc(bytes) != crc) throw new Exception("damaged zip file");
                    files.Add(new KeyValuePair<string, byte[]>(name, bytes));
                }
            }
            return files;
        }
        const int LargestList = 16 << 20;       // a zip's list of contents: far more than any zip of a world has

        // `count` bytes of the file from `at` on
        static byte[] Bytes(FileStream file, long at, int count)
        {
            var bytes = new byte[count]; int got = 0;
            file.Seek(at, SeekOrigin.Begin);
            while (got < count) { int read = file.Read(bytes, got, count - got); if (read <= 0) throw new Exception("damaged zip file"); got += read; }
            return bytes;
        }

        // unpack a compressed file of the given unpacked size
        static byte[] Inflate(byte[] packed, int size)
        {
            var bytes = new byte[size]; int got = 0;
            try
            {
                using (var z = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
                    while (got < size) { int read = z.Read(bytes, got, size - got); if (read <= 0) break; got += read; }
            }
            catch (Exception e)
            {
                // (the runtime's own unpacker may be missing, as its compressor may be - see Deflate - or the data is bad:
                // either way the remedy is the same, and the runtime's own message would mean nothing to a player)
                Plugin.Log.LogWarning("Zip: could not unpack: " + e.Message);
                throw new Exception("it could not be unpacked (zip it again with compression set to \"store\")");
            }
            if (got != size) throw new Exception("damaged zip file");
            return bytes;
        }

        static bool cannotDeflate;
        static byte[] Deflate(byte[] raw)
        {
            if (cannotDeflate) return null;
            try
            {
                var packed = new MemoryStream();
                using (var z = new DeflateStream(packed, CompressionMode.Compress, true)) z.Write(raw, 0, raw.Length);
                return packed.ToArray();
            }
            catch (Exception) { cannotDeflate = true; return null; }       // (the runtime's compressor is missing: store instead)
        }

        static uint[] table;
        static uint Crc(byte[] data)
        {
            if (table == null)
            {
                table = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; table[n] = c; }
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
