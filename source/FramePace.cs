// Tiny Town Enhanced - things the game does "a fixed amount per frame", made to run the same at any frame rate:
// the shared measure of how slow the current frame is (FrameRate), swiping the build menu (MenuSwipe) and the
// physics clock (PhysicsPace).
// ----------------------------------------------------------------------------------------------------
// FrameRate - helper for things the game does "a fixed amount per frame".
// Boost is how many full-speed frames the current frame is worth: 1 at the headset's full frame rate, 2 when the
// game is running at half of it, and so on. Multiplying a per-frame amount by it makes that amount per second again.
using System;
using HarmonyLib;
using UnityEngine.XR;
using UnityEngine;

namespace TinyTownEnhanced
{
    public static class FrameRate
    {
        const float MaxBoost = 4f;          // never more than this, however long a frame took

        static float full = 90f, askAgain;

        /// the headset's full frame rate (72, 90, ...)
        public static float Full
        {
            get
            {
                // (asked of the headset twice a second, not on every use: this is read several times a frame, and the
                //  rate only changes when the headset or its settings do)
                if (Time.unscaledTime >= askAgain)
                {
                    askAgain = Time.unscaledTime + 0.5f;
                    float r = 0f; try { r = XRDevice.refreshRate; } catch (Exception) { }
                    full = r < 30f ? 90f : r;
                }
                return full;
            }
        }

        public static float Boost { get { return Mathf.Clamp(Time.deltaTime * Full, 1f, MaxBoost); } }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - swiping through the build menu works the same at any frame rate.
    // The game moves the menu by "hand speed x a fixed amount" once per frame. That was tuned for a full frame rate;
    // in a heavy world running at half the frame rate, the menu gets half as many pushes per second, so it crawls and
    // feels as if swipes are ignored. This module scales each push by how long the frame really took, so one swipe
    // moves the menu the same distance however fast the game is running. At full frame rate nothing changes.
    public class MenuSwipe : IModule
    {
        public string Name { get { return "MenuSwipe"; } }
        public void Start(Plugin plugin)
        {
            plugin.Harmony.Patch(AccessTools.Method(typeof(Inventory), "GetHandDeltas"), null, new HarmonyMethod(typeof(MenuSwipe), "Scale"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Scale(ref float deltaX, ref float deltaY)
        {
            float boost = FrameRate.Boost;                          // 1 at full frame rate (see FrameRate, above)
            deltaX *= boost; deltaY *= boost;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - the physics step is matched to the headset.
    // The engine runs its physics on a fixed clock, 90 times a second in this game, whatever the headset does. On a
    // 72 Hz headset that is 1.25 physics steps for every frame at best, and more when frames are slow. Here the clock is
    // set to the headset's own rate, so a full-speed frame gets exactly one step. Things that fall still fall at the
    // same real speed; there are just slightly fewer, slightly longer steps.
    public class PhysicsPace : IModule
    {
        public string Name { get { return "PhysicsPace"; } }

        static bool said;

        public void Start(Plugin plugin) { }
        public void SceneLoaded(string scene) { Match(); }
        public void Tick() { Match(); }                 // (the headset's rate isn't known until it has started; and keep it if something resets it)

        static void Match()
        {
            try
            {
                float rate = XRDevice.refreshRate;
                if (rate < 30f || rate > 200f) return;
                float step = 1f / rate;
                if (Mathf.Abs(Time.fixedDeltaTime - step) < 0.0001f) return;
                float before = Time.fixedDeltaTime;
                Time.fixedDeltaTime = step;
                if (!said) { said = true; Plugin.Log.LogInfo("PhysicsPace: physics step " + (before * 1000f).ToString("0.0") + " ms -> " + (step * 1000f).ToString("0.0") + " ms (headset " + rate.ToString("0") + " Hz)"); }
            }
            catch (Exception) { }
        }
    }
}
