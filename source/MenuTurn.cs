// Tiny Town Enhanced - you can turn yourself round in the main menu: the town and the menu panels turn about you, so
// it is as if you turned where you stand.
//   Hands: hold both triggers and move the hands around each other (as when turning a world with the game's rotate);
//          the town turns by as much as the line between the hands turns (as if you had hold of it).
//   Stick: push either thumbstick left or right. It turns the way Stick Turning is set in Settings > Controls (Snap:
//          one flick, one turn of Snap Angle; Smooth: turns while held, at Turn Speed), whether or not Stick Turning
//          itself is ticked (that one is for worlds).
// Everything turns about your head: the town, the menu, the title and the help panel. The game keeps you near one spot in
// the menu (MenuState.cameraOffset: where you should stand, measured from the town's middle) and puts everything back
// if you walk well away from it; that spot is turned along with the town, so turning never sets that off. When the
// game does put things back (or the menu is left), the spot is restored to the game's own.
// Off with [Menu] MenuTurning = false in the config file.
using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VRTK;

namespace TinyTownEnhanced
{
    public class MenuTurn : IModule
    {
        public string Name { get { return "MenuTurn"; } }
        static ConfigEntry<bool> on;
        static bool inMenu, flicked, both, failed; static float lastYaw;
        static readonly FieldInfo fRecentering = AccessTools.Field(typeof(MenuState), "recenterInProgress"),
                                  fCameraOffset = AccessTools.Field(typeof(MenuState), "cameraOffset");
        static readonly FieldInfo[] fPanels = { AccessTools.Field(typeof(MenuState), "menu"), AccessTools.Field(typeof(MenuState), "title"),
                                                AccessTools.Field(typeof(MenuState), "popupHelpCanvas") };
        static bool turned; static Vector3 gameOffset;          // the game's own cameraOffset, while it is turned

        public void Start(Plugin plugin)
        {
            on = plugin.Config.Bind("Menu", "MenuTurning", true, "The main menu's town can be turned with both triggers held, or with a thumbstick.");
            var h = plugin.Harmony; var me = typeof(MenuTurn);
            h.Patch(AccessTools.Method(typeof(MenuState), "LoadMenuWorld"), prefix: new HarmonyMethod(me, "Entering"));
            h.Patch(AccessTools.Method(typeof(MenuState), "Exit"), prefix: new HarmonyMethod(me, "Leaving"));
            h.Patch(AccessTools.Method(typeof(MenuState), "Recenter"), prefix: new HarmonyMethod(me, "Restore"));
            h.Patch(AccessTools.Method(typeof(MenuState), "Update"), postfix: new HarmonyMethod(me, "Frame"));
        }
        public void SceneLoaded(string scene) { inMenu = false; }
        public void Tick() { }

        static void Entering() { inMenu = true; both = false; flicked = false; }
        static void Leaving(MenuState __instance) { inMenu = false; Restore(__instance); }

        // the game puts the town back in front of you (or the menu is left): its own standing spot again
        static void Restore(MenuState __instance)
        {
            if (!turned) return;
            turned = false;
            try { fCameraOffset.SetValue(__instance, gameOffset); } catch (Exception) { }
        }

        static void Frame(MenuState __instance)
        {
            if (!inMenu || failed || !on.Value) return;
            try
            {
                if ((bool)fRecentering.GetValue(__instance)) { both = false; return; }
                var world = Singleton<WorldSettings>.Instance;
                if (world == null) return;
                float angle = Hands() + Stick();
                if (angle == 0f) return;
                Transform head = VRTK_DeviceFinder.HeadsetTransform();
                if (head == null) return;
                Vector3 pivot = head.position;
                world.RotateAbout(pivot, angle);
                foreach (var f in fPanels)
                {
                    var go = f.GetValue(__instance) as GameObject;
                    if (go != null) go.transform.RotateAround(pivot, Vector3.up, angle);
                }
                // (the spot you are kept near turns with the town, so it stays where you are)
                var offset = (Vector3)fCameraOffset.GetValue(__instance);
                if (!turned) { turned = true; gameOffset = offset; }
                fCameraOffset.SetValue(__instance, Quaternion.AngleAxis(angle, Vector3.up) * offset);
            }
            catch (Exception e) { failed = true; Plugin.Log.LogWarning("MenuTurn: switched off after an error: " + e); }
        }

        // both triggers held: the change in direction of the line from one hand to the other, about the upright
        static float Hands()
        {
            var l = Sticks.Events(0); var r = Sticks.Events(1);
            GameObject lc = Sticks.Controller(0), rc = Sticks.Controller(1);
            bool held = l != null && r != null && lc != null && rc != null && l.triggerPressed && r.triggerPressed;
            if (!held) { both = false; return 0f; }
            Vector3 across = rc.transform.position - lc.transform.position; across.y = 0f;
            if (across.sqrMagnitude < 0.0025f) return 0f;                       // (hands too close together to tell a direction)
            float yaw = Mathf.Atan2(across.x, across.z) * Mathf.Rad2Deg;
            if (!both) { both = true; lastYaw = yaw; return 0f; }
            float turn = Mathf.DeltaAngle(lastYaw, yaw); lastYaw = yaw;
            return turn;                                                        // (the town turns with the hands)
        }

        // a thumbstick left or right (whichever is pushed further), snap or smooth as set for Stick Turning
        static float Stick()
        {
            float a = Sticks.Left.x, b = Sticks.Right.x;
            float x = Mathf.Abs(a) > Mathf.Abs(b) ? a : b;
            if (StickMove.TurnStyle == 1) return -x * StickMove.TurnSpeed * Time.deltaTime;
            if (Mathf.Abs(x) < 0.3f) { flicked = false; return 0f; }
            if (flicked || Mathf.Abs(x) < 0.7f) return 0f;
            flicked = true;
            // (pushed right you turn right, as with Stick Turning in a world: the town goes the other way)
            return -Mathf.Sign(x) * StickMove.SnapAngle;
        }
    }
}
