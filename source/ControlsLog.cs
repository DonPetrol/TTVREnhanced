// Tiny Town Enhanced - diagnostic: writes the controls to the log (BepInEx\LogOutput.log), to see which physical
// button does what on a given headset setup (for example Quest by Link versus Quest through ALVR / SteamVR).
//
// The game picks its button layout once, from the VR software it finds:
//   Oculus software:      grab/use = Grip,              delete = ButtonOne (A/X),  undo = ButtonTwo (B/Y)
//   anything else (SteamVR, ALVR, Virtual Desktop by SteamVR):
//                         grab/use = Touchpad (stick click on a thumbstick controller), delete = Grip,
//                         undo = ButtonTwo (which VRTK reads from SteamVR's "ApplicationMenu" button)
// So under SteamVR it all depends on which physical button the streaming software reports as ApplicationMenu and as
// the touchpad press. This module logs:
//   - once per world: the VR software, the headset, the controllers' names, and the layout the game chose;
//   - every press and release the game sees (VRTK's buttons), with the game action it triggers;
//   - under SteamVR, every raw button SteamVR reports for each hand (by its SteamVR name), so the two can be compared.
// Nothing is changed: it only listens. Switch it off with [Diagnostics] LogControls = false in the config file.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;
using VRTK;

namespace TinyTownEnhanced
{
    public class ControlsLog : IModule
    {
        public string Name { get { return "ControlsLog"; } }
        static BepInEx.Configuration.ConfigEntry<bool> on;
        static PlayState play; static VRTK_ControllerEvents[] hooked = new VRTK_ControllerEvents[2];

        public void Start(Plugin plugin)
        {
            // (off by default since 1.1.1; the setting was renamed from LogControls so that a settings file which still says
            //  "true" from when it was on by default does not keep it on)
            on = plugin.Config.Bind("Diagnostics", "LogControlPresses", false,
                "Write the controller layout, every button press the game sees, and (under SteamVR) every raw button to the log. For checking controls; turn off when done.");
            if (on.Value) Plugin.Instance.gameObject.AddComponent<RawPoll>();      // (the every-frame part is only there when it is on at start-up)
        }
        public void SceneLoaded(string scene) { play = null; }

        // about twice a second: once the world's play state and its controllers are there, describe them and listen
        public void Tick()
        {
            if (!on.Value) return;
            if (play == null) play = UnityEngine.Object.FindObjectOfType<PlayState>();
            if (play == null) return;
            var l = play.leftControllerEvents; var r = play.rightControllerEvents;
            if (l == hooked[0] && r == hooked[1]) return;
            Hook(0, l); Hook(1, r);
            Describe();
        }

        static string Layout(bool oculus, string button)
        {
            if (oculus)
                switch (button) { case "Grip": return "grab/use"; case "ButtonOne": return "delete"; case "ButtonTwo": return "undo"; }
            else
                switch (button) { case "Touchpad": return "grab/use"; case "Grip": return "delete"; case "ButtonTwo": return "undo"; }
            switch (button) { case "Trigger": return "game's trigger actions (menu pointer, zoom/move with both)"; case "StartMenu": return "wrist menu / system"; }
            return "nothing in the game";
        }

        static void Describe()
        {
            var sb = new StringBuilder("Controls: ");
            try
            {
                var sdk = VRTK_SDK_Bridge.GetSystemSDK();
                sb.Append("XR device '").Append(XRSettings.loadedDeviceName).Append("', headset '").Append(XRDevice.model)
                  .Append("', VRTK system SDK ").Append(sdk != null ? sdk.GetType().Name : "(none)")
                  .Append(", game uses the ").Append(play.IsOculus ? "OCULUS" : "STEAMVR / OTHER").Append(" layout")
                  .Append(ButtonLayout.Applied ? " (chosen by TTE's Fix Oculus Controls)" : "");
                foreach (string b in new[] { "Trigger", "Grip", "Touchpad", "ButtonOne", "ButtonTwo", "StartMenu" })
                    sb.Append("\n    ").Append(b).Append(" -> ").Append(Layout(play.IsOculus, b));
                if (Headset.IsOpenVR) sb.Append(RawPoll.Devices());
            }
            catch (Exception e) { sb.Append(" (").Append(e.Message).Append(")"); }
            Plugin.Log.LogInfo(sb.ToString());
        }

        // listen to the same VRTK events the game listens to
        static void Hook(int hand, VRTK_ControllerEvents ev)
        {
            if (hooked[hand] == ev) return;
            hooked[hand] = ev;
            if (ev == null) return;
            string side = hand == 0 ? "Left" : "Right";
            ControllerInteractionEventHandler P(string b, bool down)
            {
                return delegate (object s, ControllerInteractionEventArgs e)
                {
                    if (!on.Value) return;
                    bool oc = play != null && play.IsOculus;
                    Plugin.Log.LogInfo("Controls: " + side + " " + b + (down ? " pressed  -> " + Layout(oc, b) : " released"));
                };
            }
            ev.TriggerPressed += P("Trigger", true); ev.TriggerReleased += P("Trigger", false);
            ev.GripPressed += P("Grip", true); ev.GripReleased += P("Grip", false);
            ev.TouchpadPressed += P("Touchpad", true); ev.TouchpadReleased += P("Touchpad", false);
            ev.ButtonOnePressed += P("ButtonOne", true); ev.ButtonOneReleased += P("ButtonOne", false);
            ev.ButtonTwoPressed += P("ButtonTwo", true); ev.ButtonTwoReleased += P("ButtonTwo", false);
            ev.StartMenuPressed += P("StartMenu", true); ev.StartMenuReleased += P("StartMenu", false);
        }

