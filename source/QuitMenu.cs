// Tiny Town Enhanced - the Quit tab asks "save or not?" as a second step instead of using a checkbox.
// Before: [Main Menu] [Desktop] plus a "save changes" checkbox that was easy to overlook.
// Now:    [Main Menu] [Desktop]  ->  touch one  ->  [Save and Quit] [Quit without Saving], under
//         "Are you sure you want to quit to main menu?" (or "... to desktop?")
// "Quit without Saving" asks once more if the world has changes that were not saved: [Back] [Discard Changes].
// The two buttons are the game's own; this module only changes what they say and what the first touch does.
// The second touch sets the game's own "save changes" value and then runs the game's own quit code, so saving
// works exactly as the checkbox did. Opening another tab cancels and brings the first step back.
// (In the main menu, where no world is open, there is nothing to save and Quit works as before.)
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class QuitMenu : IModule
    {
        public string Name { get { return "QuitMenu"; } }
        const string SaveText = "Save and Quit", DiscardText = "Quit without\nSaving";
        const float IgnoreSeconds = 0.6f;       // a touch that is still on the button must not answer the second step

        enum Step { Where, ToMenu, ToDesktop }
        static Step step = Step.Where;
        static float changedAt;
        static bool passing;                    // true while the game's own quit code is being run on purpose
        static bool confirming;                 // the third step is showing: "Quit without Saving" was touched with unsaved changes
        static string menuLabel, desktopLabel, questionWas; static Text question;

        static readonly Type T = typeof(WristQuitPage);
        static readonly FieldInfo fMenuButton = AccessTools.Field(T, "mainMenuButton"), fQuitButton = AccessTools.Field(T, "quitButton"),
            fLabel = AccessTools.Field(T, "saveChangeLabelGO"), fCheckbox = AccessTools.Field(T, "saveChangeCheckboxGO"),
            fSave = AccessTools.Field(T, "saveChanges"), fPlay = AccessTools.Field(T, "playState"), fSound = AccessTools.Field(T, "soundManager");

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(QuitMenu);
            h.Patch(AccessTools.Method(T, "OnMainMenuButtonPressed"), new HarmonyMethod(me, "MenuPressed"));
            h.Patch(AccessTools.Method(T, "OnQuitButtonPressed"), new HarmonyMethod(me, "DesktopPressed"));
            foreach (var m in new[] { "Activate", "Deactivate", "ShowMainMenuButton", "HideMainMenuButton" })
                h.Patch(AccessTools.Method(T, m), null, new HarmonyMethod(me, "Reset"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static bool WorldOpen(WristQuitPage page)
        {
            var play = fPlay.GetValue(page) as PlayState;
            return play != null && play.gameObject.activeSelf;
        }

        // left button: "Main Menu" in the first step, "Save and Quit" in the second
        static bool MenuPressed(WristQuitPage __instance)
        {
            if (passing) return true;
            try
            {
                if (!WorldOpen(__instance)) return true;
                if (step == Step.Where) { Ask(__instance, Step.ToMenu); return false; }
                if (Time.unscaledTime - changedAt < IgnoreSeconds) return false;
                if (confirming) { confirming = false; Ask(__instance, step); return false; }       // "Back": to the second step again
                return Finish(__instance, true, true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("QuitMenu: " + e.Message); return true; }
        }

        // right button: "Desktop" in the first step, "Quit without Saving" in the second
        static bool DesktopPressed(WristQuitPage __instance)
        {
            if (passing) return true;
            try
            {
                if (!WorldOpen(__instance)) return true;
                if (step == Step.Where) { Ask(__instance, Step.ToDesktop); return false; }
                if (Time.unscaledTime - changedAt < IgnoreSeconds) return false;
                if (!confirming && Unsaved.Changes) { Confirm(__instance); return false; }
                return Finish(__instance, false, false);
            }
            catch (Exception e) { Plugin.Log.LogWarning("QuitMenu: " + e.Message); return true; }
        }

        // first step done: show the second
        static void Ask(WristQuitPage page, Step where)
        {
            step = where; changedAt = Time.unscaledTime;
            var sound = fSound.GetValue(page) as SoundManager;
            if (sound != null) sound.Play(SoundManager.SoundEffect.BUTTON_CLICK_POSITIVE);
            SetLabel(fMenuButton.GetValue(page) as GameObject, SaveText, ref menuLabel);
            SetLabel(fQuitButton.GetValue(page) as GameObject, DiscardText, ref desktopLabel);
            Question(page, "Are you sure you want to quit to " + (where == Step.ToMenu ? "main menu" : "desktop") + "?");
        }

        // "Quit without Saving" with changes that have not been saved (Unsaved.cs): a third step, [Back] [Discard Changes]
        static void Confirm(WristQuitPage page)
        {
            confirming = true; changedAt = Time.unscaledTime;
            var sound = fSound.GetValue(page) as SoundManager;
            if (sound != null) sound.Play(SoundManager.SoundEffect.BUTTON_CLICK_POSITIVE);
            SetLabel(fMenuButton.GetValue(page) as GameObject, "Back", ref menuLabel);
            SetLabel(fQuitButton.GetValue(page) as GameObject, "Discard Changes", ref desktopLabel);
            Question(page, "You have unsaved changes.\nQuit anyway?");
        }

        // second step done: set the game's "save changes" value, then run the game's own code for where we are going.
        // Returns true when the button that was touched is already the right one (its own code then simply continues).
        static bool Finish(WristQuitPage page, bool save, bool touchedMenuButton)
        {
            Step where = step;
            fSave.SetValue(page, save);
            bool wantMenu = where == Step.ToMenu;
            if (wantMenu == touchedMenuButton) return true;
            passing = true;
            try
            {
                if (wantMenu) page.OnMainMenuButtonPressed(null); else page.OnQuitButtonPressed(null);
            }
            finally { passing = false; }
            return false;
        }

        // back to the first step, with the checkbox (and its label) out of sight
        static void Reset(WristQuitPage __instance)
        {
            try
            {
                step = Step.Where; confirming = false;
                RestoreLabel(fMenuButton.GetValue(__instance) as GameObject, ref menuLabel);
                RestoreLabel(fQuitButton.GetValue(__instance) as GameObject, ref desktopLabel);
                var heading = fLabel.GetValue(__instance) as GameObject;
                if (heading != null) heading.SetActive(false);
                if (question != null && questionWas != null) question.text = questionWas;
                var box = fCheckbox.GetValue(__instance) as GameObject;
                if (box != null) box.SetActive(false);
            }
            catch (Exception e) { Plugin.Log.LogWarning("QuitMenu: " + e.Message); }
        }

        // the page's own line, "Are you sure you want to quit?": it says where to, or warns of unsaved changes
        static void Question(WristQuitPage page, string words)
        {
            if (question == null)
                foreach (Text text in page.GetComponentsInChildren<Text>(true))
                    if (text.text != null && text.text.StartsWith("Are you sure")) { question = text; questionWas = text.text; break; }
            if (question == null) return;
            question.text = words;
            question.horizontalOverflow = HorizontalWrapMode.Wrap; question.verticalOverflow = VerticalWrapMode.Truncate;     // (kept inside its own space: it is drawn smaller instead of running over the buttons)
            question.resizeTextForBestFit = true; question.resizeTextMinSize = 10; question.resizeTextMaxSize = Mathf.Max(question.fontSize, 10);
        }

        // the wording on a button (the game uses UI text; 3D text is handled too in case a label is one)
        static void SetLabel(GameObject go, string text, ref string original)
        {
            if (go == null) return;
            var ui = go.GetComponentInChildren<Text>(true);
            if (ui != null)
            {
                if (original == null) original = ui.text;
                ui.text = text;
                ui.resizeTextForBestFit = true; ui.resizeTextMinSize = 8; ui.resizeTextMaxSize = Mathf.Max(ui.fontSize, 8);   // longer wording shrinks to fit
                return;
            }
            var mesh = go.GetComponentInChildren<TextMesh>(true);
            if (mesh != null) { if (original == null) original = mesh.text; mesh.text = text; }
        }

        static void RestoreLabel(GameObject go, ref string original)
        {
            if (go == null || original == null) return;
            var ui = go.GetComponentInChildren<Text>(true);
            if (ui != null) ui.text = original;
            else { var mesh = go.GetComponentInChildren<TextMesh>(true); if (mesh != null) mesh.text = original; }
        }
    }
}
