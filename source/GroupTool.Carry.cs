// Tiny Town Enhanced - Group Tool, continued: carrying the selection.
// (One class in several files: GroupTool.cs has the description of the whole tool, the brush and the every-frame work.)
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;


namespace TinyTownEnhanced
{
    public partial class GroupTool
    {
        // ================================================================ carrying the selection
        // While carried, the others are not moved one by one. They are taken out of the world without being drawn on their
        // own, gathered under one empty stand-in for the world ("carry root"), and the game draws them all as ONE cell -
        // its own way of drawing a detached object (SceneGraph.BatchSubGraph), done once for the lot instead of once per
        // object. That cell (and the outlines) hang from a stand-in ("Carried group") that follows the held object, so a
        // frame moves one thing however many are carried. The objects themselves (and their colliders) stay where they
        // were until they are put down: moving that many colliders every frame makes the physics engine rebuild its
        // picture of the whole world each time.
        class Carried { public Matrix4x4 Offset; public Vector3 Position, Scale; public Quaternion Rotation; public bool Ground; public Bounds Box; }
        static readonly Dictionary<GameObject, Carried> carrying = new Dictionary<GameObject, Carried>();
        static PlayState.HandData carrier; static GameObject held; static Transform stand;
        static Quaternion startTurn; static bool level;        // (level: a ground piece is carried, by something that is not ground)
        static Matrix4x4 startPose;                            // the held object when it was picked up (the others are measured from it)
        static GameObject carryRoot; static object carryCell; static Transform carryCellDrawn;
        static readonly Type SG = typeof(SceneGraph);
        static readonly MethodInfo mAcquireCell = AccessTools.Method(SG, "AcquireCell"), mBatchSubGraph = AccessTools.Method(SG, "BatchSubGraph"),
            mDetachCell = AccessTools.Method(SG, "DetachCellFromGameObject"), mReleaseCell = AccessTools.Method(SG, "ReleaseCell");

        // a selected object has just been picked up: the rest of the selection is lifted out of the world to come with it
        static void PickedUp(PlayState __instance, PlayState.HandData data, GameObject go)
        {
            try
            {
                play = __instance;
                var members = Members(go);                                      // (the selection, or a group made with Create Group)
                if (go == null || members.Count < 2 || carrying.Count > 0 || lifting) return;
                var root = (Transform)fRoot.GetValue(Graph);
                startPose = go.transform.localToWorldMatrix;
                Matrix4x4 toHeld = go.transform.worldToLocalMatrix;
                // where each is around the held one is measured now; lifting them out of the world (the slow part) follows
                var wait = new List<KeyValuePair<GameObject, Carried>>();
                bool ground = false;
                foreach (var m in members)
                {
                    if (m == null || m == go || !m.activeInHierarchy || m.transform.parent != root) continue;      // (only objects standing on their own)
                    if (m.transform.IsChildOf(go.transform)) continue;
                    var md = Game.Model(m);
                    if (md == null) continue;
                    var c = new Carried { Offset = toHeld * m.transform.localToWorldMatrix, Position = m.transform.position, Rotation = m.transform.rotation, Scale = m.transform.localScale };
                    c.Ground = md.type == MeshDataType.Ground && md.generateSides;
                    c.Box = WorldBox(md.bounds, m.transform.localToWorldMatrix);
                    if (md.type == MeshDataType.Ground) ground = true;
                    wait.Add(new KeyValuePair<GameObject, Carried>(m, c));
                }
                if (wait.Count == 0) return;
                carrier = data; held = go; startTurn = go.transform.rotation;
                var lead = Game.Model(go);
                level = ground && (lead == null || lead.type != MeshDataType.Ground);       // (a ground piece held is kept level by the game)
                lifting = true;
                Busy.Run("Picking up " + (wait.Count + 1) + " objects...", wait.Count, delegate { Bulk(delegate { Lift(wait); }); }, Busy.PickUpThreshold);
            }
            catch (Exception e) { Plugin.Log.LogWarning("GroupTool (pick up): " + e); }
        }
        static bool lifting;

