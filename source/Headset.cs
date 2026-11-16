// Tiny Town Enhanced - is the headset being worn?
// With an Oculus headset that isn't on your head, the Oculus software only lets the game draw a frame every couple of
// seconds. Anything in the game that advances "once per frame" (such as its loading) then crawls. Modules ask
// Headset.Idle and, when it is true, do their waiting in one go instead of spreading it over frames.
// Oculus says outright whether the headset is worn. For other headsets it can only be guessed from how slowly frames
// arrive (Guessed is then true). A guess needs two slow frames in a row, so one hitch does not count; and because a
// module that waits "in one go" lets no frames through, it must let two pass now and then for the guess to be made
// again (FastSync.cs does) - otherwise a guess of "idle" could never be taken back.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace TinyTownEnhanced
{
    public static class Headset
    {
        static PropertyInfo userPresent; static bool looked;
        static int lastFrame = -1, slowFrames;      // the last frame judged, and how many in a row were slow

        /// The VR software the game is running on ("OpenVR" for SteamVR, "Oculus", ...). Asking the engine makes a new
        /// piece of text each time, and it was asked several times a frame; it does not change while the game runs, so
        /// it is asked until it has an answer (at most once a second) and then kept.
        public static string Device
        {
            get
            {
                if (device.Length == 0 && Time.unscaledTime >= askDeviceAt)
                {
                    askDeviceAt = Time.unscaledTime + 1f;
                    try { device = XRSettings.loadedDeviceName ?? ""; } catch (Exception) { }
                }
                return device;
            }
        }
        static string device = ""; static float askDeviceAt = -1f;
        public static bool IsOpenVR { get { return Device == "OpenVR"; } }

        /// true when the last answer of Idle was a guess from the frame rate
        public static bool Guessed { get; private set; }

        public static bool Idle
        {
            get
            {
                try
                {
                    if (Device == "Oculus")
                    {
                        if (!looked) { looked = true; userPresent = AccessTools.Property(AccessTools.TypeByName("OVRPlugin"), "userPresent"); }
                        if (userPresent != null) { Guessed = false; return !(bool)userPresent.GetValue(null, null); }
                    }
                }
                catch (Exception) { }
                // other headsets: judge by how slowly frames are arriving (each frame is judged once)
                Guessed = true;
                if (Time.frameCount != lastFrame)
                {
                    lastFrame = Time.frameCount;
                    slowFrames = Time.unscaledDeltaTime > 0.5f ? slowFrames + 1 : 0;
                }
                return slowFrames >= 2;
            }
        }
    }
}
