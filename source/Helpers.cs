// Tiny Town Enhanced - three small helpers used by several modules: the player's camera, remembered (Eye), a
// single-colour copy of one of the game's materials (Tint), and the mod's own folders in the game's folder, where
// worlds are exported to and imported from (Folders).
// ----------------------------------------------------------------------------------------------------
// Tiny Town Enhanced - helper: the player's camera, remembered.
// Unity's Camera.main searches the whole scene every time it is asked, which in a world of tens of thousands of
// objects takes milliseconds. Anything that needs the camera every frame asks here instead; the search is repeated
// only if the remembered camera has gone, and then at most once a second.
using UnityEngine;

namespace TinyTownEnhanced
{
    public static class Eye
    {
        static Camera camera; static float lookedAt = -10f;

        public static Camera Camera
        {
            get
            {
                if (camera != null && camera.isActiveAndEnabled) return camera;
                if (Time.unscaledTime - lookedAt < 1f) return null;
                lookedAt = Time.unscaledTime;
                camera = Camera.main;
                return camera;
            }
        }

        /// the player's head: the headset as the game knows it, or failing that the camera (null if neither is there yet)
        public static Transform Head
        {
            get
            {
                Transform head = VRTK.VRTK_DeviceFinder.HeadsetTransform();
                if (head != null) return head;
                Camera c = Camera;
                return c != null ? c.transform : null;
            }
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - helper: a setting that is not kept from one game to the next (the World tab's Placement
    // page). It is still in the config file, but goes back to its default every time the game starts.
    public static class ForThisGame
    {
        public static BepInEx.Configuration.ConfigEntry<T> Fresh<T>(BepInEx.Configuration.ConfigEntry<T> entry)
        {
            entry.Value = (T)entry.DefaultValue;
            return entry;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - helper: a colour as six hex digits (RRGGBB) and back; the text form world settings, looks and
    // Workshop items' looks.txt are all saved in.
    public static class Colours
    {
        /// the mod's blue (#00C6FF): the "Enhanced" box on the main menu, the wrist menu's header, the hands' dots and the
        /// Group Tool's brush
        public static readonly Color Accent = new Color32(0, 198, 255, 255);

        public static string Hex(Color c)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f).ToString("X2") + Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f).ToString("X2") + Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f).ToString("X2");
        }
        /// false (and white) when the text is not six hex digits
        public static bool TryHex(string s, out Color c)
        {
            c = Color.white; int n;
            if (s == null || s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out n)) return false;
            c = new Color(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f, 1f);
            return true;
        }
        public static Color FromHex(string s) { Color c; TryHex(s, out c); return c; }
        /// the same colour as RRGGBB would be written (two colours that save alike), without making the text
        public static int Rgb(Color c)
        {
            return (Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f) << 16) | (Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f) << 8) | Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - helper: the game's own parts of the open world, found once for each world instead of being
    // looked up (by reflection, or by a search of the scene) wherever they are wanted. Plugin.cs tells it when a world
    // is opened (PlayState.Enter).
    public static class Game
    {
        static readonly System.Reflection.FieldInfo fGraph = HarmonyLib.AccessTools.Field(typeof(PlayState), "graph"),
                                                    fGround = HarmonyLib.AccessTools.Field(typeof(PlayState), "groundManager");
        static PlayState play; static SceneGraph graph; static GroundManager ground;

        /// the open world's play state (null before a world has been opened; it is the same one for every world)
        public static PlayState Play { get { return play; } }
        public static SceneGraph Graph { get { if (graph == null && play != null) graph = fGraph.GetValue(play) as SceneGraph; return graph; } }
        public static GroundManager Ground { get { if (ground == null && play != null) ground = fGround.GetValue(play) as GroundManager; return ground; } }
        /// the build menu
        public static Inventory Inventory { get { return play != null ? play.inventory : null; } }
        /// which model an object in the world is (null if it is not one)
        public static MeshData Model(GameObject node) { return node != null ? MeshDataManager.GetInstance().GetMeshData(node.name) : null; }

        /// (Plugin.cs: just before PlayState.Enter)
        public static void Entered(PlayState __instance)
        {
            if (play != __instance) { play = __instance; graph = null; ground = null; }
            else ground = null;         // (the ground manager is made anew for each world)
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - helper: a copy of one of the game's materials that draws in a single chosen colour.
    // The copy keeps the game's shader (so it is lit like everything else) but swaps the texture for a plain one. If the
    // shader has a colour of its own, the texture is white and that colour is set; if not (the game's main "Custom/Shared"
    // shader has none), the plain texture itself is given the colour. The first use writes the shader's name to the log.
    public static class Tint
    {
        /// make (or update) a single-colour copy of source; pass the previous copy back in to reuse it
        public static Material Of(Material source, Color color, Material copy, string what)
        {
            color = new Color(color.r, color.g, color.b, 1f);
            if (copy == null)
            {
                copy = new Material(source) { name = "TinyTownEnhanced " + what };
                Texture was = copy.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : null;
                // (the game's main shader reads from a stack of textures; its plain stand-in must be a stack as well)
                var stack = was as Texture2DArray;
                if (stack != null) copy.SetTexture("_MainTex", new Texture2DArray(1, 1, stack.depth, TextureFormat.RGBA32, false) { name = "TinyTownEnhanced " + what });
                else if (copy.HasProperty("_MainTex")) copy.SetTexture("_MainTex", new Texture2D(1, 1) { name = "TinyTownEnhanced " + what });
                Plugin.Log.LogDebug("Tint: " + what + " uses shader " + source.shader.name + " (texture " + (was == null ? "NONE" : was.GetType().Name + " " + was.dimension) + ", colour " + (copy.HasProperty("_Color") ? "yes" : "NO") + ")");
            }
            bool own = copy.HasProperty("_Color");
            if (own) copy.SetColor("_Color", color);
            Texture plain = copy.HasProperty("_MainTex") ? copy.GetTexture("_MainTex") : null;
            Color fill = own ? Color.white : color;
            var flat = plain as Texture2D; var layers = plain as Texture2DArray;
            if (flat != null) { flat.SetPixel(0, 0, fill); flat.Apply(); }
            else if (layers != null)
            {
                var one = new[] { fill };
                for (int layer = 0; layer < layers.depth; layer++) layers.SetPixels(one, layer);
                layers.Apply();
            }
            return copy;
        }
    }

    /// The mod's own folders, in the game's folder:
    ///   TTVREnhanced/CustomWorlds/Exported   where Export (WorldPage.cs) writes a world's .zip
    ///   TTVREnhanced/CustomWorlds/Imported   where Import (CreateWorld.cs) looks for .zip files to make worlds from
    /// Both are made when the game starts, so that there is somewhere to put a zip before Import is first opened.
    public static class Folders
    {
        public const string Shown = "TTVREnhanced/CustomWorlds/";      // as written in messages
        public static string Exported { get { return Made("Exported"); } }
        public static string Imported { get { return Made("Imported"); } }
        static string Made(string name)
        {
            string folder = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "TTVREnhanced"), "CustomWorlds"), name);
            try { System.IO.Directory.CreateDirectory(folder); } catch (System.Exception) { }
            return folder;
        }
        public static void MakeAll() { Made("Exported"); Made("Imported"); }
    }
}