        // ---- SteamVR's own view: the raw buttons of each hand, every frame, logged when they change
        class RawPoll : MonoBehaviour
        {
            static readonly ulong[] last = new ulong[2]; static readonly uint[] lastIndex = { uint.MaxValue, uint.MaxValue };
            static readonly uint size = (uint)Marshal.SizeOf(typeof(VRControllerState_t));

            void Update()
            {
                if (on == null || !on.Value || play == null || !Headset.IsOpenVR) return;
                try
                {
                    CVRSystem sys = OpenVR.System;
                    if (sys == null) return;
                    for (int hand = 0; hand < 2; hand++)
                    {
                        uint index = sys.GetTrackedDeviceIndexForControllerRole(hand == 0 ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
                        if (index >= OpenVR.k_unMaxTrackedDeviceCount) continue;
                        var st = new VRControllerState_t();
                        if (!sys.GetControllerState(index, ref st, size)) continue;
                        if (index != lastIndex[hand]) { lastIndex[hand] = index; last[hand] = st.ulButtonPressed; continue; }
                        ulong changed = st.ulButtonPressed ^ last[hand];
                        if (changed == 0) continue;
                        for (int bit = 0; bit < 64; bit++)
                        {
                            ulong m = 1UL << bit;
                            if ((changed & m) == 0) continue;
                            Plugin.Log.LogInfo("Controls: raw SteamVR " + (hand == 0 ? "Left" : "Right") + " " + ButtonName(bit) +
                                ((st.ulButtonPressed & m) != 0 ? " pressed" : " released") + " (VRTK calls it " + VrtkName(bit) + ")");
                        }
                        last[hand] = st.ulButtonPressed;
                    }
                }
                catch (Exception) { }
            }

            static string ButtonName(int bit)
            {
                switch (bit)
                {
                    case 0: return "System";
                    case 1: return "ApplicationMenu";
                    case 2: return "Grip";
                    case 7: return "A";
                    case 32: return "Axis0 (touchpad/stick click)";
                    case 33: return "Axis1 (trigger)";
                    case 34: return "Axis2";
                    case 35: return "Axis3";
                    case 36: return "Axis4";
                }
                return "button " + bit;
            }
            static string VrtkName(int bit)
            {
                switch (bit)
                {
                    case 0: return "StartMenu";
                    case 1: return "ButtonTwo";
                    case 2: return "Grip";
                    case 7: return "ButtonOne";
                    case 32: return "Touchpad";
                    case 33: return "Trigger";
                }
                return "nothing";
            }

            /// the controllers' names and what kind each input is, as SteamVR reports them
            public static string Devices()
            {
                var sb = new StringBuilder();
                CVRSystem sys = OpenVR.System;
                if (sys == null) return "\n    (SteamVR not answering)";
                for (int hand = 0; hand < 2; hand++)
                {
                    uint index = sys.GetTrackedDeviceIndexForControllerRole(hand == 0 ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
                    sb.Append("\n    ").Append(hand == 0 ? "Left" : "Right").Append(" controller (device ").Append(index).Append("): ");
                    if (index >= OpenVR.k_unMaxTrackedDeviceCount) { sb.Append("not found"); continue; }
                    foreach (var p in new[] { ETrackedDeviceProperty.Prop_TrackingSystemName_String, ETrackedDeviceProperty.Prop_ManufacturerName_String,
                                              ETrackedDeviceProperty.Prop_ModelNumber_String, ETrackedDeviceProperty.Prop_RenderModelName_String })
                        sb.Append(Str(sys, index, p)).Append(" | ");
                    sb.Append("inputs:");
                    for (int axis = 0; axis < 5; axis++)
                    {
                        var err = ETrackedPropertyError.TrackedProp_Success;
                        var t = (EVRControllerAxisType)sys.GetInt32TrackedDeviceProperty(index, ETrackedDeviceProperty.Prop_Axis0Type_Int32 + axis, ref err);
                        if (err == ETrackedPropertyError.TrackedProp_Success && t != EVRControllerAxisType.k_eControllerAxis_None)
                            sb.Append(" ").Append(axis).Append("=").Append(t.ToString().Replace("k_eControllerAxis_", ""));
                    }
                    var e2 = ETrackedPropertyError.TrackedProp_Success;
                    ulong buttons = sys.GetUint64TrackedDeviceProperty(index, ETrackedDeviceProperty.Prop_SupportedButtons_Uint64, ref e2);
                    if (e2 == ETrackedPropertyError.TrackedProp_Success)
                    {
                        sb.Append(" | buttons:");
                        for (int bit = 0; bit < 64; bit++) if ((buttons & (1UL << bit)) != 0) sb.Append(" ").Append(ButtonName(bit));
                    }
                }
                return sb.ToString();
            }
            static string Str(CVRSystem sys, uint index, ETrackedDeviceProperty p)
            {
                var err = ETrackedPropertyError.TrackedProp_Success;
                uint n = sys.GetStringTrackedDeviceProperty(index, p, null, 0u, ref err);
                if (n <= 1) return "?";
                var b = new StringBuilder((int)n);
                sys.GetStringTrackedDeviceProperty(index, p, b, n, ref err);
                return b.ToString();
            }
        }
    }
}
