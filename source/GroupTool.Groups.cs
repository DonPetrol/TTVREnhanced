// Tiny Town Enhanced - Group Tool, continued: groups made to stay together, saved with the world.
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
        // ================================================================ groups made to stay together (saved with the world)
        static readonly Dictionary<GameObject, int> groupOf = new Dictionary<GameObject, int>();
        static int nextGroup = 1;
        // each group's objects, worked out again only after groupOf has changed (groupsChanged): finding a group's
        // members used to mean going through every grouped object, for every object the brush touched
        static readonly Dictionary<int, List<GameObject>> byGroup = new Dictionary<int, List<GameObject>>();
        static bool groupsChanged = true;
        static readonly List<GameObject> noMembers = new List<GameObject>();
        static List<GameObject> InGroup(int id)
        {
            if (groupsChanged)
            {
                groupsChanged = false; byGroup.Clear();
                foreach (var kv in groupOf)
                {
                    List<GameObject> list;
                    if (!byGroup.TryGetValue(kv.Value, out list)) byGroup[kv.Value] = list = new List<GameObject>();
                    list.Add(kv.Key);
                }
            }
            List<GameObject> members;
            return byGroup.TryGetValue(id, out members) ? members : noMembers;
        }

        /// EditTab.cs: is `node` one of a group made with Create Group
        public static bool InCreatedGroup(GameObject node) { return node != null && groupOf.ContainsKey(node); }
        /// EditTab.cs: show the Create Group / Break Group row (several selected with `node` among them, or a group)
        public static bool GroupRowWanted(GameObject node)
        {
            return InCreatedGroup(node) || (node != null && selectedSet.Contains(node) && selected.Count >= 2);
        }
        /// EditTab.cs: Create Group (the selection becomes one group) or Break Group (the group `node` is in is undone)
        public static void CreateOrBreak(GameObject node)
        {
            if (node == null) return;
            int id;
            if (groupOf.TryGetValue(node, out id))
            {
                var gone = new List<GameObject>(InGroup(id));
                foreach (var g in gone) groupOf.Remove(g);
            }
            else if (selectedSet.Contains(node) && selected.Count >= 2)
            {
                id = nextGroup++;
                foreach (var go in selected) if (go != null) groupOf[go] = id;     // (one of another group leaves it for this one)
            }
            else return;
            groupsChanged = true;
            Unsaved.Changes = true;
        }

        // saving: the game numbers the objects as it writes them (Looks.cs passes the numbers on); kept as
        // "group:number,number,...;group:..." in the world's settings
        static readonly Dictionary<int, List<int>> savingGroups = new Dictionary<int, List<int>>();
        public static void SaveStarts() { savingGroups.Clear(); }
        public static void SavingNode(GameObject node, int number)
        {
            int id;
            if (node == null || !groupOf.TryGetValue(node, out id)) return;
            List<int> list;
            if (!savingGroups.TryGetValue(id, out list)) savingGroups[id] = list = new List<int>();
            list.Add(number);
        }
        static void WriteGroups()
        {
            var parts = new List<string>();
            foreach (var kv in savingGroups)
                if (kv.Value.Count >= 2)
                {
                    var numbers = new string[kv.Value.Count];
                    for (int i = 0; i < numbers.Length; i++) numbers[i] = kv.Value[i].ToString();
                    parts.Add(kv.Key + ":" + string.Join(",", numbers));
                }
            WorldFile.SetText("groups", parts.Count > 0 ? string.Join(";", parts.ToArray()) : null);
        }
        // loading: the same numbers, back to objects
        public static void Loaded(Dictionary<int, Transform> numbered)
        {
            groupOf.Clear(); nextGroup = 1; groupsChanged = true;
            string text = WorldFile.GetText("groups");
            if (numbered == null || string.IsNullOrEmpty(text)) return;
            int made = 0;
            foreach (string each in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = each.Split(':');
                if (p.Length < 2) continue;
                int id = nextGroup++, count = 0;
                foreach (string n in p[1].Split(','))
                {
                    int number; Transform t;
                    if (int.TryParse(n, out number) && numbered.TryGetValue(number, out t) && t != null) { groupOf[t.gameObject] = id; count++; }
                }
                if (count > 0) made++;
            }
            if (made > 0) Plugin.Log.LogInfo("GroupTool: " + made + " group(s) loaded");
        }
        public static void Cleared() { groupOf.Clear(); nextGroup = 1; groupsChanged = true; }


    }
}
