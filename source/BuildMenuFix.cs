// Tiny Town Enhanced - a fix for the game: opening the build menu while the screen fades to black on the way out of a
// world left it open in the main menu, where nothing closes it and it blocks the wrist menu.
// From the moment you choose to leave a world the build menu cannot be opened, and it is shut when the world is left.
using System;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class BuildMenuFix : IModule
    {
        public string Name { get { return "BuildMenuFix"; } }
        static bool leaving;

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(BuildMenuFix);
            h.Patch(AccessTools.Method(typeof(PlayState), "GeneratePreviewImage"), new HarmonyMethod(me, "Leaving"));     // "Save and Quit" / "Quit without Saving"
            h.Patch(AccessTools.Method(typeof(PlayState), "Exit"), new HarmonyMethod(me, "Left"));
            h.Patch(AccessTools.Method(typeof(PlayState), "Enter"), new HarmonyMethod(me, "Entered"));
            h.Patch(AccessTools.Method(typeof(Inventory), "Open"), new HarmonyMethod(me, "MayOpen"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Leaving(PlayState __instance) { leaving = true; Shut(__instance.inventory, false); }
        static void Left(PlayState __instance) { Shut(__instance.inventory, true); }
        static void Entered() { leaving = false; }
        static bool MayOpen() { return !leaving; }

        // at once: the game's own closing takes a moment, and stops when the build menu is switched off with the world;
        // so the menu is put, in one step, in the state the game leaves it in when it has closed
        static void Shut(Inventory inventory, bool atOnce)
        {
            try
            {
                if (inventory == null || inventory.IsFullyClosed) return;
                if (!atOnce && inventory.isActiveAndEnabled) { inventory.Close(); return; }
                var T = typeof(Inventory);
                foreach (string name in new[] { "openCoroutine", "closeCoroutine" })
                {
                    var running = (Coroutine)AccessTools.Field(T, name).GetValue(inventory);
                    if (running != null) inventory.StopCoroutine(running);
                }
                AccessTools.Field(T, "opening").SetValue(inventory, false);
                AccessTools.Field(T, "closing").SetValue(inventory, false);
                AccessTools.Field(T, "openPercent").SetValue(inventory, 0f);
                // and the rest of what the game does when the menu has closed (PlayState, on InventoryClosing): its tabs
                // are faded out, it follows no hand and it is switched off. Without this the tabs could stay half
                // visible and the menu kept working every frame in the next world; opening it sets all of this again.
                AccessTools.Method(T, "UpdateFilterButtonAlpha").Invoke(inventory, new object[] { 0f });
                inventory.Target = null; inventory.SelectionHandData = null;
                inventory.gameObject.SetActive(false);
            }
            catch (Exception e) { Plugin.Log.LogWarning("BuildMenuFix: " + e.Message); }
        }
    }
}
