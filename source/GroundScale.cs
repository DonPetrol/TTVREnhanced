// Tiny Town Enhanced - ground pieces can be resized, across only.
//
// Any other object is resized by holding it in one hand and pinching it with the other, then moving the hands apart or
// together (PlayState.UpdateHand, HandState.Scaling). The game leaves ground out of this in one place: while one hand
// holds a ground piece, the other hand's selection skips that piece (PlayState.GetCurrentSelection and
// GetCurrentSelectionExpanded check "is the other hand holding ground?"), so it can never be pinched. With Scale Ground
// ticked (World tab > Placement, page 2; on by default) that check is answered "no" for the moment of the selection,
// and ground is pinched and resized like everything else. Other ground pieces still come first when the pinch is inside
// them (the game sorts the held piece last), so picking up a neighbouring piece works as before.
//
// Ground is resized across only: the game makes a scale of (s, s, s); for ground it is made (s, thickness, s), keeping
// the thickness the piece had (a transpiler swaps the one "new Vector3(s, s, s)" in the scaling step for Across).
//
// The game measures a held ground piece at its original size in a few places, which is fixed here for resized pieces:
//   - snapping it to other ground (PlayState.SnapGround reads the piece's unscaled bounds)
//   - the ghost copies Object Copying offers (PaintingProxies.SetObject sizes them by the x scale alone)
//   - the outline of the walls shown under it while it is held (PickupObject and UnsnapObject size it unscaled)
// Each of these reads MeshData.bounds, which is shared by every piece of that kind: it is swapped for the held piece's
// real size only for the length of that one call, and put back straight after.
//
// Nothing about saving changes: the game already saves, loads, undoes and copies each object's scale on all three
// axes, and a world with resized ground opens in an unmodified game with the pieces at their size.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class GroundScale : IModule
    {
        public string Name { get { return "GroundScale"; } }

        static ConfigEntry<bool> on;
        /// Scale Ground: may a held ground piece be pinched and resized (the checkbox on the Placement tab)
        public static bool On
        {
            get { return on == null || on.Value; }
            set { if (on != null) on.Value = value; }
        }

        public void Start(Plugin plugin)
        {
            on = ForThisGame.Fresh(plugin.Config.Bind("Placement", "ScaleGround", true, "Ground pieces can be resized with both hands like other objects, across only (they keep their thickness)."));
            var h = plugin.Harmony; var me = typeof(GroundScale);

            // 1) let the other hand pinch a held ground piece
            foreach (var name in new[] { "GetCurrentSelection", "GetCurrentSelectionExpanded" })
                h.Patch(AccessTools.Method(typeof(PlayState), name), prefix: new HarmonyMethod(me, "SelectBefore"), finalizer: new HarmonyMethod(me, "SelectAfter"));

            // 2) resize ground across only
            h.Patch(AccessTools.Method(typeof(PlayState), "UpdateHand"), null, null, new HarmonyMethod(me, "AcrossOnly"));

            // 3) measure a resized ground piece at its real size
            h.Patch(AccessTools.Method(typeof(PlayState), "SnapGround"), prefix: new HarmonyMethod(me, "SnapBefore"), finalizer: new HarmonyMethod(me, "PutBack"));
            // (PutBack runs as the first postfix, so other postfixes on SetObject - CopyUpDown.cs - see the true bounds)
            h.Patch(AccessTools.Method(typeof(PaintingProxies), "SetObject"), prefix: new HarmonyMethod(me, "GhostsBefore"),
                    postfix: new HarmonyMethod(me, "PutBack") { priority = Priority.First }, finalizer: new HarmonyMethod(me, "PutBack"));
            foreach (var name in new[] { "PickupObject", "UnsnapObject" })
                h.Patch(AccessTools.Method(typeof(PlayState), name), null, new HarmonyMethod(me, "Outline"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static bool IsGround(MeshData data) { return data != null && data.type == MeshDataType.Ground; }

        // ---- 1) selection: the "other hand holds ground" check sees no ground for the length of the call

        static PlayState.HandData hiddenFrom; static MeshData hidden;

        static void SelectBefore(PlayState.HandData other)
        {
            if (!On || other == null || other.heldObject == null || !IsGround(other.meshData)) return;
            hidden = other.meshData; hiddenFrom = other;
            other.meshData = null;
        }
        // (a finalizer: runs however the call ends)
        static void SelectAfter()
        {
            if (hiddenFrom != null) { hiddenFrom.meshData = hidden; hiddenFrom = null; hidden = null; }
        }

        // ---- 2) the scaling step: (s, s, s) becomes (s, thickness, s) for ground

        static IEnumerable<CodeInstruction> AcrossOnly(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var scaleSpeed = AccessTools.Field(typeof(PlayState), "scaleSpeed");
            var ctor = AccessTools.Constructor(typeof(Vector3), new[] { typeof(float), typeof(float), typeof(float) });
            // the scaling step is the only place scaleSpeed is read; the first Vector3 made after that is the new scale
            int from = list.FindIndex(i => i.LoadsField(scaleSpeed));
            for (int i = from; from >= 0 && i < list.Count; i++)
            {
                var ins = list[i];
                if (!(ins.operand is ConstructorInfo) || !ctor.Equals(ins.operand)) continue;
                if (ins.opcode == OpCodes.Newobj)                // new Vector3(s, s, s) left on the stack
                {
                    list.Insert(i, new CodeInstruction(OpCodes.Ldarg_2));                       // (other: the hand holding the piece)
                    list[i + 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GroundScale), "Across")).MoveLabelsFrom(list[i + 1]);
                    return list;
                }
                if (ins.opcode == OpCodes.Call)                  // a Vector3 local filled in place: ref v, s, s, s
                {
                    list.Insert(i, new CodeInstruction(OpCodes.Ldarg_2));
                    list[i + 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GroundScale), "AcrossAt")).MoveLabelsFrom(list[i + 1]);
                    return list;
                }
            }
            Plugin.Log.LogWarning("GroundScale: the scaling step was not found; ground is resized evenly, like other objects");
            return list;
        }
        static Vector3 Across(float x, float y, float z, PlayState.HandData other)
        {
            if (other != null && other.heldObject != null && IsGround(other.meshData)) y = other.heldObject.transform.localScale.y;
            return new Vector3(x, y, z);
        }
        static void AcrossAt(ref Vector3 v, float x, float y, float z, PlayState.HandData other) { v = Across(x, y, z, other); }

        // ---- 3) a resized piece measured at its real size (MeshData.bounds swapped for one call)

        static MeshData swapped; static Bounds original;
        /// a model's own bounds, also while they are swapped for a resized piece (GroupTool.Copy.cs)
        public static Bounds TrueBounds(MeshData data) { return swapped == data && data != null ? original : data.bounds; }
        /// GroupTool.Copy.cs has taken the bounds over for this call, and puts the model's own back itself
        public static void HandOver(MeshData data) { if (swapped == data) swapped = null; }

        static void Swap(MeshData data, Vector3 by)
        {
            PutBack();
            if (data == null || by == Vector3.one) return;
            swapped = data; original = data.bounds;
            data.bounds = new Bounds(original.center, Vector3.Scale(original.size, by));
        }
        static void PutBack()
        {
            if (swapped != null) { swapped.bounds = original; swapped = null; }
        }

        // snapping: the held piece's sides are where they really are
        static void SnapBefore(PlayState.HandData data)
        {
            try { if (data != null && data.heldObject != null && IsGround(data.meshData)) Swap(data.meshData, data.heldObject.transform.localScale); }
            catch (Exception e) { PutBack(); Plugin.Log.LogWarning("GroundScale: " + e.Message); }
        }

        // Object Copying's ghosts: the game sizes them by bounds x (x scale); for a piece not scaled evenly the bounds
        // are given as size x scale / x scale, so that the game's sum comes out at the real size
        static void GhostsBefore(MeshData data, Vector3 objectScale)
        {
            try
            {
                if (data == null || objectScale.x <= 0f || (objectScale.y == objectScale.x && objectScale.z == objectScale.x)) return;
                Swap(data, objectScale / objectScale.x);
            }
            catch (Exception e) { PutBack(); Plugin.Log.LogWarning("GroundScale: " + e.Message); }
        }

        // the outline of the walls shown under a held ground piece: as wide and deep as the piece is
        static void Outline(PlayState.HandData data)
        {
            try
            {
                if (data == null || data.heldObject == null || !IsGround(data.meshData) || !data.meshData.generateSides) return;
                var preview = data.groundSidesPreview;
                if (preview == null || !preview.activeSelf) return;
                Vector3 s = data.heldObject.transform.localScale, size = preview.transform.localScale;
                if (s.x == 1f && s.z == 1f) return;
                preview.transform.localScale = new Vector3(size.x * s.x, size.y, size.z * s.z);
            }
            catch (Exception e) { Plugin.Log.LogWarning("GroundScale: " + e.Message); }
        }
    }
}
