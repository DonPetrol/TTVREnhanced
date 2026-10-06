// Tiny Town Enhanced - an optional sheet of water under the whole world (World tab > Misc, saved with the world).
// The sheet is one very large flat square, drawn with the same material and the same spot of the texture as the top
// of the game's own water ground tile, so it matches water tiles placed in the world. It sits a hair below the level
// of a water tile on the bottom layer; "Water Level" moves it up or down, "Water Color" gives it a colour of its own. It is only a picture: nothing rests on it.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TinyTownEnhanced
{
    public class WaterPlane : IModule
    {
        public string Name { get { return "WaterPlane"; } }
        const float Size = 4000f;                                   // side of the square, in world units (a ground tile is 4)
        static GameObject sheet; static float level; static bool failed;
        static Material original, tinted;

        public void Start(Plugin plugin)
        {
            WorldTab.AddCheckbox("Misc", "water", "Water Plane", "A sheet of water lies under the whole world.", () => false, delegate { Apply(); });
            WorldTab.AddSlider("Misc", "waterLevel", "Water Level", -20f, 20f, 0.25f, v => v.ToString("0.00"), () => 0f, delegate { Apply(); }, () => WorldFile.GetBool("water"));
            WorldTab.AddColor("Misc", "waterColor", "Water Color", () => new Color(0.25f, 0.55f, 0.85f), delegate { Apply(); }, () => WorldFile.GetBool("water"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Apply()
        {
            try
            {
                bool on = WorldFile.GetBool("water");
                if (on && sheet == null && !failed) Build();
                if (sheet == null) return;
                sheet.SetActive(on);
                // its own colour once one has been picked; until then exactly the water tile's look
                sheet.GetComponent<MeshRenderer>().sharedMaterial = WorldFile.IsSet("waterColor") ? (tinted = Tint.Of(original, WorldFile.GetColor("waterColor"), tinted, "water plane")) : original;
                sheet.transform.localPosition = new Vector3(0f, level + WorldFile.GetFloat("waterLevel"), 0f);
            }
            catch (Exception e) { failed = true; Plugin.Log.LogWarning("WaterPlane: " + e); }
        }

        static void Build()
        {
            // the game's water ground tile: the ground piece with "water" in its name (the plainest one, if several)
            var all = AccessTools.Field(typeof(MeshDataManager), "data").GetValue(MeshDataManager.GetInstance()) as Dictionary<string, MeshData>;
            MeshData water = null; string found = "";
            foreach (var kv in all)
            {
                MeshData d = kv.Value;
                if (d == null || d.type != MeshDataType.Ground || d.name == null || d.name.IndexOf("water", StringComparison.OrdinalIgnoreCase) < 0 || d.vertices == null || d.uvs == null) continue;
                found += " " + d.name;
                if (water == null || d.name.Length < water.name.Length) water = d;
            }
            Material material = null;
            foreach (var inventory in Resources.FindObjectsOfTypeAll<Inventory>())
                if (water != null && inventory.materials != null && water.materialIndex < inventory.materials.Count) material = inventory.materials[water.materialIndex];
            var world = GameObject.Find("DisplayWorld");
            if (water == null || material == null || world == null) { failed = true; Plugin.Log.LogWarning("WaterPlane: not possible here (water tile" + (water == null ? " not" : "") + " found; candidates:" + found + ")"); return; }

            // the highest upward-facing corner of the tile: its height is the water's surface, its texture spot the water's colour
            int top = 0;
            for (int i = 0; i < water.vertices.Length; i++)
            {
                bool up = water.normals == null || i >= water.normals.Length || water.normals[i].y > 0.9f;
                if (up && water.vertices[i].y >= water.vertices[top].y) top = i;
            }
            Vector2 uv = top < water.uvs.Length ? water.uvs[top] : Vector2.zero;
            level = water.vertices[top].y - 0.02f;                  // (a hair lower, so real water tiles do not flicker against it)

            float h = Size * 0.5f;
            var mesh = new Mesh { name = "TinyTownEnhanced Water" };
            mesh.vertices = new[] { new Vector3(-h, 0f, -h), new Vector3(-h, 0f, h), new Vector3(h, 0f, h), new Vector3(h, 0f, -h) };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new[] { uv, uv, uv, uv };
            if (water.colors != null && top < water.colors.Length) { Color c = water.colors[top]; mesh.colors = new[] { c, c, c, c }; }
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();

            sheet = new GameObject("TinyTownEnhanced Water Plane");
            sheet.transform.SetParent(world.transform, false);
            sheet.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = sheet.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = original = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }
    }
}
