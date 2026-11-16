// Tiny Town Enhanced - Object Copying also offers a copy above and below.
// Holding the copy button shows ghost copies of the object you last placed, one on each of its four sides; touching
// a ghost makes a real object there. This module adds two more ghosts, directly above and directly below, so things
// can be stacked the same way. They follow the game's own rules: no ghost where the same object (or, for ground,
// any ground) already is, and none twice in the same place.
// World > Placement, page 2: "Copy Up and Down" (on by default).
//
// Undo (and deleting): the game keeps adding ghosts as copies are made (each copy gets ghosts of its own), but never takes any away
// when a copy is undone, so ghosts were left around objects that no longer exist (and the next copy started from an
// undone one). And when a move was undone the ghosts stayed where the object had been moved to. Now every object the
// ghosts were set out around is remembered (PaintingProxies.SetObject), with the object itself once it is known; after
// an undo or a delete the ghosts are set out again around only those still there, wherever they now are (an undone
// move puts them back round the object where it went back to); the next copy then starts from the last of them.
// This is done whether or not "Copy Up and Down" is ticked.
//
// Ignore Copy Collision (World > Placement, page 2, off by default): the ghosts are set out even where something is
// already in the way (the game leaves out any ghost that would sit inside another ground piece, or on top of the same
// object), so a ground piece can be copied into another one.
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
        /// the World tab's Placement page (with the scaling options) shows this as "Copy Up and Down"
        public static bool On { get { return on != null && on.Value; } set { if (on != null) on.Value = value; } }
        static readonly Type T = typeof(PaintingProxies);
        static readonly FieldInfo fProxies = AccessTools.Field(T, "proxies"), fColliders = AccessTools.Field(T, "overlapColliders"), fGround = AccessTools.Field(T, "groundMask"),
                                  fPrimitive = AccessTools.Field(T, "primitiveMask"), fMaterials = AccessTools.Field(T, "materials"), fScale = AccessTools.Field(T, "scale");
        static readonly MethodInfo mTake = AccessTools.Method(T, "GetProxyFromPool");
        static ConfigEntry<bool> ignoreCollision;
        /// the World tab's Placement page shows this as "Ignore Copy Collision"
        public static bool IgnoreCollision { get { return ignoreCollision != null && ignoreCollision.Value; } set { if (ignoreCollision != null) ignoreCollision.Value = value; } }
        static bool warned;

        public void Start(Plugin plugin)
        {
            on = ForThisGame.Fresh(plugin.Config.Bind("Gameplay", "CopyUpDown", true, "Object Copying also offers a copy directly above and below."));
            ignoreCollision = ForThisGame.Fresh(plugin.Config.Bind("Gameplay", "IgnoreCopyCollision", false, "Object Copying offers its copies even where something is already in the way (a ground piece can be copied into another)."));
            var h = plugin.Harmony; var me = typeof(CopyUpDown);
            // (one prefix, one postfix and one finalizer on SetObject: the finalizer runs after every postfix, so the
            //  extra ghosts above and below see the same "nothing is in the way" as the game's own)
            h.Patch(AccessTools.Method(T, "SetObject"), prefix: new HarmonyMethod(me, "Setting"), postfix: new HarmonyMethod(me, "Set"), finalizer: new HarmonyMethod(me, "SetDone"));
            h.Patch(AccessTools.Method(T, "Reset"), prefix: new HarmonyMethod(me, "Forget"));
            h.Patch(AccessTools.Method(typeof(PlayState), "OnUndoButtonPressed"), postfix: new HarmonyMethod(me, "Undone"));
            // (deleting takes objects away too: anything remembered for undo is followed by the same check)
            h.Patch(AccessTools.Method(typeof(UndoStack), "Push"), postfix: new HarmonyMethod(me, "Changed"));
            h.Patch(AccessTools.Method(typeof(PlayState), "Enter"), prefix: new HarmonyMethod(me, "Forget"));
        }
        public void SceneLoaded(string scene) { seeds.Clear(); }
        public void Tick() { }

        // ---- undo: the ghosts set out again around only the objects that are still there
        class Seed { public MeshData Data; public Vector3 Position; public Quaternion Rotation; public Vector3 Scale; public GameObject Object; }
        static readonly List<Seed> seeds = new List<Seed>();
        static readonly Type P = typeof(PlayState);
        static readonly FieldInfo fPaint = AccessTools.Field(P, "proxies"), fLastData = AccessTools.Field(P, "lastPlacedMeshData"), fLastPos = AccessTools.Field(P, "lastPlacedPosition"),
                                  fLastRot = AccessTools.Field(P, "lastPlacedRotation"), fLastScale = AccessTools.Field(P, "lastPlacedScale"), fLastObject = AccessTools.Field(P, "lastPlacedObject");
        static readonly Collider[] near = new Collider[64];

        // ---- SetObject: Ignore Copy Collision, the ghosts above and below, and remembering what they were set out around
        static int savedGround, savedPrimitive; static bool masked;
        static void Setting(PaintingProxies __instance, MeshData data)
        {
            // (a group with ground in it looks for ground in the way itself, at each of its pieces: GroupTool.Copy.cs)
            if (!IgnoreCollision && !GroupTool.GroupGroundGhosts(data)) return;
            // (the game looks for what is in the way only on these two layers: with no layers, nothing is ever in the way)
            savedGround = (int)fGround.GetValue(__instance); savedPrimitive = (int)fPrimitive.GetValue(__instance);
            fGround.SetValue(__instance, 0); fPrimitive.SetValue(__instance, 0); masked = true;
        }
        static void Set(PaintingProxies __instance, MeshData data, Vector3 position, Quaternion rotation, Vector3 objectScale)
        {
            More(__instance, data, position, rotation, objectScale);
            Remember(data, position, rotation, objectScale);
        }
        static Exception SetDone(PaintingProxies __instance, Exception __exception)
        {
            if (masked) { masked = false; fGround.SetValue(__instance, savedGround); fPrimitive.SetValue(__instance, savedPrimitive); }
            return __exception;
        }

        // The object a seed stands for: the one just put down (the game's lastPlacedObject), or one being set out again
        // after an undo (binding). A copy made from a ghost does not exist yet when its ghosts are set out: it is found
        // a moment later, when its undo is recorded (Changed).
        static GameObject binding;
        static void Remember(MeshData data, Vector3 position, Quaternion rotation, Vector3 objectScale)
        {
            if (data == null) return;
            GameObject go = binding;
            if (go == null && Game.Play != null)
            {
                var last = fLastObject.GetValue(Game.Play) as GameObject;
                if (last != null && last.name.StartsWith(data.name) && Same(last.transform.position, position)) go = last;
            }
            // (one seed for each object: a seed of the same object somewhere else was where it used to be)
            if (go != null) seeds.RemoveAll(s => s.Object == go && !Same(s.Position, position));
            foreach (var s in seeds)
                if (s.Data == data && Same(s.Position, position)) { if (s.Object == null) s.Object = go; return; }
            seeds.Add(new Seed { Data = data, Position = position, Rotation = rotation, Scale = objectScale, Object = go });
        }
        static void Forget() { seeds.Clear(); }

        static void Undone(PlayState __instance) { afterUndo = true; Check(__instance); }
        // (objects are only followed to where they now are after an undo: anything else that records an undo while an
        //  object is in a hand, or being carried, must not set the ghosts out round it there)
        static bool afterUndo;
        static void Changed()
        {
            if (seeds.Count == 0) return;
            // (seeds whose object was not known yet - a copy just made from a ghost - find it now, while it is where
            //  it was made)
            foreach (var s in seeds) if (s.Object == null) s.Object = Find(s);
            if (checking) return;
            if (play == null) play = Game.Play;
            Check(play);
        }
        static PlayState play; static bool checking;
        static void Check(PlayState state)
        {
            if (seeds.Count > 0 && state != null && !checking) { checking = true; Plugin.Instance.StartCoroutine(Rebuild(state)); }
        }

        static System.Collections.IEnumerator Rebuild(PlayState play)
        {
            yield return null; yield return null;           // (undone objects are only gone once the frame has ended)
            while (Busy.Working) yield return null;         // (a large group's undo or delete is done a few frames later)
            checking = false;
            bool follow = afterUndo; afterUndo = false;
            try
            {
                // Which seeds are still there, and where: one whose object is known follows that object (an undone move
                // took it back to where it was); otherwise an object of its name has to be right where it was.
                bool moved = false;
                var keep = new List<Seed>();
                foreach (var s in seeds)
                {
                    if (s.Object == null) { if (Exists(s)) keep.Add(s); continue; }
                    if (!s.Object.activeInHierarchy) continue;                       // (undone or deleted: switched off by the game)
                    Transform t = s.Object.transform;
                    if (follow && (!Same(t.position, s.Position) || Quaternion.Angle(t.rotation, s.Rotation) > 0.01f || t.localScale != s.Scale))
                    {
                        moved = true;
                        s.Position = t.position; s.Rotation = t.rotation; s.Scale = t.localScale;
                    }
                    keep.Add(s);
                }
                if (!moved && keep.Count == seeds.Count) yield break;  // (the undo was of something else)
                var paint = fPaint.GetValue(play) as PaintingProxies;
                if (paint == null) yield break;
                GroupTool.Rebuilding = true;                 // (the Group Tool keeps what it is copying)
                try { paint.Reset(); }                       // (forgets the seeds too: they come back as they are set out again)
                finally { GroupTool.Rebuilding = false; }
                foreach (var s in keep)
                {
                    binding = s.Object;
                    try { paint.SetObject(s.Data, s.Position, s.Rotation, s.Scale); }
                    finally { binding = null; }
                }
                if (keep.Count > 0)
                {
                    var last = keep[keep.Count - 1];
                    fLastData.SetValue(play, last.Data); fLastPos.SetValue(play, last.Position);
                    fLastRot.SetValue(play, last.Rotation); fLastScale.SetValue(play, last.Scale);
                    if (last.Object != null) fLastObject.SetValue(play, last.Object);
                }
                else { fLastData.SetValue(play, null); fLastObject.SetValue(play, null); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("CopyUpDown (undo): " + e.Message); }
        }

        // is the object this seed stands for still in the world? (an object of that name with its pivot right there)
        static bool Exists(Seed s) { return Find(s) != null; }
        static GameObject Find(Seed s)
        {
            // (the objects and the seeds are both in the world's own space, which is where its colliders are)
            Vector3 at = s.Position; const float cm = 0.01f;
            int n = Physics.OverlapSphereNonAlloc(at, cm * 5f, near, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
                for (Transform t = near[i].transform; t != null; t = t.parent)
                    if (t.name.StartsWith(s.Data.name) && (t.position - at).magnitude < cm) return t.gameObject;
            return null;
        }

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
                Vector3 size = Vector3.Scale(data.bounds.size, objectScale);      // (ground may be resized across only: GroundScale.cs)
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
