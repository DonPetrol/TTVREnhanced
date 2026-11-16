// Tiny Town Enhanced - the camera's screen (the live picture on the back of the photo camera) is always bright.
// The game draws that picture with a lit material, so at night or in shade it is as dark as the camera body and hard
// to see. Here every part of the camera that shows a camera picture (a render texture) is given the picture as its own
// glow instead, on black: it then shows at full brightness whatever the light, the way Looks.cs makes a model Unlit.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class CameraScreen : IModule
    {
        public string Name { get { return "CameraScreen"; } }
        static VirtualCamera seen;
        static readonly HashSet<Material> ours = new HashSet<Material>();
        static Shader standard;

        public void Start(Plugin plugin) { }
        public void SceneLoaded(string scene) { seen = null; found = false; }
        static bool found; static int waited;

        // about twice a second: the camera's screens, made bright once each (and again if the game gives one a new material)
        public void Tick()
        {
            try
            {
                // (once a screen has been made bright the camera is only looked over again every five seconds, in case
                //  the game gives it a new material; until then, twice a second)
                if (found && ++waited < 10) return;
                waited = 0;
                if (seen == null) seen = UnityEngine.Object.FindObjectOfType<VirtualCamera>();
                if (seen == null) return;
                foreach (var r in seen.GetComponentsInChildren<Renderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (m == null || ours.Contains(m) || !(m.mainTexture is RenderTexture)) continue;
                    r.sharedMaterial = Bright(m); found = true;
                    Plugin.Log.LogDebug("CameraScreen: '" + r.name + "' made bright (was " + m.shader.name + ")");
                }
                // (a screen drawn as UI: the plain UI material is never lit)
                foreach (var img in seen.GetComponentsInChildren<RawImage>(true))
                    if (img.texture is RenderTexture && img.material != null && img.material != img.defaultMaterial && !ours.Contains(img.material))
                    {
                        Plugin.Log.LogDebug("CameraScreen: '" + img.name + "' drawn unlit (was " + img.material.shader.name + ")");
                        img.material = null;
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning("CameraScreen: " + e.Message); }
        }

        static Material Bright(Material from)
        {
            if (standard == null) standard = Shader.Find("Standard");
            var m = new Material(from) { name = from.name + " (bright)" };
            Texture picture = from.mainTexture;
            if (standard != null && m.shader != standard) m.shader = standard;
            m.shaderKeywords = new string[0];
            m.mainTexture = picture;
            m.color = Color.black;                                  // (no light on it at all)
            m.SetTexture("_EmissionMap", picture);                  // (and the picture as its glow)
            m.SetColor("_EmissionColor", Color.white);
            m.EnableKeyword("_EMISSION");
            m.SetFloat("_Glossiness", 0f); m.SetFloat("_Metallic", 0f);
            m.SetFloat("_SpecularHighlights", 0f); m.SetFloat("_GlossyReflections", 0f);
            m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); m.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            ours.Add(m);
            return m;
        }
    }
}
