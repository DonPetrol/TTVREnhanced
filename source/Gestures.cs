// Tiny Town Enhanced - the two-handed world gestures behave the same at any frame rate, and zooming keeps you in place.
//   - Zoom (pinch): the game changed the scale by a fixed amount per frame, so zooming took twice as long at half
//     the frame rate. The change is now scaled by the real frame time.
//   - Optional (the "Head-based Zoom" checkbox in Settings > Controls, off by default): the game zooms around a point in front
//     of your hands, which slides the world under you. With the option on it zooms around your head, in every
//     direction, so you stay exactly where you are.
//   - Glide after letting go of a pan: the slow-down used the frame time twice, so the glide stopped short at low
//     frame rates. It now slows down at the same rate per second as it does at full frame rate.
//   - Starting a rotate: the game waits for a number of frames of the right hand movement before it starts turning
//     the world. Slow frames now count for more, so it starts after the same amount of time.
// At full frame rate, with the option off, everything is exactly as in the original game.
using System.Reflection;
using System;
using HarmonyLib;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class Gestures : IModule
    {
        public string Name { get { return "Gestures"; } }
        /// false = the game's own zoom centre (in front of your hands); true = your head. Set by the Settings checkbox (ZoomOption, at the end of this file).
        public static bool ZoomAroundPlayer;

        static readonly FieldInfo fScaleOrigin = AccessTools.Field(typeof(ControllerPinch), "scaleOrigin"),
                                  fDeceleration = AccessTools.Field(typeof(WorldSettings), "deceleration"),
                                  fRotateCount = AccessTools.Field(typeof(ControllerRotate), "consecutivePerpendicularFrameCount");
        static float rotateCredit;
        // the game's own "shadows reach this far at this zoom" step, and the play state it belongs to (see ZoomTo)
        static readonly MethodInfo mShadowDistance = AccessTools.Method(typeof(PlayState), "UpdateShadowDistance");
        static PlayState play;

        public void Start(Plugin plugin)
        {
            unlocked = plugin.Config.Bind("Gestures", "UnlockWorldScale", false, "The world can be zoomed past the game's limits (0.2x to 4x), from 0.01x to 100x.");
            plugin.Harmony.Patch(AccessTools.PropertySetter(typeof(WorldSettings), "Scale"), new HarmonyMethod(typeof(Gestures), "ScaleSet"));
            var h = plugin.Harmony; var me = typeof(Gestures);
            h.Patch(AccessTools.Method(typeof(ControllerPinch), "UpdateWorldTransform"), new HarmonyMethod(me, "ZoomBefore"), new HarmonyMethod(me, "ZoomAfter"));
            h.Patch(AccessTools.Method(typeof(WorldSettings), "Update"), new HarmonyMethod(me, "Glide"));
            h.Patch(AccessTools.Method(typeof(ControllerRotate), "UpdateWorldTransform"), new HarmonyMethod(me, "RotateBefore"), new HarmonyMethod(me, "RotateAfter"));
            h.Patch(AccessTools.Method(typeof(PlayState), "Enter"), new HarmonyMethod(me, "Entered"));
        }
        static void Entered(PlayState __instance) { play = __instance; }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // ---- zoom
        public struct Before { public float Scale; public Vector3 Offset; }

        public static void ZoomBefore(out Before __state)
        {
            __state = new Before { Scale = float.NaN };
            // (an error here would otherwise land in the game's own per-frame code; without a "before", ZoomAfter leaves the game's zoom as it is)
            try
            {
                var ws = Singleton<WorldSettings>.Instance;
                __state = new Before { Scale = ws.Scale, Offset = ws.Offset };
            }
            catch (Exception e) { Plugin.Log.LogWarning("Gestures (zoom): " + e.Message); }
        }

        // the game has just zoomed by its per-frame amount: undo that and zoom by the per-second amount, around the player
        public static void ZoomAfter(ControllerPinch __instance, bool __result, Before __state)
        {
            if (!__result || float.IsNaN(__state.Scale)) return;
            try
            {
                var ws = Singleton<WorldSettings>.Instance;
                float change = (ws.Scale - __state.Scale) * FrameRate.Boost;
                if (change == 0f) return;
                ws.Scale = __state.Scale; ws.Offset = __state.Offset;

                Vector3 centre = (Vector3)fScaleOrigin.GetValue(__instance);             // the game's zoom centre, in world units
                if (ZoomAroundPlayer) centre = ws.ToPhysicsPosition(ws.GetHeadWorldPosition());
                Vector3 was = ws.ToDisplayPosition(centre);
                ws.Scale = __state.Scale + change;
                ws.Offset -= ws.ToDisplayPosition(centre) - was;                         // keep the centre where it was
            }
            catch (Exception e) { Plugin.Log.LogWarning("Gestures (zoom): " + e.Message); }
        }

        // ---- Unlock World Scale (the checkbox is on the World tab's Snapping page: WorldTab.cs).
        // The game keeps the zoom between 0.2x and 4x, in the one place every zoom goes through (WorldSettings.Scale).
        // Unlocked, that place lets it go from 0.01x to 100x instead. (The game's own limits are left as they are:
        // other things, such as how far shadows reach, are worked out from them.)
        public const float GameSmallest = 0.2f, GameLargest = 4f, Smallest = 0.01f, Largest = 100f;
        static BepInEx.Configuration.ConfigEntry<bool> unlocked;
        public static bool Unlocked
        {
            get { return unlocked != null && unlocked.Value; }
            set
            {
                if (unlocked == null) return;
                unlocked.Value = value;
                // (locked again: back inside the game's limits at once)
                try { var ws = Singleton<WorldSettings>.Instance; if (!value && ws != null && (ws.Scale < GameSmallest || ws.Scale > GameLargest)) ZoomTo(Mathf.Clamp(ws.Scale, GameSmallest, GameLargest)); }
                catch (Exception) { }
            }
        }
        static bool ScaleSet(WorldSettings __instance, float value)
        {
            if (!Unlocked || __instance.DisplayWorld == null) return true;
            float scale = Mathf.Clamp(value, Smallest, Largest);
            __instance.DisplayWorld.localScale = new Vector3(scale, scale, scale);
            return false;
        }

        /// zoom the world to a scale, around the viewer's head (the point of the world at your head stays there);
        /// the game keeps the scale within its own limits
        public static void ZoomTo(float scale)
        {
            var ws = Singleton<WorldSettings>.Instance;
            Vector3 centre = ws.ToPhysicsPosition(ws.GetHeadWorldPosition());
            Vector3 was = ws.ToDisplayPosition(centre); float before = ws.Scale;
            ws.Scale = scale;
            ws.Offset -= ws.ToDisplayPosition(centre) - was;
            // The game sets how far shadows reach from the zoom, but only when a world is entered and during its own
            // two-handed pinch. A zoom made here (the World Scale slider, the stick) has to ask for that step itself,
            // or the shadows keep the reach of the old zoom (VideoOptions' Shadow Distance hangs on the same step).
            if (ws.Scale == before) return;
            try { if (play != null && mShadowDistance != null) mShadowDistance.Invoke(play, null); }
            catch (Exception e) { Plugin.Log.LogWarning("Gestures (shadow distance): " + e.Message); }
        }

        // ---- glide: the game's WorldSettings.Update, with the slow-down per second instead of per frame-squared
        /// false: the world stops dead when a grab-and-pull is let go, instead of drifting on (Settings > Controls)
        public static bool GrabDrift = true;
        static Vector3 expected; static bool stickOwns;             // the glide speed as this module left it, and whether the stick set it

        /// the thumbstick sets how fast the world is sliding (StickMove.cs); it then drifts to a stop like a grab does
        public static void StickVelocity(Vector3 velocity)
        {
            Singleton<WorldSettings>.Instance.Velocity = velocity;
            expected = Singleton<WorldSettings>.Instance.Velocity; stickOwns = true;
        }

        // The game's view reaches a fixed distance: all of the world at 1x, and less and less of it as the world is
        // made bigger - even at the game's own 4x the far side is cut off. So whenever the world is bigger than 1x
        // the view reaches as many times further as the world is bigger, and shows what 1x shows. (Whether or not
        // the zoom's limits are lifted.) Shadows (VideoOptions.cs) are the game's own up to its limit of 4x, and
        // past it reach further by how far past it the zoom is ("Beyond").
        public static float Beyond { get { var ws = Singleton<WorldSettings>.Instance; return ws != null ? Mathf.Max(1f, ws.Scale / GameLargest) : 1f; } }
        static float fittedTo = -1f, gameFar = -1f; static Camera fittedCamera;
        static void FitView(float scale)
        {
            Camera camera = Eye.Camera;
            if (camera == null) return;
            if (camera != fittedCamera) { fittedCamera = camera; gameFar = camera.farClipPlane; }     // (the game's own reach, taken before it is first changed)
            fittedTo = scale;
            camera.farClipPlane = gameFar * Mathf.Max(1f, scale);
        }

        static bool Glide(WorldSettings __instance)
        {
            try { float scale = __instance.Scale; if (scale != fittedTo) FitView(scale); } catch (Exception) { }
            try
            {
                float dt = Time.deltaTime;
                Vector3 v = __instance.Velocity;
                // a speed nobody here set has come from the game: a grab was let go (or the game stopped the world)
                if (v != expected) stickOwns = false;
                if (!stickOwns && !GrabDrift && v != Vector3.zero) { __instance.Velocity = v = Vector3.zero; }
                __instance.Offset += v * dt;
                if (v.magnitude > 0.0001f)
                {
                    float deceleration = (float)fDeceleration.GetValue(__instance);
                    Vector3 slower = v - v.normalized * deceleration * dt / FrameRate.Full;   // (at full frame rate dt = 1 / Full: exactly the game's amount)
                    __instance.Velocity = new Vector3(Stop(v.x, slower.x), Stop(v.y, slower.y), Stop(v.z, slower.z));
                }
                expected = __instance.Velocity;
                return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Gestures (glide): " + e.Message); return true; }
        }

        static float Stop(float before, float after) { return Mathf.Sign(before) != Mathf.Sign(after) ? 0f : after; }

        // ---- rotate: frames spent recognising the gesture count for the time they took
        static void RotateBefore(ControllerRotate __instance, out int __state)
        {
            __state = int.MinValue;                                 // (no "before": RotateAfter then counts nothing extra for this frame)
            try { __state = (int)fRotateCount.GetValue(__instance); } catch (Exception) { }
        }

        static void RotateAfter(ControllerRotate __instance, int __state)
        {
            try
            {
                int count = (int)fRotateCount.GetValue(__instance);
                if (count == 0) { rotateCredit = 0f; return; }
                if (count != __state + 1) return;                   // not counting this frame
                rotateCredit += FrameRate.Boost - 1f;
                int extra = (int)rotateCredit;
                if (extra <= 0) return;
                rotateCredit -= extra;
                fRotateCount.SetValue(__instance, count + extra);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Gestures (rotate): " + e.Message); }
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - the "Head-based Zoom" checkbox (Settings > Controls).
    // Off (the default): zooming works as in the original game, around a point in front of your hands.
    // On: the world zooms around your head, so you stay where you are (see Gestures.cs).
    public class ZoomOption : IModule
    {
        public string Name { get { return "ZoomOption"; } }

        public void Start(Plugin plugin)
        {
            var setting = plugin.Config.Bind("Gestures", "ZoomAroundMe", false, "Zoom the world around your head instead of a point in front of your hands.");
            SettingsTabs.AddCheckbox("Controls", "Head-based Zoom", "The world zooms around your head, so you stay where you are.",
                                     setting, delegate (bool on) { Gestures.ZoomAroundPlayer = on; });
            GrabDriftRow(plugin);
        }
        public void SceneLoaded(string scene) { }
        // (the other Controls checkbox that belongs to the game's own gestures is added here too)
        static void GrabDriftRow(Plugin plugin)
        {
            var drift = plugin.Config.Bind("Gestures", "GrabDrift", true, "The world drifts on for a moment after a grab-and-pull is let go (the game's own behaviour).");
            SettingsTabs.AddCheckbox("Controls", "Grab Movement Drift", "The world drifts on for a moment when you let go of a grab.", drift, delegate (bool on) { Gestures.GrabDrift = on; });
        }
        public void Tick() { }
    }
}
