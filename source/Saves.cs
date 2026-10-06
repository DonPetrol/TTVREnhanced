// Tiny Town Enhanced - autosaves and manual saves are kept apart.
// In the game a world is one file, "data". Its autosave writes over that same file, and "Quit without Saving" then
// puts back a copy taken when the world was opened - so an autosave only ever survives a crash.
// With this module, in a world's folder:
//   data        the manual save: only "Save and Quit" writes it (the game then also keeps the three before it as
//               backup_1, backup_2, backup_3 - that part is the game's own)
//   autosave    what the autosave writes, instead of "data"
// "Quit without Saving" leaves both as they are, so the work of that session can still be opened from its autosave
// (WorldPage.cs shows the choice). "Save and Quit" removes the autosave, since the save is then the newer one; so
// does quitting without saving when the autosave is no different from the save.
// The game autosaves at once on entering a world; that one is left out unless something has been changed, or merely
// opening a world's save would write over the autosave kept from the session before.
// Opening and leaving a world also puts the date on its "played" file (WorldList.cs: the Last Played order).
// An unmodified game ignores "autosave" and reads "data" as always. Uploading to the Workshop only ever takes "data".
//
// Settings > Gameplay: Autosave (on/off), Autosave Interval, Autosave Message (a countdown before each autosave and
// "Saved" after it, as a small readout below the middle of the view).
using System.Collections;
using System.IO;
using System.Reflection;
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class Saves : IModule
    {
        public string Name { get { return "Saves"; } }
        public const string Autosave = "autosave", Manual = "data";
        public static bool On { get { return on != null && on.Value; } }
        const int Countdown = 5;                                    // seconds of warning before an autosave
        static ConfigEntry<bool> on, message; static ConfigEntry<float> minutes;
        static readonly FieldInfo fSince = AccessTools.Field(typeof(PlayState), "timeSinceLastSave"), fDirectory = AccessTools.Field(typeof(PlayState), "worldDirectory");
        static int shown = -1; static float nextLook; static string finished;
        static bool entered;                                        // a world has just been entered: the next autosave is the game's one on arrival
        static bool wrote;                                          // the autosave now being made was let through

        public void Start(Plugin plugin)
        {
            on = plugin.Config.Bind("Gameplay", "Autosave", true, "The world is saved by itself every few minutes while you build (to its own file, not over your save).");
            minutes = plugin.Config.Bind("Gameplay", "AutosaveMinutes", 1f, "Minutes between autosaves (1 to 30).");
            message = plugin.Config.Bind("Gameplay", "AutosaveMessage", true, "Show a countdown before each autosave, and a note when it is done.");
            SettingsTabs.AddCheckbox("Gameplay", "Autosave", "The world is saved by itself while you build, apart from your own save.", on, null);
            SettingsTabs.AddSlider("Gameplay", "Autosave Interval", minutes, 1f, 30f, 1f, v => v.ToString("0") + " min", null, () => on.Value);
            SettingsTabs.AddCheckbox("Gameplay", "Autosave Message", "A countdown shows before each autosave.", message, null, () => on.Value);

            var h = plugin.Harmony; var me = typeof(Saves);
            h.Patch(AccessTools.Method(typeof(SceneGraph), "SaveBackup", new[] { typeof(string), typeof(string) }), new HarmonyMethod(me, "Autosaving"), new HarmonyMethod(me, "Autosaved"));
            h.Patch(AccessTools.Method(typeof(PlayState), "Enter"), new HarmonyMethod(me, "Entered"));
            h.Patch(AccessTools.Method(typeof(PlayState), "GeneratePreviewImage"), new HarmonyMethod(me, "Leaving"));
            h.Patch(AccessTools.Method(typeof(SceneGraph), "Save", new[] { typeof(string), typeof(string) }), null, new HarmonyMethod(me, "Saved"));
            // (PlayState.Early runs once a frame, and only while a world is open; the game's autosave clock is in it)
            h.Patch(AccessTools.Method(typeof(PlayState), "Early"), new HarmonyMethod(me, "Clock"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // a world is being entered
        static void Entered(PlayState __instance)
        {
            entered = true;
            try { WorldSort.Touch((string)fDirectory.GetValue(__instance)); } catch (Exception) { }
        }

        // the game is about to autosave over "data": send it to "autosave" instead
        static bool Autosaving(ref string filename)
        {
            if (filename == Manual) filename = Autosave;
            shown = -1;
            wrote = true;
            // The game sets its autosave clock to "due" on entering a world, so it autosaves on the very first frame.
            // That save would be of the file just opened, written over the autosave already there - and when the
            // save was opened while a newer autosave was being kept (the work of a session left without saving),
            // that work would be gone the moment the world appeared. So the autosave on arrival is not made unless
            // something has been changed; every later one is.
            if (entered)
            {
                entered = false;
                if (!Unsaved.Changes) { wrote = false; return false; }
            }
            return true;
        }

        // the game has made the autosave (said afterwards, and only if one was made: not when it was left out above,
        // nor when the game could not make it)
        static void Autosaved()
        {
            if (!wrote) return;
            wrote = false;
            if (message.Value) Popup.Note("Saved", 3f, 1);
        }

        // before the game's own clock runs this frame: the interval, the on/off switch and the countdown
        static void Clock(PlayState __instance)
        {
            try
            {
                // (looked at twice a second, and while counting down once for each second of it)
                play = __instance;
                if (Time.unscaledTime < nextLook) return;
                nextLook = Time.unscaledTime + 0.5f;
                float interval = Mathf.Clamp(minutes.Value, 1f, 30f) * 60f;
                __instance.saveInterval = interval;
                if (!on.Value) { fSince.SetValue(__instance, 0f); return; }                 // (the clock never gets there)
                if (!message.Value) return;
                float left = interval - (float)fSince.GetValue(__instance);
                int seconds = Mathf.CeilToInt(left);
                // (while counting down: looked at again just after the next whole second passes, not every frame)
                if (left <= Countdown + 0.5f) nextLook = Time.unscaledTime + Mathf.Clamp(left - Mathf.Floor(left) + 0.01f, 0.02f, 0.5f);
                if (seconds >= 1 && seconds <= Countdown && seconds != shown)
                {
                    shown = seconds;
                    Popup.Note("Autosave in " + seconds, 1.5f, 1);
                }
            }
            catch (Exception) { }
        }

        // leaving the world. The game would put back the copy it took on opening ("original") if you are not saving;
        // with autosaves in a file of their own "data" has not been touched, so that copy is simply removed.
        static void Leaving(PlayState __instance, bool saveChanges)
        {
            try
            {
                string directory = (string)fDirectory.GetValue(__instance);
                if (string.IsNullOrEmpty(directory)) return;
                string original = Path.Combine(directory, "original"), autosave = Path.Combine(directory, Autosave), manual = Path.Combine(directory, Manual);
                if (File.Exists(original)) File.Delete(original);
                finished = saveChanges ? autosave : null;                                   // the autosave goes once the save has been written (Saved, below)
                if (saveChanges) { WorldSort.Touch(directory); return; }
                Soon(delegate { Tidy(directory); });
            }
            catch (Exception e) { Plugin.Log.LogWarning("Saves: " + e.Message); }
        }

        // the world was left without saving: what is no use goes, and the rest is marked as played now
        static void Tidy(string directory)
        {
            string autosave = Path.Combine(directory, Autosave), manual = Path.Combine(directory, Manual);
            if (File.Exists(autosave) && WorldCopies.SameFiles(autosave, manual)) File.Delete(autosave);    // nothing was changed
            // a new world left before it was ever saved or autosaved has nothing in it to open: its folder goes
            if (!File.Exists(autosave) && !File.Exists(manual) && Directory.GetDirectories(directory).Length == 0)
            {
                bool empty = true;
                foreach (string file in Directory.GetFiles(directory))
                    if (Path.GetFileName(file) != "preview.jpg" && Path.GetFileName(file) != WorldSort.PlayedFile) empty = false;
                if (empty) { Directory.Delete(directory, true); return; }
            }
            WorldSort.Touch(directory);
        }

        // Do something to a world's files now - or, if a file is in use, once more a second later. The game writes an
        // autosave in the background and lets nothing else touch the file meanwhile, so leaving a world just as it
        // autosaves would otherwise fail here and leave things half tidied.
        static void Soon(Action work)
        {
            try { work(); }
            catch (IOException) { Plugin.Instance.StartCoroutine(Later(work)); }
        }
        static IEnumerator Later(Action work)
        {
            yield return new WaitForSecondsRealtime(1f);
            try { work(); } catch (Exception e) { Plugin.Log.LogWarning("Saves: " + e.Message); }
        }

        // Save and Quit has written the save: the autosave is the older of the two now
        static void Saved(string directory)
        {
            string old = finished;
            finished = null;
            try { if (old != null) Soon(delegate { if (File.Exists(old)) File.Delete(old); }); } catch (Exception) { }
            Thumbnails.Keep(directory);         // (the game made a new picture of the world just before: a chosen one goes back)
        }

        /// a manual save without leaving the world (the Save button in the World tab's header): the same save as Save
        /// and Quit's - the previous saves move down to Backup 1-3 - without a new picture
        public static void SaveNow()
        {
            try
            {
                if (play == null) return;
                var graph = (SceneGraph)AccessTools.Field(typeof(PlayState), "graph").GetValue(play);
                string directory = (string)fDirectory.GetValue(play);
                if (graph == null || string.IsNullOrEmpty(directory)) return;
                graph.Save(directory, Manual);
                string autosave = Path.Combine(directory, Autosave);
                if (File.Exists(autosave)) File.Delete(autosave);       // the save is the newer one now
                fSince.SetValue(play, 0f);                              // and the autosave clock starts again
                shown = -1;
                Popup.Note("Saved", 3f, 1);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Saves: " + e); Popup.Note("Could not save", 3f, 1); }
        }
        static PlayState play;
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - does the open world have changes that have not been saved?
    // The game does not keep track. Here a world counts as changed once anything that can be undone has been done
    // (placing, moving, painting, deleting...), or one of the World tab's settings has been changed, since it was opened
    // or last saved. QuitMenu.cs uses this to ask before "Quit without Saving" throws work away.
    public class Unsaved : IModule
    {
        public string Name { get { return "Unsaved"; } }
        public static bool Changes;

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(Unsaved);
            h.Patch(AccessTools.Method(typeof(UndoStack), "Push"), null, new HarmonyMethod(me, "Mark"));
            h.Patch(AccessTools.Method(typeof(PlayState), "Enter"), new HarmonyMethod(me, "Clean"));
            h.Patch(AccessTools.Method(typeof(SceneGraph), "Save", new[] { typeof(string), typeof(string) }), null, new HarmonyMethod(me, "Clean"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        public static void Mark() { Changes = true; }
        static void Clean() { Changes = false; }
    }
}
