// Tiny Town Enhanced - posed characters keep their heads straight when a world is opened.
//
// The bug (in the game itself): a posed character is saved as the places of its hands, feet and head only. When a
// world is opened (and when a pose change is undone), CharacterManager.BakeLoadedCharacter rebuilds each one with the
// game's IK (FullBodyBipedIK for the body, LookAtIK for the head) on ONE hidden working copy per character type, and
// bakes the result into a mesh. While you pose a character by hand, the IK components put the working copy's bones back
// to their rest pose before every solve. BakeLoadedCharacter calls the solvers directly and skips that, so each
// character starts from wherever the last character of the same type left the bones. The head's look-at only turns the
// head towards its target and never undoes the twist it starts with, so the twist builds up from character to character:
// heads end up turned or tilted sideways. (The game puts the hands and feet straight after the solve, and the heads
// only when a whole world is cleared - which is why the first character of each type comes out right.)
//
// The fix: just before the game's rebuild, the working copy's bones are put back to their rest pose, exactly as
// posing by hand does. Nothing in the save changes, so worlds saved before this look right the next time they open.
using System;
using System.Collections.Generic;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class PoseFix : IModule
    {
        public string Name { get { return "PoseFix"; } }

        public void Start(Plugin plugin)
        {
            plugin.Harmony.Patch(AccessTools.Method(typeof(CharacterManager), "BakeLoadedCharacter"),
                new HarmonyMethod(typeof(PoseFix), "Before"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // (the game's own parameter and field names: Harmony matches them)
        static void Before(GameObject go, Dictionary<string, GameObject> ___prefabLookup)
        {
            try
            {
                // the working copy is found the way the game finds it: the character's name without its last six letters
                // ("Name_00001" -> "Name")
                if (go == null || ___prefabLookup == null || go.name.Length <= 6) return;
                GameObject copy;
                if (!___prefabLookup.TryGetValue(go.name.Substring(0, go.name.Length - 6), out copy) || copy == null) return;
                GameObject rig = copy.transform.GetChild(0).gameObject;

                // put the bones back to their rest pose. Only a solver that has run before has a rest pose stored (and a
                // copy whose solver has never run has never been moved, so has nothing to put back)
                var body = rig.GetComponent<FullBodyBipedIK>();
                if (body != null && body.solver.initiated) body.solver.FixTransforms();
                var look = rig.GetComponent<LookAtIK>();
                if (look != null && look.solver.initiated) look.solver.FixTransforms();
            }
            catch (Exception e) { Plugin.Log.LogWarning("PoseFix: " + e.Message); }
        }
    }
}
