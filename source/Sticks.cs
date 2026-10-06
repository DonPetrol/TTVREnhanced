// Tiny Town Enhanced - helper: the left and right thumbstick, whatever the headset.
// Modules ask Sticks.Left / Sticks.Right and get a direction (x right, y forward, each -1 to 1; zero when at rest).
//   Oculus software (Rift, Quest by Link / Air Link): the thumbstick, as the game itself reads it.
//   SteamVR (Vive, Index, Windows Mixed Reality, anything else): the controller is asked what each of its inputs is.
//     If it has a thumbstick, that is used (whichever input it is on; if it has two, the one being pushed further).
//     If it only has a trackpad (Vive wands), the trackpad is used by TOUCH: where the thumb rests on the pad gives the
//     direction, with a larger dead area in the middle so a thumb resting near the centre does nothing. (Not by
//     pressing it in: on these controllers the game uses that press to grab things.)
// Sticks.LeftClick / RightClick: is the stick pressed in. Only reported where that press is free: under the Oculus
// software, and under SteamVR for a thumbstick that is not the control the game uses for grabbing. Otherwise false.
//   Anything that cannot be asked falls back to the game's own reading.
// The first time each hand is read, what was found is written to the log.
// What a SteamVR controller's inputs are is asked once per controller and remembered: it does not change while the
// controller stays the same, and asking is five calls into SteamVR per hand.
using System;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;
using VRTK;

namespace TinyTownEnhanced
{
    public static class Sticks
    {
        const float DeadZone = 0.2f, PadDeadZone = 0.35f;
        static readonly Vector2[] value = new Vector2[2]; static readonly int[] frame = { -1, -1 }; static readonly string[] told = new string[2];
        static readonly GameObject[] seen = new GameObject[2]; static readonly VRTK_ControllerEvents[] listener = new VRTK_ControllerEvents[2];

        public static Vector2 Left { get { return Read(0); } }
        public static Vector2 Right { get { return Read(1); } }
        public static bool LeftClick { get { Read(0); return click[0]; } }
        public static bool RightClick { get { Read(1); return click[1]; } }
        static readonly bool[] click = new bool[2];

        /// the game's own reader of a hand's buttons (0 left, 1 right), as last found by Read; null if that hand has none
        public static VRTK_ControllerEvents Events(int hand) { Read(hand); return listener[hand]; }

        // SteamVR, per hand: the device the inputs were asked of (-1: not asked yet), which of its inputs are
        // thumbsticks (one bit each), whether input 0 is a trackpad, and the wording for the log with the input it names
        static readonly long[] askedOf = { -1, -1 }; static readonly int[] stickInputs = new int[2]; static readonly bool[] hasPad = new bool[2];
        static readonly string[] wording = new string[2]; static readonly int[] wordingFor = { -1, -1 };

        /// the controller object the game uses for a hand (0 left, 1 right)
        public static GameObject Controller(int hand) { return hand == 0 ? VRTK_DeviceFinder.GetControllerLeftHand() : VRTK_DeviceFinder.GetControllerRightHand(); }

        static Vector2 Read(int hand)
        {
            if (frame[hand] == Time.frameCount) return value[hand];
            frame[hand] = Time.frameCount;
            Vector2 v = Vector2.zero; string how = "none"; bool pressed = false; float dead = DeadZone;
            try
            {
                GameObject controller = Controller(hand);
                if (controller != seen[hand]) { seen[hand] = controller; askedOf[hand] = -1; listener[hand] = controller != null ? controller.GetComponent<VRTK_ControllerEvents>() : null; }
                var events = listener[hand];
                if (events != null)
                {
                    v = events.GetTouchpadAxis(); how = "the game's own reading";
                    if (XRSettings.loadedDeviceName == "OpenVR")
                    {
                        try { how = SteamVR(hand, controller, ref v, ref pressed, ref dead); } catch (Exception) { }      // (not there, or not answering: keep the game's reading)
                    }
                    else pressed = events.touchpadPressed;           // (Oculus software: the game grabs with the grip, so the stick's click is free)
                }
            }
            catch (Exception) { v = Vector2.zero; pressed = false; }
            click[hand] = pressed;
            if (how != told[hand] && how != "none") { told[hand] = how; Plugin.Log.LogInfo("Sticks: " + (hand == 0 ? "left" : "right") + " hand uses " + how); }
            float tilt = v.magnitude;
            value[hand] = tilt < dead ? Vector2.zero : v / tilt * Mathf.InverseLerp(dead, 1f, Mathf.Min(tilt, 1f));
            return value[hand];
        }

        // ask SteamVR what the controller's inputs are, and read the right one
        static string SteamVR(int hand, GameObject controller, ref Vector2 v, ref bool pressed, ref float dead)
        {
            uint index = VRTK_DeviceFinder.GetControllerIndex(controller);
            CVRSystem system = OpenVR.System;
            if (system == null || index >= OpenVR.k_unMaxTrackedDeviceCount) return "the game's own reading";
            var device = SteamVR_Controller.Input((int)index);
            if (askedOf[hand] != index)
            {
                int sticks = 0; bool padFound = false, answered = false;
                for (int axis = 0; axis < 5; axis++)
                {
                    var error = ETrackedPropertyError.TrackedProp_Success;
                    var type = (EVRControllerAxisType)system.GetInt32TrackedDeviceProperty(index, ETrackedDeviceProperty.Prop_Axis0Type_Int32 + axis, ref error);
                    if (error != ETrackedPropertyError.TrackedProp_Success) continue;
                    answered = true;
                    if (type == EVRControllerAxisType.k_eControllerAxis_Joystick) sticks |= 1 << axis;
                    else if (type == EVRControllerAxisType.k_eControllerAxis_TrackPad && axis == 0) padFound = true;
                }
                stickInputs[hand] = sticks; hasPad[hand] = padFound;
                // (a controller that answered nothing may only just have been switched on: it is asked again next frame)
                if (answered) askedOf[hand] = index;
            }
            Vector2 best = Vector2.zero; int stick = -1; bool pad = hasPad[hand];
            for (int axis = 0; axis < 5; axis++)
            {
                if ((stickInputs[hand] & (1 << axis)) == 0) continue;
                Vector2 a = device.GetAxis(EVRButtonId.k_EButton_Axis0 + axis);
                if (stick < 0 || a.sqrMagnitude > best.sqrMagnitude) { best = a; stick = axis; }
            }
            if (stick >= 0)
            {
                v = best;
                // (input 0's press is what the game grabs with under SteamVR; a stick on another input has a click of its own)
                pressed = stick > 0 && device.GetPress(EVRButtonId.k_EButton_Axis0 + stick);
                // (the wording is only put together when the input it names changes, not every frame)
                if (wordingFor[hand] != stick) { wordingFor[hand] = stick; wording[hand] = "a thumbstick (SteamVR, input " + stick + (stick > 0 ? ", click free)" : ", click is the game's grab)"); }
                return wording[hand];
            }
            if (pad)
            {
                v = device.GetTouch(EVRButtonId.k_EButton_SteamVR_Touchpad) ? device.GetAxis(EVRButtonId.k_EButton_Axis0) : Vector2.zero;
                dead = PadDeadZone;
                return "the trackpad, by touch (SteamVR)";
            }
            return "the game's own reading";
        }
    }
}
