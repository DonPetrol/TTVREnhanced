// Tiny Town Enhanced - ground edge walls no longer vanish in worlds with a lot of ground.
// The game draws every ground piece's side walls as one mesh. A mesh normally holds at most about 65,000 vertices;
// past that the walls of the whole world stop being drawn (ground pieces look like floating sheets). This module
// switches that one mesh to the large format (32-bit indices) before the game fills it. Nothing else changes.
using System.Reflection;
using System;
using HarmonyLib;
using UnityEngine.Rendering;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class GroundWalls : IModule
    {
        public string Name { get { return "GroundWalls"; } }
        /// the game's one object that holds all the walls (also used by WallOptions, below)
        internal static readonly FieldInfo sides = AccessTools.Field(typeof(GroundManager), "sides");
        static bool warned;

        public void Start(Plugin plugin)
        {
            plugin.Harmony.Patch(AccessTools.Method(typeof(GroundManager), "Batch"), new HarmonyMethod(typeof(GroundWalls), "Before"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static Mesh WallMesh(GroundManager manager)
        {
            var go = sides.GetValue(manager) as GameObject;
            var filter = go != null ? go.GetComponent<MeshFilter>() : null;
            return filter != null ? filter.sharedMesh : null;
        }

        // (the game empties and refills this same mesh on every rebuild, so it is checked each time, just before)
        static void Before(GroundManager __instance)
        {
            try
            {
                var mesh = WallMesh(__instance);
                if (mesh != null && mesh.indexFormat != IndexFormat.UInt32) mesh.indexFormat = IndexFormat.UInt32;
            }
            catch (Exception e) { Complain(e.Message); }
        }

        static void Complain(string why)
        {
            if (warned) return;
            warned = true;
            Plugin.Log.LogWarning("GroundWalls: " + why);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - ground pieces and the walls down their sides. World tab > Misc, saved with the world:
    //   Ground Shadows     off: ground pieces and their walls cast no shadows (everything else still does, also onto them)
    //   Ground Walls       on/off
    //   Ground Wall Color
    // The game keeps all walls in one object ("GroundSides"); this only changes how that object is drawn. Ground pieces
    // are drawn in meshes combined with other objects, so for shadows FastRebuild gives them meshes of their own.
    public class WallOptions : IModule
    {
        public string Name { get { return "WallOptions"; } }
        static MeshRenderer walls; static Material original, tinted;

        public void Start(Plugin plugin)
        {
            WorldTab.AddCheckbox("Misc", "groundShadows", "Ground Shadows", "Ground pieces cast shadows.", () => true, delegate { Apply(); });
            WorldTab.AddCheckbox("Misc", "walls", "Ground Walls", "Ground pieces have walls down their sides.", () => true, delegate { Apply(); });
            WorldTab.AddColor("Misc", "wallColor", "Ground Wall Color", () => new Color(0.72f, 0.74f, 0.75f), delegate { Apply(); }, () => WorldFile.GetBool("walls"));
            plugin.Harmony.Patch(AccessTools.Constructor(typeof(GroundManager), new[] { typeof(Material), typeof(Transform) }), null, new HarmonyMethod(typeof(WallOptions), "Made"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // the game has just made its ground manager, and with it the one object that holds all the walls
        static void Made(GroundManager __instance)
        {
            try
            {
                var go = GroundWalls.sides.GetValue(__instance) as GameObject;
                walls = go != null ? go.GetComponent<MeshRenderer>() : null;
                if (walls == null) return;
                original = walls.sharedMaterial; tinted = null;
                Apply();
            }
            catch (Exception e) { Plugin.Log.LogWarning("WallOptions: " + e.Message); }
        }

        static void Apply()
        {
            try
            {
                bool shadows = WorldFile.GetBool("groundShadows");
                if (FastRebuild.GroundShadows != shadows) { FastRebuild.GroundShadows = shadows; FastRebuild.RebuildWorld(); }
                if (walls == null) return;
                walls.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                walls.enabled = WorldFile.GetBool("walls");
                if (WorldFile.IsSet("wallColor")) walls.sharedMaterial = tinted = Tint.Of(original, WorldFile.GetColor("wallColor"), tinted, "ground walls");
                else walls.sharedMaterial = original;
            }
            catch (Exception e) { Plugin.Log.LogWarning("WallOptions: " + e.Message); }
        }
    }
}
