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
                Plugin.Log.LogInfo("Tint: " + what + " uses shader " + source.shader.name + " (texture " + (was == null ? "NONE" : was.GetType().Name + " " + was.dimension) + ", colour " + (copy.HasProperty("_Color") ? "yes" : "NO") + ")");
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
