// Tiny Town Enhanced - Group Tool, continued: copying a group with Object Copying.
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
        // ================================================================ copying a group (Object Copying)
        // After a group has been carried and put down, the game's copy ghosts are set out around the object that was held
        // (the "lead"). Here they are spaced by the size of the whole group instead of the lead alone (MeshData.bounds is
        // what the game spaces them by: it is swapped for the group's size, measured along the lead's own axes, for just
        // as long as the ghosts are being set out). Touching a ghost makes the game copy the lead there; the rest of the
        // group is copied the same way around it, all in one undo. The copies are then the group that the next ghosts and
        // copies go by: the selection (when it was a selection) or a new group of their own (when it was one made with
        // Create Group). Each step is remembered, so undoing a copy goes back to copying the group before it.
        class Copying { public GameObject Lead; public List<GameObject> Rest; public List<Bounds> Shape; }
        static readonly List<Copying> copying = new List<Copying>();
        static Bounds? swapped; static MeshData swappedData;

        static bool Copied(GameObject lead, List<GameObject> rest)
        {
            if (lead == null || rest.Count == 0) return false;
            var md = Game.Model(lead);
            if (md == null || md.type == MeshDataType.Character) return false;     // (a character is copied on its own)
            copying.Add(new Copying { Lead = lead, Rest = rest });
            return true;
        }

        // the ghosts set out again for the object last put down, now that it is known to lead a group (only the group's
        // ghosts, none spaced by the lead alone)
        // (typeof(PlayState) and not P: P is set in GroupTool.cs, and the files' fields are set up in no fixed order, so
        //  here it could still be null)
        static readonly FieldInfo fPaint = AccessTools.Field(typeof(PlayState), "proxies"), fLastData = AccessTools.Field(typeof(PlayState), "lastPlacedMeshData"),
            fLastPos = AccessTools.Field(typeof(PlayState), "lastPlacedPosition"), fLastRot = AccessTools.Field(typeof(PlayState), "lastPlacedRotation"), fLastScale = AccessTools.Field(typeof(PlayState), "lastPlacedScale");
        static void RedoGhosts()
        {
            var paint = fPaint.GetValue(play) as PaintingProxies;
            var data = fLastData.GetValue(play) as MeshData;
            if (paint == null || data == null) return;
            Rebuilding = true;
            try { paint.Reset(); } finally { Rebuilding = false; }
            paint.SetObject(data, (Vector3)fLastPos.GetValue(play), (Quaternion)fLastRot.GetValue(play), (Vector3)fLastScale.GetValue(play));
        }
        static Copying CopyingNow()
        {
            while (copying.Count > 0)
            {
                var c = copying[copying.Count - 1];
                if (c.Lead != null && c.Lead.activeInHierarchy) return c;
                copying.RemoveAt(copying.Count - 1);                    // (undone: back to the group before)
            }
            return null;
        }
        // something else was picked up: copying goes by that now
        static void CopyingEnds() { if (!Rebuilding) copying.Clear(); }
        /// CopyUpDown.cs is setting the ghosts out again after an undo (not a new pick-up)
        public static bool Rebuilding;

        static void GhostsForGroup(MeshData data)
        {
            var c = CopyingNow();
            if (c == null || data == null || Game.Model(c.Lead) != data) return;
            // the group's size along the lead's own axes (in the lead's units, unscaled, as the game's bounds are; a
            // ground piece resized across only is measured at its real shape: GroundScale.cs)
            Transform lead = c.Lead.transform;
            Bounds own = GroundScale.TrueBounds(data);
            Vector3 shape = lead.localScale / Mathf.Max(lead.localScale.x, 1e-6f);
            Bounds all = new Bounds(Vector3.Scale(own.center, shape), Vector3.Scale(own.size, shape));
            foreach (var m in c.Rest)
            {
                if (m == null || !m.activeInHierarchy) continue;
                var md = Game.Model(m);
                if (md == null) continue;
                Bounds b = md.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    all.Encapsulate(Vector3.Scale(lead.InverseTransformPoint(m.transform.TransformPoint(corner)), lead.localScale) / Mathf.Max(lead.localScale.x, 1e-6f));
                }
            }
            swapped = own; swappedData = data;
            GroundScale.HandOver(data);
            data.bounds = new Bounds(data.bounds.center, all.size);
        }
        
        // the ghosts are set out: those of the group's lead show the group (its largest objects), and the bounds go back
        static void GhostsDone(PaintingProxies __instance)
        {
            bool group = swapped.HasValue;
            Bounds leadBounds = group ? swapped.Value : new Bounds();
            if (group && swappedData != null) swappedData.bounds = swapped.Value;
            swapped = null; swappedData = null;
            try
            {
                var c = group ? CopyingNow() : null;
                Mesh leadMesh = c != null ? Game.Model(c.Lead).mesh : null;
                var list = fGhosts.GetValue(__instance) as List<GameObject>;
                if (list == null) return;
                foreach (var ghost in list)
                {
                    if (ghost == null) continue;
                    var filter = ghost.GetComponent<MeshFilter>();
                    bool wanted = c != null && filter != null && filter.sharedMesh == leadMesh;
                    Dress(__instance, ghost, wanted ? c : null, leadBounds);
                }
                if (c != null && !CopyUpDown.IgnoreCollision) GroundInTheWay(__instance, list, c, leadMesh);
            }
            catch (Exception e) { Plugin.Log.LogWarning("GroupTool (ghosts): " + e.Message); }
        }

        // A group with ground in it: the game's own "is ground in the way?" was left out for its ghosts (CopyUpDown.cs: it
        // would look with a box the size of the whole group, centred on the lead, which takes in the group itself and
        // the ground round it). Instead each of the group's own ground pieces is looked for where its copy would go,
        // and a ghost whose copy would put ground inside other ground is taken away.
        static readonly MethodInfo mReturnGhost = AccessTools.Method(typeof(PaintingProxies), "ReturnProxyToPool");
        static readonly Collider[] inTheWay = new Collider[8];
        static int groundMask = -1;
        static readonly List<Bounds> groundBoxes = new List<Bounds>();
        /// CopyUpDown.cs: the ghosts being set out now are a group's with ground in it (its own look for ground is used)
        public static bool GroupGroundGhosts(MeshData data)
        {
            var c = CopyingNow();
            return c != null && data != null && Game.Model(c.Lead) == data && GroundBoxes(c).Count > 0;
        }
        // the group's ground pieces as boxes around the lead, in the lead's own measure
        static List<Bounds> GroundBoxes(Copying c)
        {
            groundBoxes.Clear();
            Matrix4x4 toLead = c.Lead.transform.worldToLocalMatrix;
            var lead = Game.Model(c.Lead);
            if (lead != null && lead.type == MeshDataType.Ground) groundBoxes.Add(lead.bounds);
            foreach (var m in c.Rest)
            {
                if (m == null || !m.activeInHierarchy) continue;
                var md = Game.Model(m);
                if (md != null && md.type == MeshDataType.Ground) groundBoxes.Add(WorldBox(md.bounds, toLead * m.transform.localToWorldMatrix));
            }
            return groundBoxes;
        }
        static void GroundInTheWay(PaintingProxies paint, List<GameObject> ghosts, Copying c, Mesh leadMesh)
        {
            var boxes = GroundBoxes(c);
            if (boxes.Count == 0) return;
            if (groundMask < 0) groundMask = 1 << LayerMask.NameToLayer("Ground");
            for (int g = ghosts.Count - 1; g >= 0; g--)
            {
                var ghost = ghosts[g];
                if (ghost == null || ghost.GetComponent<MeshFilter>().sharedMesh != leadMesh) continue;
                // (a ghost stands in the world's own space - where the colliders are - at its local place)
                Transform t = ghost.transform;
                bool blocked = false;
                foreach (var b in boxes)
                {
                    Vector3 centre = t.localPosition + t.localRotation * Vector3.Scale(b.center, t.localScale);
                    Vector3 half = Vector3.Scale(b.extents, t.localScale) * 0.98f;     // (as the game's own: pieces side by side touch)
                    half = new Vector3(Mathf.Abs(half.x), Mathf.Abs(half.y), Mathf.Abs(half.z));
                    if (Physics.OverlapBoxNonAlloc(centre, half, inTheWay, t.localRotation, groundMask) > 0) { blocked = true; break; }
                }
                if (!blocked) continue;
                Dress(paint, ghost, null, new Bounds());
                ghosts.RemoveAt(g);
                mReturnGhost.Invoke(paint, new object[] { ghost });
            }
        }

        // A ghost of a group's lead shows the group's largest objects too (up to Shown of them, besides the lead: every
        // object could be a lot to draw on each of six ghosts), each where it is around the lead, in the game's own ghost
        // look. "Largest" is by real size: each model's own size times its scale, since models differ in size before they
        // are scaled. A ghost not of a group is put back.
        const int Shown = 8;
        static readonly FieldInfo fGhosts = AccessTools.Field(typeof(PaintingProxies), "proxies"), fGhostLooks = AccessTools.Field(typeof(PaintingProxies), "materials");
        class GroupGhost : MonoBehaviour { public Copying For; public List<Bounds> Shape; public Transform Parts; public List<BoxCollider> Boxes = new List<BoxCollider>(); }

        // the group's shape: each of the others' boxes around the lead (in the lead's own measure). Copying along a row
        // keeps the same shape, so the ghosts' boxes and parts made for it are kept instead of being made again.
        static List<Bounds> ShapeOf(Copying c)
        {
            if (c.Shape != null) return c.Shape;
            var shape = new List<Bounds>();
            Transform lead = c.Lead.transform;
            foreach (var m in c.Rest)
            {
                if (m == null || !m.activeInHierarchy) continue;
                var md = Game.Model(m);
                if (md == null) continue;
                shape.Add(WorldBox(md.bounds, lead.worldToLocalMatrix * m.transform.localToWorldMatrix));
            }
            return c.Shape = shape;
        }
        static bool SameShape(List<Bounds> a, List<Bounds> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if ((a[i].center - b[i].center).sqrMagnitude > 1e-6f || (a[i].size - b[i].size).sqrMagnitude > 1e-6f) return false;
            return true;
        }
        static void Dress(PaintingProxies paint, GameObject ghost, Copying c, Bounds leadBounds)
        {
            var dressed = ghost.GetComponent<GroupGhost>();
            if (c == null)
            {
                if (dressed == null) return;
                foreach (var extra in dressed.Boxes) if (extra != null) UnityEngine.Object.Destroy(extra);
                if (dressed.Parts != null) UnityEngine.Object.Destroy(dressed.Parts.gameObject);
                UnityEngine.Object.Destroy(dressed);
                return;
            }
            // the ghost is only touched where one of the group's objects would be: its own box is the lead's again (the
            // game sized it to the whole group), and each of the others gets a box of its own, around it as it stands
            // around the lead (still boxes, as the game's own ghosts are: the models' exact shapes would cost far more)
            var box = ghost.GetComponent<BoxCollider>();
            if (box != null) { box.size = leadBounds.size; box.center = leadBounds.center; }
            if (dressed == null) dressed = ghost.AddComponent<GroupGhost>();
            if (dressed.For == c && dressed.Parts != null) return;
            var shape = ShapeOf(c);
            if (dressed.Parts != null && SameShape(dressed.Shape, shape)) { dressed.For = c; return; }      // (the same group, copied along)
            dressed.Shape = shape;
            foreach (var extra in dressed.Boxes) if (extra != null) UnityEngine.Object.Destroy(extra);
            dressed.Boxes.Clear();
            if (dressed.Parts != null) UnityEngine.Object.Destroy(dressed.Parts.gameObject);
            dressed.For = c;
            var parts = new GameObject("TTE group ghost").transform;
            parts.SetParent(ghost.transform, false);
            dressed.Parts = parts;
            parts.gameObject.layer = ghost.layer;
            foreach (var own in shape)
            {
                var extra = ghost.AddComponent<BoxCollider>();
                extra.isTrigger = box != null && box.isTrigger;
                extra.center = own.center; extra.size = own.size;
                dressed.Boxes.Add(extra);
            }

            // the largest of the rest, by real size
            var looks = fGhostLooks.GetValue(paint) as Material[];
            var sized = new List<KeyValuePair<float, GameObject>>();
            foreach (var m in c.Rest)
            {
                if (m == null || !m.activeInHierarchy) continue;
                var md = Game.Model(m);
                if (md == null || md.mesh == null) continue;
                Vector3 size = Vector3.Scale(md.bounds.size, m.transform.lossyScale);
                sized.Add(new KeyValuePair<float, GameObject>(size.x * size.y * size.z, m));
            }
            sized.Sort((a, b) => b.Key.CompareTo(a.Key));
            Matrix4x4 toLead = c.Lead.transform.worldToLocalMatrix;
            for (int i = 0; i < sized.Count && i < Shown; i++)
            {
                var m = sized[i].Value;
                var md = Game.Model(m);
                var part = new GameObject(m.name, typeof(MeshFilter), typeof(MeshRenderer));
                part.layer = ghost.layer;
                part.transform.SetParent(parts, false);
                Matrix4x4 rel = toLead * m.transform.localToWorldMatrix;
                Vector3 pos; Quaternion rot; Vector3 scale;
                MatrixDecomposition.DecomposeMatrix(ref rel, out pos, out rot, out scale);
                part.transform.localPosition = pos; part.transform.localRotation = rot; part.transform.localScale = scale;
                part.GetComponent<MeshFilter>().sharedMesh = md.mesh;
                var r = part.GetComponent<MeshRenderer>();
                r.sharedMaterial = looks != null && md.materialIndex < looks.Length ? looks[md.materialIndex] : ghost.GetComponent<MeshRenderer>().sharedMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        // the game has copied the lead to a ghost: is it a group's (and so should the rest follow)?
        static Copying CopyOf(GameObject copy)
        {
            var c = CopyingNow();
            if (c == null || copy == null || copy == c.Lead || !copy.name.StartsWith(c.Lead.name)) return null;
            var hands = Hands;
            if (hands != null) foreach (var d in hands) if (d != null && d.state == PlayState.HandState.Gripping) return c;
            return null;
        }

        // the rest of the group copied, the same way round the copy as round the lead
        static List<UndoOperation> CopyRest(Copying c, GameObject copy)
        {
            var undo = new List<UndoOperation>();
            if (copy == null || !copy.activeInHierarchy) return undo;
            Matrix4x4 move = copy.transform.localToWorldMatrix * c.Lead.transform.worldToLocalMatrix;
            var graph = Graph; var copies = new List<GameObject>();
            var speech = (SpeechBubbleManager)fSpeech.GetValue(play); int speechLayer = (int)fSpeechLayer.GetValue(play);
            foreach (var m in c.Rest)
            {
                if (m == null || !m.activeInHierarchy) continue;
                var md = Game.Model(m);
                if (md == null || md.type == MeshDataType.Character) continue;
                Matrix4x4 where = move * m.transform.localToWorldMatrix;
                Vector3 pos; Quaternion rot; Vector3 scale;
                MatrixDecomposition.DecomposeMatrix(ref where, out pos, out rot, out scale);
                var go = graph.CreateNode(md.name, md);
                go.transform.position = pos; go.transform.rotation = rot; go.transform.localScale = scale;
                graph.AttachNode(go, null);
                if (md.type == MeshDataType.Ground && md.generateSides) Ground.Add(go);
                Looks.CopyLook(m, go);
                undo.Add(new CreateUndoOperation(go, graph, play.characterManager, Ground, speech, speechLayer));
                copies.Add(go);
            }
            if (copies.Count == 0) return undo;
            // the copies are now the group copying goes by (and, like the one copied, a selection or a made group)
            int id;
            if (groupOf.TryGetValue(c.Lead, out id))
            {
                int fresh = nextGroup++;
                groupOf[copy] = fresh; foreach (var g in copies) groupOf[g] = fresh;
                groupsChanged = true;
                Unsaved.Changes = true;
            }
            else if (selectedSet.Contains(c.Lead))
            {
                ClearAll();
                Select(copy); foreach (var g in copies) Select(g);
            }
            copying.Add(new Copying { Lead = copy, Rest = copies });
            return undo;
        }

    }
}
