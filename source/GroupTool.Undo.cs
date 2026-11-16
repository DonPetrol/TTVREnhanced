// Tiny Town Enhanced - Group Tool, continued: one undo for all of a group.
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
        // ================================================================ one undo for all of them
        class Together : UndoOperation
        {
            public readonly List<UndoOperation> All = new List<UndoOperation>();
            public override void Apply()
            {
                var all = new List<UndoOperation>(All);
                Busy.Run("Undoing " + all.Count + " objects...", all.Count, delegate { Bulk(delegate { for (int i = all.Count - 1; i >= 0; i--) all[i].Apply(); }); });
            }
            public override void Discard() { foreach (var u in All) u.Discard(); }
        }

        // undo waits while a large group job is still being done (it would undo half of it)
        static bool UndoWhenFree() { return !Busy.Working; }

        // the game is about to remember something for undo: if it is the held object being put down, the rest are put
        // down too; if it is a copy made from a group's ghost, the rest of the group is copied too; if it is one of a group
        // being deleted, the rest are deleted too. Each way one undo covers all.
        static void Pushing(ref UndoOperation operation)
        {
            try
            {
                if (play == null || operation == null || operation is Together) return;
                if (operation is CreateUndoOperation)
                {
                    var copy = (GameObject)fCreated.GetValue(operation);
                    var c = CopyOf(copy);
                    if (c == null) return;
                    var all = new Together(); all.All.Add(operation);
                    Busy.Run("Copying " + (c.Rest.Count + 1) + " objects...", c.Rest.Count, delegate { Bulk(delegate { all.All.AddRange(CopyRest(c, copy)); }); });
                    operation = all;
                    return;
                }
                if (operation is TransformUndoOperation && carrying.Count > 0 && (GameObject)fMoved.GetValue(operation) == held)
                {
                    var all = new Together(); all.All.Add(operation);
                    all.All.AddRange(DropCarry(true));
                    operation = all;
                }
                else if (operation is DestroyUndoOperation)
                {
                    // one of a group deleted: the rest of the group goes too, in the same undo
                    var gone = (GameObject)fDeleted.GetValue(operation);
                    var others = OthersWith(gone);
                    if (others.Count == 0) return;
                    var all = new Together(); all.All.Add(operation);
                    var graph = Graph;
                    Busy.Run("Deleting " + (others.Count + 1) + " objects...", others.Count, delegate { Bulk(delegate {
                        foreach (var m in others)
                        {
                            if (m == null || !m.activeInHierarchy) continue;
                            var md = Game.Model(m);
                            if (md != null && md.type == MeshDataType.Ground && md.generateSides) Ground.Remove(m);
                            Transform t = m.transform;
                            all.All.Add(new DestroyUndoOperation(m, t.position, t.rotation, t.localScale, graph, play.characterManager, Ground));     // (takes it out of the world)
                        }
                    }); });
                    operation = all;
                    if (selectedSet.Contains(gone)) ClearAll();
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("GroupTool (undo): " + e); }
        }
    }
}
