// Tiny Town Enhanced - you start next to the world when it opens, not far away from it.
// The game stands you a whole world-width away from the middle of the world (so a big world puts you far off), and
// it measures the world from ground pieces only (so a world without ground puts you somewhere unrelated).
// This module measures everything in the world and stands you about a metre outside its edge, facing it, with the
// ground (or the base of its contents) at the usual tabletop height.
using System;
using HarmonyLib;
using UnityEngine;
using VRTK;

namespace TinyTownEnhanced
{
    public class SpawnFix : IModule
    {
        public string Name { get { return "SpawnFix"; } }
        const float Gap = 1f;               // metres between you and the edge of the world

        public void Start(Plugin plugin)
        {
            plugin.Harmony.Patch(AccessTools.Method(typeof(MenuState), "CenterWorldInFrontOfHeadset"), new HarmonyMethod(typeof(SpawnFix), "Prefix"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static bool Prefix()
        {
            try
            {
                var ws = Singleton<WorldSettings>.Instance;
                // every placed object has a box around it; ground pieces are the ones named gr_...
                bool any = false, anyGround = false; Bounds all = default(Bounds), ground = default(Bounds);
                foreach (var box in ws.PhysicsWorld.GetComponentsInChildren<BoxCollider>())
                {
                    if (!box.enabled) continue;
                    Bounds b = box.bounds;
                    if (!any) { all = b; any = true; } else all.Encapsulate(b);
                    if (!box.name.StartsWith("gr_")) continue;
                    if (!anyGround) { ground = b; anyGround = true; } else ground.Encapsulate(b);
                }
                if (!any) return true;                                      // empty world: nothing to stand next to

                Transform head = VRTK_DeviceFinder.HeadsetTransform();
                Vector3 forward = head.forward; forward.y = 0f; forward.Normalize();
                float angle = Vector3.Angle(-Vector3.right, forward);
                if (Vector3.Cross(-Vector3.right, forward).y < 0f) angle = -angle;
                // the middle of the world; at ground level if it has ground, otherwise at the base of its contents
                Vector3 foot = new Vector3(all.center.x, anyGround ? ground.center.y : all.min.y, all.center.z);

                ws.Velocity = Vector3.zero;
                ws.Scale = 0.7f;
                ws.Rotation = 0f;
                ws.Offset = Vector3.zero;
                float distance = Mathf.Max(all.extents.x, all.extents.z) * ws.Scale + Gap;
                ws.Offset = head.position - ws.ToDisplayPosition(foot) + forward * distance - new Vector3(0f, 0.5f, 0f);
                ws.RotateAbout(ws.ToDisplayPosition(foot), angle);
                return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("SpawnFix: " + e.Message); return true; }
        }
    }
}
