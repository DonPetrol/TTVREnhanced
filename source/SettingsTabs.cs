// Tiny Town Enhanced - the Settings page is split into categories: Gameplay, Controls, Video, Audio, Accessibility.
// The page itself (category buttons, rows, page arrows) is a MenuPage; this module says what is on it and keeps the
// values in the mod's settings file (BepInEx\config\donpetrol.tinytownenhanced.cfg).
//
// Adding a setting from another module is one call, made in that module's Start:
//     SettingsTabs.AddCheckbox("Controls", "My Option", "Shown beside the box while it is ticked.", entry, on => { ... });
//     SettingsTabs.AddChoice("Video", "Quality", new[] { "Low", "High" }, entry, index => { ... });      // press to step through
//     SettingsTabs.AddSlider("Video", "Scale", entry, 0.5f, 2f, 0.05f, v => v.ToString("0.00") + "x", v => { ... });
using System.Collections.Generic;
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine.UI;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class SettingsTabs : IModule
    {
        public string Name { get { return "SettingsTabs"; } }

        static readonly MenuPage page = new MenuPage("SettingsTabs",
            new[] { "Gameplay", "Controls", "Video", "Audio", "Accessibility" },
            // the order of the rows inside a category, by label; rows not named here follow in the order they were added
            new[] { "Resolution Scale", "Anti-aliasing", "Shadow Quality", "Shadow Distance", "Larger Mesh Grid", "High Resolution Photos", "FPS Counter",
                    "Character Pose Toggling", "Copy Up and Down", "Autosave", "Autosave Interval", "Autosave Message",
                    "Head-based Zoom", "Grab Movement Drift", "Stick Movement", "Stick Direction", "Stick Speed", "Sprint Speed", "Stick Movement Drift", "Stick Up/Down", "Stick Scale",
                    "Stick Turning", "Turn Style", "Snap Angle", "Turn Speed", "Swap Sticks" });
        static bool gameRowsAdded;
        static readonly List<Action> resets = new List<Action>();    // puts each of the mod's settings back to its default
        static GameObject reset; static WristSettingsPage current;

        /// A checkbox row. Call from a module's Start.
        public static void AddCheckbox(string category, string label, string description, ConfigEntry<bool> entry, Action<bool> changed, Func<bool> visible = null)
        {
            page.AddCheckbox(category, label, description, () => entry.Value, delegate (bool v) { entry.Value = v; if (changed != null) changed(v); }, visible);
            resets.Add(delegate { entry.Value = (bool)entry.DefaultValue; if (changed != null) changed(entry.Value); });
            if (changed != null) changed(entry.Value);
        }

        /// A row with a button that steps through the options each time it is pressed. The entry holds the option's number.
        public static void AddChoice(string category, string label, string[] options, ConfigEntry<int> entry, Action<int> changed, Func<bool> visible = null)
        {
            page.AddChoice(category, label, options, () => entry.Value, delegate (int v) { entry.Value = v; if (changed != null) changed(v); }, visible);
            resets.Add(delegate { entry.Value = (int)entry.DefaultValue; if (changed != null) changed(entry.Value); });
            if (changed != null) changed(Mathf.Clamp(entry.Value, 0, options.Length - 1));
        }

        /// A slider row from min to max in steps; format turns the value into the text shown beside it.
        public static void AddSlider(string category, string label, ConfigEntry<float> entry, float min, float max, float step, Func<float, string> format, Action<float> changed, Func<bool> visible = null)
        {
            page.AddSlider(category, label, min, max, step, format, () => entry.Value, delegate (float v) { entry.Value = v; if (changed != null) changed(v); }, visible);
            resets.Add(delegate { entry.Value = (float)entry.DefaultValue; if (changed != null) changed(entry.Value); });
            if (changed != null) changed(Mathf.Clamp(entry.Value, min, max));
        }

        public void Start(Plugin plugin)
        {
            // built the first time the Settings page is opened; refreshed every time after that
            plugin.Harmony.Patch(AccessTools.Method(typeof(WristSettingsPage), "Activate"), null, new HarmonyMethod(typeof(SettingsTabs), "Opened"));
            plugin.Harmony.Patch(AccessTools.Method(typeof(WristSettingsPage), "Deactivate"), null, new HarmonyMethod(typeof(SettingsTabs), "Closed"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // (nothing that goes wrong here may get out: this runs inside the game's own routine for opening the tab, and
        // would stop it before it has set the tab's colour and the title)
        static void Opened(WristSettingsPage __instance)
        {
            try { Open(__instance); } catch (Exception e) { Plugin.Log.LogWarning("SettingsTabs: " + e); }
        }

        static void Open(WristSettingsPage __instance)
        {
            if (!gameRowsAdded)
            {   // the game's own rows, each given a category
                gameRowsAdded = true;
                var s = __instance;
                page.AddGameRow("Audio", "Music Volume", null, null, "MusicVolumeLabel", "MusicVolume");
                page.AddGameRow("Audio", "SoundFX Volume", null, null, "SoundFXVolumeLabel", "SoundFXVolume");
                page.AddGameRow("Video", "High Resolution Photos", "HighResScreenshotToggleDesc", () => s.HighResPhotosToggle, "HighResScreenshotToggleLabel", "HighResScreenshotToggleCheckbox");
                page.AddGameRow("Gameplay", "Character Pose Toggling", "CharacterPoseToggleDesc", () => s.CharacterPoseToggle, "CharacterPoseToggleLabel", "CharacterPoseToggleCheckbox");
            }
            current = __instance;
            var menu = __instance.GetComponentInParent<WristMenu>();
            if (menu == null) menu = UnityEngine.Object.FindObjectOfType<WristMenu>();
            page.Open(__instance.transform, menu);
            // the red Reset button in the header's corner, while this page is open: every setting back to its default
            if (reset == null)
                reset = page.HeaderButton("Reset", delegate
                {
                    page.Ask("Put every setting on this page back to its default?", "Reset", delegate
                    {
                        foreach (var r in resets) try { r(); } catch (Exception e) { Plugin.Log.LogWarning("SettingsTabs: " + e.Message); }
                        try { GameDefaults(); } catch (Exception e) { Plugin.Log.LogWarning("SettingsTabs: " + e.Message); }
                    });
                });
            if (reset != null) reset.SetActive(true);
        }

        // the game's own four settings: both volumes full, character poses and high resolution photos off
        static void GameDefaults()
        {
            var settings = current;
            if (settings == null) return;
            var T = typeof(WristSettingsPage);
            var sound = (SoundManager)AccessTools.Field(T, "soundManager").GetValue(settings);
            sound.MusicVolume = 1f; sound.SoundFXVolume = 1f;
            ((TouchSlider)AccessTools.Field(T, "musicVolumeSlider").GetValue(settings)).NormalizedValue = 1f;
            ((TouchSlider)AccessTools.Field(T, "soundFXVolumeSlider").GetValue(settings)).NormalizedValue = 1f;
            if (settings.CharacterPoseToggle) settings.CharacterPoseToggle = false;
            if (settings.HighResPhotosToggle) settings.HighResPhotosToggle = false;
            AccessTools.Field(T, "changesPending").SetValue(settings, true);          // (the game then saves them when the page is left)
        }

        static void Closed() { if (reset != null) reset.SetActive(false); }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - the Settings tab is also on the wrist menu while you are in a world.
    // The game only shows that tab in the main menu. Here it is switched on for worlds too; the game's own code then
    // spaces the bottom tabs out again (Photos, Speech, Snapping, Settings, Help, Quit).
    public class SettingsInWorld : IModule
    {
        public string Name { get { return "SettingsInWorld"; } }

        public void Start(Plugin plugin)
        {
            plugin.Harmony.Patch(AccessTools.Method(typeof(WristMenu), "ResetForPlay"), null, new HarmonyMethod(typeof(SettingsInWorld), "AddTab"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void AddTab(WristMenu __instance)
        {
            try
            {
                __instance.settingsPageTabButton.gameObject.SetActive(true);
                AccessTools.Method(typeof(WristMenu), "UpdateButtons").Invoke(__instance, null);
                // six tabs are narrower than five: let the wording shrink if it has to
                foreach (var tab in new[] { __instance.photosPageTabButton, __instance.speechPageTabButton, __instance.snappingPageTabButton,
                                            __instance.settingsPageTabButton, __instance.helpPageTabButton, __instance.quitPageTabButton })
                {
                    var text = tab.GetComponentInChildren<Text>(true);
                    if (text == null || text.resizeTextForBestFit) continue;
                    text.resizeTextMaxSize = Mathf.Max(text.fontSize, 10); text.resizeTextMinSize = 10; text.resizeTextForBestFit = true;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("SettingsInWorld: " + e.Message); }
        }
    }
}
