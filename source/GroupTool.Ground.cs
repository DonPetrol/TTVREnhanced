// Tiny Town Enhanced - Group Tool, continued: many ground pieces handled at once.
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
        // ================================================================ ground pieces, many at once
        // The game's GroundManager redraws the sides of every ground piece in the world each time one piece is added or
        // taken away (Add and Remove both end with Batch, which rebuilds the one big mesh of all the sides), so moving a
        // group of n ground pieces cost n whole rebuilds - the long freeze. While a group job runs (Bulk), adding only
        // records the piece and taking away only forgets it; at the end the sides are worked out once, for those pieces
        // and their neighbours only (GroundDone).
        static int bulk;
        static readonly Type GM = typeof(GroundManager);
        static readonly FieldInfo fGroundData = AccessTools.Field(GM, "objectData");
        static readonly MethodInfo mRelease = AccessTools.Method(GM, "ReleaseChunk");
        static FieldInfo fSides;
        static readonly List<GameObject> groundAdded = new List<GameObject>();
        static readonly List<Bounds> groundGone = new List<Bounds>();
        static readonly MethodInfo mUpdateObject = AccessTools.Method(GM, "UpdateObject"), mUpdateNeighbours = AccessTools.Method(GM, "UpdateNeighbors"),
            mLowest = AccessTools.Method(GM, "UpdateLowestDepth"), mGroundBatch = AccessTools.Method(GM, "Batch");
        static void Bulk(Action work)
        {
            bulk++;
            try { work(); }
            finally
            {
                bulk--;
                if (bulk == 0) GroundDone();
            }
        }
        // the sides worked out for just the pieces added or taken away, and the pieces beside them; then the one mesh of
        // all the sides made once (nothing at all when no ground piece was touched)
        static void GroundDone()
        {
            if (groundAdded.Count == 0 && groundGone.Count == 0) return;
            try
            {
                var g = play != null ? Ground : null;
                if (g == null) return;
                var all = (System.Collections.IDictionary)fGroundData.GetValue(g);
                foreach (var go in groundAdded)
                {
                    if (go == null || !all.Contains(go)) continue;
                    var box = go.GetComponent<BoxCollider>();
                    if (box == null) continue;
                    mUpdateObject.Invoke(g, new object[] { go.transform.position.y, box.bounds, all[go] });
                    mUpdateNeighbours.Invoke(g, new object[] { go, box.bounds });
                }
                foreach (var b in groundGone) mUpdateNeighbours.Invoke(g, new object[] { null, b });
                mLowest.Invoke(g, null);
                mGroundBatch.Invoke(g, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("GroupTool (ground sides): " + e.Message); var g = Ground; if (g != null) g.UpdateAll(); }
            finally { groundAdded.Clear(); groundGone.Clear(); }
        }
        static void GroundAdding(GameObject go, ref bool update)
        {
            if (bulk == 0) return;
            update = false;
            if (go != null && go.transform.childCount > 0) groundAdded.Add(go.transform.GetChild(0).gameObject);
        }
        static bool GroundRemoving(GroundManager __instance, GameObject go)
        {
            if (bulk == 0 || go == null) return true;
            try
            {
                var key = go.transform.GetChild(0).gameObject;
                var all = (System.Collections.IDictionary)fGroundData.GetValue(__instance);
                if (all.Contains(key))
                {
                    object data = all[key];
                    if (fSides == null) fSides = AccessTools.Field(data.GetType(), "sides");
                    mRelease.Invoke(__instance, new[] { fSides.GetValue(data) });
                    fSides.SetValue(data, null);
                    all.Remove(key);
                    var box = key.GetComponent<BoxCollider>();
                    if (box != null) groundGone.Add(box.bounds);
                }
                return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("GroupTool (ground): " + e.Message); return true; }
        }

    }
}
