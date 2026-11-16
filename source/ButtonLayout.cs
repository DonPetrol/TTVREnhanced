// Tiny Town Enhanced - Oculus button layout under SteamVR (Settings > Controls, "Fix Oculus Controls").
//
// The game picks its buttons from the VR software, not from the controllers. Through the Oculus app (Link, Air Link)
// it uses the Touch layout: grip grabs, A/X deletes, B/Y undoes. Through SteamVR (ALVR, Virtual Desktop, Steam Link)
// it uses the layout made for Vive wands, even with the very same Touch controllers: the stick click grabs, the grip
// deletes, A/X does nothing. The build menu also scrolls differently (dragging, as on a trackpad, instead of flicks).
//
// With this on, when SteamVR reports Oculus controllers (or an Oculus headset), the game is told to use its Oculus
// layout when a world is opened: PlayState.IsOculus is set just before PlayState.Enter, which is where the game chooses
// which buttons to listen to, and kept the same for PlayState.Exit (which stops listening to the same buttons). The
// build menu (Inventory) is given the same answer. Vibration strength is left as the game sets it for SteamVR.
// Vive wands, Index and Windows Mixed Reality controllers keep the game's SteamVR layout.
// A change to the setting takes effect the next time a world is opened.
using System;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine.XR;
using Valve.VR;
using VRTK;

namespace TinyTownEnhanced
{
    public class ButtonLayout : IModule
    {
        public string Name { get { return "ButtonLayout"; } }
        static ConfigEntry<bool> setting;

        /// true while the open world uses the Oculus layout because of this module (the game itself chose SteamVR's)
        public static bool Applied { get; private set; }
        static bool entered;            // the layout chosen when the world was opened, kept for Exit

        public void Start(Plugin plugin)
        {
            setting = plugin.Config.Bind("Controls", "OculusLayoutOnSteamVR", true,
                "With Oculus (Quest / Rift) controllers under SteamVR (ALVR, Virtual Desktop, Steam Link): grip grabs, A/X deletes, B/Y undoes, as through the Oculus app. Takes effect when a world is next opened.");
            SettingsTabs.AddCheckbox("Controls", "Fix Oculus Controls",
                "Quest and Rift controllers through SteamVR use the same buttons as through the Oculus app: grip grabs, A/X deletes, B/Y undoes. Applies when a world is next opened.",
                setting, null, () => Headset.IsOpenVR);
            var h = plugin.Harmony;
            h.Patch(AccessTools.Method(typeof(PlayState), "Enter"), prefix: new HarmonyMethod(typeof(ButtonLayout), "BeforeEnter"));
            h.Patch(AccessTools.Method(typeof(PlayState), "Exit"), prefix: new HarmonyMethod(typeof(ButtonLayout), "BeforeExit"));
            h.Patch(AccessTools.Method(typeof(Inventory), "Update"), prefix: new HarmonyMethod(typeof(ButtonLayout), "BeforeMenu"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // a world is being opened: choose the layout (the game's own choice unless this setting applies)
        static void BeforeEnter(PlayState __instance)
        {
            try
            {
                bool game = VRTK_SDK_Bridge.GetSystemSDK() is SDK_OculusSystem;
                Applied = !game && setting.Value && OculusUnderSteamVR();
                entered = game || Applied;
                __instance.IsOculus = entered;
                if (Applied) Plugin.Log.LogInfo("ButtonLayout: Oculus controllers under SteamVR - using the game's Oculus button layout");
            }
            catch (Exception e) { Plugin.Log.LogWarning("ButtonLayout: " + e.Message); }
        }

        // leaving the world: the game stops listening to the buttons it chose on Enter, so it must get the same answer
        static void BeforeExit(PlayState __instance)
        {
            try { __instance.IsOculus = entered; } catch (Exception) { }
            Applied = false;
        }

        // the build menu: scrolled by stick flicks, as with the Oculus app
        static void BeforeMenu(ref bool ___isOculus, bool ___vrSystemDetermined)
        {
            if (Applied && ___vrSystemDetermined) ___isOculus = true;
        }

        /// SteamVR is running and the controllers (or, if no controller answers, the headset) are Oculus ones
        static bool OculusUnderSteamVR()
        {
            if (!Headset.IsOpenVR) return false;
            CVRSystem sys = OpenVR.System;
            if (sys == null) return false;
            bool anyController = false;
            foreach (var role in new[] { ETrackedControllerRole.LeftHand, ETrackedControllerRole.RightHand })
            {
                uint i = sys.GetTrackedDeviceIndexForControllerRole(role);
                if (i >= OpenVR.k_unMaxTrackedDeviceCount) continue;
                anyController = true;
                if (IsOculus(sys, i)) return true;
            }
            return !anyController && IsOculus(sys, OpenVR.k_unTrackedDeviceIndex_Hmd);
        }
        static bool IsOculus(CVRSystem sys, uint index)
        {
            return Str(sys, index, ETrackedDeviceProperty.Prop_ManufacturerName_String).IndexOf("oculus", StringComparison.OrdinalIgnoreCase) >= 0
                || Str(sys, index, ETrackedDeviceProperty.Prop_RenderModelName_String).StartsWith("oculus", StringComparison.OrdinalIgnoreCase);
        }
        static string Str(CVRSystem sys, uint index, ETrackedDeviceProperty p)
        {
            var err = ETrackedPropertyError.TrackedProp_Success;
            uint n = sys.GetStringTrackedDeviceProperty(index, p, null, 0u, ref err);
            if (n <= 1) return "";
            var b = new StringBuilder((int)n);
            sys.GetStringTrackedDeviceProperty(index, p, b, n, ref err);
            return b.ToString();
        }
    }
}