        // a model's box (its own bounds, placed by `where`) as a box along the world's axes
        static Bounds WorldBox(Bounds b, Matrix4x4 where)
        {
            Bounds box = new Bounds(where.MultiplyPoint3x4(b.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                box.Encapsulate(where.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return box;
        }

        static void Lift(List<KeyValuePair<GameObject, Carried>> wait)
        {
            lifting = false;
            if (carrier == null || held == null || carrier.heldObject != held) { carrier = null; held = null; return; }   // (already put down)
            var graph = Graph;
            if (stand == null)
            {
                stand = new GameObject("TTE Carried group").transform;
                UnityEngine.Object.DontDestroyOnLoad(stand.gameObject);
            }
            stand.SetParent(Singleton<WorldSettings>.Instance.DisplayWorld, false);
            Follow();
            // the stand-in for the world: at its middle, unturned, unscaled, with an empty first child (the game's drawing
            // skips each object's first child, which is its collision box)
            carryRoot = new GameObject("TTE carry root");
            new GameObject("(skipped)").transform.SetParent(carryRoot.transform, false);
            foreach (var kv in wait)
            {
                GameObject m = kv.Key; Carried c = kv.Value;
                if (m == null || !m.activeInHierarchy) continue;
                try
                {
                    if (c.Ground) Ground.Remove(m);
                    graph.DetachNode(m, false);                                 // (out of the world, not drawn on its own)
                    m.transform.SetParent(carryRoot.transform, true);
                    carrying[m] = c;
                }
                catch (Exception e) { Plugin.Log.LogWarning("GroupTool (lift): " + e.Message); }
            }
            if (carrying.Count == 0) { UnityEngine.Object.Destroy(carryRoot); carryRoot = null; carrier = null; held = null; return; }
            // all of them drawn as one, hung from the stand-in where they are around the held object
            Matrix4x4 fromStart = startPose.inverse;
            try
            {
                carryCell = mAcquireCell.Invoke(graph, null);
                mBatchSubGraph.Invoke(graph, new[] { (object)carryRoot, carryCell });
                carryCellDrawn = carryRoot.transform.Find("Cell");
                if (carryCellDrawn != null) Hang(carryCellDrawn, fromStart);
            }
            catch (Exception e) { Plugin.Log.LogWarning("GroupTool (drawing the carried group): " + e); }
            if (outlines != null) Hang(outlines, fromStart);
            Follow();
        }

        // something hung from the stand-in, placed by `offset` from the held object
        static void Hang(Transform t, Matrix4x4 offset)
        {
            Vector3 pos; Quaternion rot; Vector3 scale;
            MatrixDecomposition.DecomposeMatrix(ref offset, out pos, out rot, out scale);
            t.SetParent(stand, false);
            t.localPosition = pos; t.localRotation = rot; t.localScale = scale;
        }

        // the stand-in goes where the held object is (in the world's own space, as the cells are drawn)
        static void Follow()
        {
            if (held == null || stand == null) return;
            Matrix4x4 now = held.transform.localToWorldMatrix;
            Vector3 pos; Quaternion rot; Vector3 scale;
            MatrixDecomposition.DecomposeMatrix(ref now, out pos, out rot, out scale);
            stand.localPosition = pos; stand.localRotation = rot; stand.localScale = scale;
        }

        // every frame while carried
        static void Carry()
        {
            if (carrying.Count == 0) return;
            if (carrier == null || held == null || carrier.heldObject != held) { DropCarry(false); return; }      // (gone)
            Follow();
        }

        // A group with a ground piece in it stays level while carried and when put down: the held object (and so all of
        // them) only turns about the upright from how it was picked up, a quarter turn at a time, as a ground piece held on its own does. The game
        // places the held object every frame with SnapObject (snapped) or UnsnapObject (free); both are kept level here.
        // The turn is also kept to quarter turns (90 degrees) from how it was picked up, so the ground pieces stay lined up
        // with the ground grid, as they were.
        static Quaternion Level(Quaternion turn)
        {
            Vector3 ahead = (turn * Quaternion.Inverse(startTurn)) * Vector3.forward; ahead.y = 0f;
            if (ahead.sqrMagnitude < 1e-6f) return startTurn;
            float angle = Mathf.Atan2(ahead.x, ahead.z) * Mathf.Rad2Deg;
            angle = Mathf.Round(angle / 90f) * 90f;
            return Quaternion.AngleAxis(angle, Vector3.up) * startTurn;
        }
        static bool Levelling(PlayState.HandData data) { return level && data != null && data == carrier && held != null && data.heldObject == held; }
        static void LevelSnapped(PlayState.HandData data, ref Quaternion rotation)
        {
            if (Levelling(data)) rotation = Level(rotation);
        }
        static void LevelFree(PlayState.HandData data)
        {
            if (!Levelling(data)) return;
            Quaternion turn = Level(held.transform.rotation);
            held.transform.rotation = turn;
            if (data.batch != null) data.batch.rotation = Singleton<WorldSettings>.Instance.ToDisplayRotation(turn);
        }

        /// is this point (where it is seen) on one of the carried objects? (their colliders are left behind while carried:
        /// the point is taken back to where the group was when it was picked up, and tested against their boxes there)
        static bool OnCarried(Vector3 point)
        {
            if (stand == null) return false;
            Vector3 there = startPose.MultiplyPoint3x4(stand.InverseTransformPoint(point));
            foreach (var kv in carrying) if (kv.Value.Box.Contains(there)) return true;
            return false;
        }

        static void Place(GameObject m, Matrix4x4 where)
        {
            Vector3 pos; Quaternion rot; Vector3 scale;
            MatrixDecomposition.DecomposeMatrix(ref where, out pos, out rot, out scale);
            Transform t = m.transform;
            t.position = pos; t.rotation = rot; t.localScale = scale;
        }

        // put down: each goes back into the world where it now is (or, `placed` false, where it was before). Where each
        // goes, and the undo, are settled now; putting them back into the world (the slow part) follows through Busy.
        static List<UndoOperation> DropCarry(bool placed)
        {
            var undo = new List<UndoOperation>();
            if (carrying.Count == 0) return undo;
            var graph = Graph;
            Matrix4x4 now = held != null ? held.transform.localToWorldMatrix : Matrix4x4.identity;
            var down = new List<KeyValuePair<GameObject, Carried>>(carrying);
            Transform display = Singleton<WorldSettings>.Instance.DisplayWorld;
            foreach (var kv in down)
            {
                GameObject m = kv.Key; Carried c = kv.Value;
                if (m == null) continue;
                if (placed && held != null) Place(m, now * c.Offset);
                else { m.transform.position = c.Position; m.transform.rotation = c.Rotation; m.transform.localScale = c.Scale; }
                if (placed) undo.Add(new TransformUndoOperation(m, c.Position, c.Rotation, c.Scale, null, null, graph, play.characterManager, Ground, (int)fSpeechLayer.GetValue(play)));
            }
            // (put down where it was meant to go: Object Copying now copies the whole lot - see "copying" below)
            GameObject lead = held;
            var root = carryRoot; var cell = carryCell; var cellDrawn = carryCellDrawn;
            carryRoot = null; carryCell = null; carryCellDrawn = null;
            carrying.Clear(); carrier = null; held = null; level = false;
            if (outlines != null) { outlines.SetParent(display, false); outlines.localPosition = Vector3.zero; outlines.localRotation = Quaternion.identity; outlines.localScale = Vector3.one; }
            changes++;                                                          // (the outlines are made again where they now are)
            Busy.Run("Placing " + (down.Count + 1) + " objects...", down.Count, delegate { Bulk(delegate {
                foreach (var kv in down)
                {
                    GameObject m = kv.Key; Carried c = kv.Value;
                    if (m == null) continue;
                    try
                    {
                        graph.AttachNode(m, null);
                        if (c.Ground) Ground.Add(m);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("GroupTool (put down): " + e.Message); }
                }
                // the group's one drawing given back to the game, and the stand-in for the world gone
                try
                {
                    if (cellDrawn != null && root != null) cellDrawn.SetParent(root.transform, false);
                    if (root != null && cell != null) { mDetachCell.Invoke(graph, new object[] { root }); mReleaseCell.Invoke(graph, new[] { cell }); }
                }
                catch (Exception e) { Plugin.Log.LogWarning("GroupTool (carried drawing): " + e.Message); }
                if (root != null) UnityEngine.Object.Destroy(root);
                if (placed && lead != null)
                {
                    var rest = down.ConvertAll(kv => kv.Key); rest.RemoveAll(g => g == null);
                    if (Copied(lead, rest)) RedoGhosts();       // (the game has already set its ghosts out for the held one alone)
                }
            }); });
            return undo;
        }

    }
}
