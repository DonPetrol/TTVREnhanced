// Tiny Town Enhanced - a Workshop model can come with a starting look of its own.
// Its maker sets it in the Workshop tool (Workshop Plus: "In-game look"), and it is published inside the item as a
// small text file, looks.txt, next to the model's data.bytes and texture.png. The game itself never reads it, so the
// item works as before without this mod. With the mod, every copy of that model starts with that look (Looks.cs: it
// is the model's "usual" look), the Edit tab shows it, and the Edit tab's Reset goes back to it.
//
// looks.txt is plain text, one setting a line (any line it does not know is passed over, any setting not given is the
// game's own):
//     smoothness=0.5        0 to 1
//     metallic=0            0 to 1
//     tint=FFFFFF           a colour, as six hex digits
//     glowColor=000000      the colour it glows (black: none)
//     glowFromTexture=false true: it glows in its own colours instead, as strongly as glowStrength says
//     glowStrength=1        0.1 to 3
//     transparency=0        0 (solid) to 0.9
//     unlit=false           true: full brightness whatever the world's light
//     castShadows=true
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace TinyTownEnhanced
{
    public static class ItemLooks
    {
        public const string FileName = "looks.txt";
        public const int Largest = 4096;                 // bytes: a looks.txt bigger than this is not read
        static readonly Dictionary<string, Looks.Values> byModel = new Dictionary<string, Looks.Values>();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static int Count { get { return byModel.Count; } }

        /// the starting look the Workshop model of this name came with, if it came with one
        public static bool TryGet(string model, out Looks.Values v)
        {
            if (model != null && byModel.TryGetValue(model, out v)) return true;
            v = default(Looks.Values);
            return false;
        }

        /// read (or forget) the starting look of the Workshop model of this name, from its item folder
        public static void Read(string model, string folder)
        {
            byModel.Remove(model);
            try
            {
                string f = Path.Combine(folder, FileName);
                if (!File.Exists(f) || new FileInfo(f).Length > Largest) return;
                Looks.Values v;
                if (Parse(File.ReadAllText(f), out v)) byModel[model] = v;
            }
            catch (Exception e) { Plugin.Log.LogWarning("ItemLooks: " + model + ": " + e.Message); }
        }

        /// the settings in a looks.txt (false when there are none it understands)
        public static bool Parse(string text, out Looks.Values v)
        {
            v = new Looks.Values { Smooth = 0.5f, Metal = 0f, Tint = Color.white, Glow = Color.black, Alpha = 1f, GlowStrength = 1f };
            if (string.IsNullOrEmpty(text)) return false;
            int understood = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant(), value = line.Substring(eq + 1).Trim();
                float f; bool b; Color c;
                switch (key)
                {
                    case "smoothness": if (Number(value, 0f, 1f, out f)) { v.Smooth = f; understood++; } break;
                    case "metallic": if (Number(value, 0f, 1f, out f)) { v.Metal = f; understood++; } break;
                    case "tint": if (Colour(value, out c)) { v.Tint = c; understood++; } break;
                    case "glowcolor": if (Colour(value, out c)) { v.Glow = c; understood++; } break;
                    case "glowfromtexture": if (bool.TryParse(value, out b)) { v.GlowMap = b; understood++; } break;
                    case "glowstrength": if (Number(value, 0.1f, Looks.MaxGlow, out f)) { v.GlowStrength = f; understood++; } break;
                    case "transparency": if (Number(value, 0f, 0.9f, out f)) { v.Alpha = 1f - f; understood++; } break;
                    case "unlit": if (bool.TryParse(value, out b)) { v.Unlit = b; understood++; } break;
                    case "castshadows": if (bool.TryParse(value, out b)) { v.NoShadow = !b; understood++; } break;
                }
            }
            return understood > 0;
        }

        static bool Number(string s, float min, float max, out float f)
        {
            if (!float.TryParse(s, NumberStyles.Float, Inv, out f) || float.IsNaN(f) || float.IsInfinity(f)) return false;
            f = Mathf.Clamp(f, min, max);
            return true;
        }

        static bool Colour(string s, out Color c)
        {
            if (s.StartsWith("#")) s = s.Substring(1);
            return Colours.TryHex(s, out c);
        }
    }
}
