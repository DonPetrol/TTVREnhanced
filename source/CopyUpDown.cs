// Tiny Town Enhanced - Object Copying also offers a copy above and below.
// Holding the copy button shows ghost copies of the object you last placed, one on each of its four sides; touching
// a ghost makes a real object there. This module adds two more ghosts, directly above and directly below, so things
// can be stacked the same way. They follow the game's own rules: no ghost where the same object (or, for ground,
// any ground) already is, and none twice in the same place.
// Settings > Gameplay: "Copy Up and Down" (on by default).
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class CopyUpDown : IModule
    {
        public string Name { get { return "CopyUpDown"; } }
        static ConfigEntry<bool> on;
        static readonly Type T = typeof(PaintingProxies);
        static readonly FieldInfo fProxies = AccessTools.Field(T, "proxies"), fColliders = AccessTools.Field(T, "overlapColliders"), fGround = AccessTools.Field(T, "groundMask"),
                                  fPrimitive = AccessTools.Field(T, "primitiveMask"), fMaterials = AccessTools.Field(T, "materials"), fScale = AccessTools.Field(T, "scale");
        static readonly MethodInfo mTake = AccessTools.Method(T, "GetProxyFromPool");
        static bool warned;

        public void Start(Plugin plugin)
        {
            on = plugin.Config.Bind("Gameplay", "CopyUpDown", true, "Object Copying also offers a copy directly above and below.");
            SettingsTabs.AddCheckbox("Gameplay", "Copy Up and Down", "Object copying also offers a copy above and below.", on, null);
            plugin.Harmony.Patch(AccessTools.Method(T, "SetObject"), null, new HarmonyMethod(typeof(CopyUpDown), "More"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static bool Same(Vector3 a, Vector3 b) { return Mathf.Abs(a.x - b.x) < 0.0005f && Mathf.Abs(a.y - b.y) < 0.0005f && Mathf.Abs(a.z - b.z) < 0.0005f; }

        // the game has just set out its four ghosts (and hidden them until the button is held): add the two more
        static void More(PaintingProxies __instance, MeshData data, Vector3 position, Quaternion rotation, Vector3 objectScale)
        {
            if (!on.Value || data == null) return;
            try
            {
                var proxies = (List<GameObject>)fProxies.GetValue(__instance);
                var colliders = (Collider[])fColliders.GetValue(__instance);
                var materials = (Material[])fMaterials.GetValue(__instance);
                Vector3 scale = (Vector3)fScale.GetValue(__instance);
                Vector3 size = data.bounds.size * objectScale.x;
                bool ground = data.type == MeshDataType.Ground;
                var world = Singleton<WorldSettings>.Instance;

                foreach (float way in new[] { 1f, -1f })
                {
                    Vector3 at = position + rotation * Vector3.up * size.y * way;
                    // is something already there? (ground: any ground piece; otherwise: this same object, or any character)
                    int hits = Physics.OverlapBoxNonAlloc(at, size * 0.5f * (ground ? 0.98f : 0.5f), colliders, rotation, (int)(ground ? fGround : fPrimitive).GetValue(__instance));
                    bool taken = ground && hits > 0;
                    for (int i = 0; i < hits && !taken; i++)
                        if ((colliders[i].gameObject.name == data.name || data.type == MeshDataType.Character) && Same(at, colliders[i].transform.position)) taken = true;
                    if (taken) continue;
                    // or a ghost?
                    Vector3 shown = world.ToDisplayPosition(at);
                    foreach (var p in proxies) if (Vector3.Distance(p.transform.position, shown) < 0.0001f) { taken = true; break; }
                    if (taken) continue;

                    var ghost = (GameObject)mTake.Invoke(__instance, null);
                    proxies.Add(ghost);
                    Transform t = ghost.transform;
                    t.localPosition = at; t.localRotation = rotation; t.localScale = scale;
                    ghost.GetComponent<MeshRenderer>().sharedMaterial = materials[data.materialIndex];
                    ghost.GetComponent<MeshFilter>().sharedMesh = data.mesh;
                    ghost.GetComponent<BoxCollider>().size = data.bounds.size;
                    ghost.SetActive(false);                                     // (hidden with the rest until the copy button is held)
                }
            }
            catch (Exception e) { if (!warned) Plugin.Log.LogWarning("CopyUpDown: " + e); warned = true; }
        }
    }
}
