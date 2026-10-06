// Tiny Town Enhanced - moving, turning and zooming with the thumbsticks. Settings > Controls. Stick Movement and Stick
// Turning are off by default; Stick Up/Down and Stick Scale are on, so they come with Stick Movement when it is ticked.
//   Stick Movement    the left stick moves you (the game has no stick controls of its own)
//   Stick Direction   Head: forward on the stick is where you are looking. Hand: where that hand's controller points.
//                     In every direction: look (or point) up and push forward, and you go up.
//   Stick Speed       metres per second at full tilt
//   Sprint Speed      click the moving stick in while moving and you go this many times faster, until it is let go
//   Stick Movement Drift   on: you drift to a stop when the stick is let go, as after a grab-and-pull. Off: you stop at once.
//   Stick Up/Down     (with Stick Movement) up and down on the other stick moves you straight up and down; it blends
//                     with turning, so you can rise and turn at once
//   Stick Scale       (with Stick Movement) while BOTH triggers are held - the grip the two-handed zoom and rotate start
//                     from - up and down on the other stick zooms the world instead. The stick has to be held for a
//                     moment first, so a knock does nothing; once it has taken over, the hands' own zoom and rotate
//                     are ignored until the triggers are let go.
//   Stick Turning     the right stick turns you, about the same upright axis as the two-handed rotate
//   Turn Style        Snap: one flick, one turn of Snap Angle.  Smooth: turns while held, at Turn Speed.
//   Swap Sticks       moving goes to the right stick, and turning / up and down / zoom to the left
// The game never moves the player: it slides, turns and scales the world instead (that is what its grab gestures do),
// and so does this. It runs only inside a world. A stick stands aside while the build menu is open if it is the one
// that pans that menu (the left for a right-handed player, the right for a left-handed one).
// Which physical control "the stick" is on each kind of controller is worked out in Sticks.cs.
using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VRTK;

namespace TinyTownEnhanced
{
    public class StickMove : IModule
    {
        public string Name { get { return "StickMove"; } }
        static ConfigEntry<bool> move, turn, upDown, drift, swap, scale; static ConfigEntry<int> direction, style; static ConfigEntry<float> speed, sprint, snapAngle, turnSpeed;
        static Inventory inventory; static readonly FieldInfo fLeftHanded = AccessTools.Field(typeof(Inventory), "isLeftHanded");
        static bool flicked, failed, sprinting; static float lookAgain;

        // Stick Scale: how long the stick must be held before it takes over, and how fast it zooms (doublings per second at full tilt)
        const float TakeOver = 0.4f, ZoomRate = 1f;
        static float held; static bool scaling;

        public void Start(Plugin plugin)
        {
            move = plugin.Config.Bind("Controls", "StickMove", false, "Move around with the left thumbstick.");
            direction = plugin.Config.Bind("Controls", "StickMoveDirection", 0, "0 = forward is where you look (head), 1 = forward is where the moving hand's controller points (hand)");
            speed = plugin.Config.Bind("Controls", "StickMoveSpeed", 1.5f, "Metres per second at full tilt (0.5 to 5).");
            sprint = plugin.Config.Bind("Controls", "StickSprint", 2f, "How many times faster you go after clicking the moving stick in (1 = no sprint, up to 5).");
            drift = plugin.Config.Bind("Controls", "StickMoveDrift", false, "Stick movement drifts to a stop when the stick is let go, like a grab does.");
            upDown = plugin.Config.Bind("Controls", "StickRise", true, "Up and down on the other thumbstick moves you up and down.");
            scale = plugin.Config.Bind("Controls", "StickZoom", true, "While both triggers are held, up and down on the other thumbstick zooms the world.");
            turn = plugin.Config.Bind("Controls", "StickTurn", false, "Turn with the right thumbstick.");
            style = plugin.Config.Bind("Controls", "StickTurnStyle", 0, "0 = snap (one flick, one turn), 1 = smooth (turns while held)");
            snapAngle = plugin.Config.Bind("Controls", "StickTurnSnapAngle", 45f, "Degrees per snap turn (15 to 90).");
            turnSpeed = plugin.Config.Bind("Controls", "StickTurnSpeed", 90f, "Degrees per second for smooth turning (30 to 240).");
            swap = plugin.Config.Bind("Controls", "StickSwap", false, "Move with the right thumbstick and turn with the left.");

            Func<bool> moving = () => move.Value;
            SettingsTabs.AddCheckbox("Controls", "Stick Movement", "A thumbstick moves you around the world.", move, null);
            SettingsTabs.AddChoice("Controls", "Stick Direction", new[] { "Head", "Hand" }, direction, null, moving);
            SettingsTabs.AddSlider("Controls", "Stick Speed", speed, 0.5f, 5f, 0.25f, v => v.ToString("0.00") + " m/s", null, moving);
            SettingsTabs.AddSlider("Controls", "Sprint Speed", sprint, 1f, 5f, 0.25f, v => v <= 1f ? "Off" : v.ToString("0.00") + "x", null, moving);
            SettingsTabs.AddCheckbox("Controls", "Stick Movement Drift", "You drift to a stop when the stick is let go.", drift, null, moving);
            SettingsTabs.AddCheckbox("Controls", "Stick Up/Down", "Up and down on the other thumbstick moves you up and down.", upDown, null, moving);
            SettingsTabs.AddCheckbox("Controls", "Stick Scale", "With both triggers held, up and down on the other thumbstick zooms.", scale, null, moving);
            SettingsTabs.AddCheckbox("Controls", "Stick Turning", "A thumbstick turns you.", turn, null);
            SettingsTabs.AddChoice("Controls", "Turn Style", new[] { "Snap", "Smooth" }, style, null, () => turn.Value);
            SettingsTabs.AddSlider("Controls", "Snap Angle", snapAngle, 15f, 90f, 15f, v => v.ToString("0") + "°", null, () => turn.Value && style.Value == 0);
            SettingsTabs.AddSlider("Controls", "Turn Speed", turnSpeed, 30f, 240f, 15f, v => v.ToString("0") + "°/s", null, () => turn.Value && style.Value == 1);
            SettingsTabs.AddCheckbox("Controls", "Swap Sticks", "Moving is on the right stick; turning on the left.", swap, null, () => move.Value || turn.Value);

            var me = typeof(StickMove);
            // (PlayState.Early runs once a frame, and only while a world is open)
            plugin.Harmony.Patch(AccessTools.Method(typeof(PlayState), "Early"), null, new HarmonyMethod(me, "Frame"));
            // while the stick is zooming, the hands' own zoom and rotate do nothing
            var block = new HarmonyMethod(me, "HandsOff") { priority = Priority.First };
            plugin.Harmony.Patch(AccessTools.Method(typeof(ControllerPinch), "UpdateWorldTransform"), block);
            plugin.Harmony.Patch(AccessTools.Method(typeof(ControllerRotate), "UpdateWorldTransform"), block);
        }
        public void SceneLoaded(string scene) { inventory = null; }
        public void Tick() { }

        static bool HandsOff(ref bool __result)
        {
            if (!scaling) return true;
            __result = false;
            return false;
        }

        // how far to one side the second stick must be before it turns you while it can also raise you, and how far up or
        // down before it raises you while it can also turn you (the turning band is the wider one)
        const float TurnBand = 0.35f, RiseBand = 0.2f;
        static float Band(float v, float band) { float a = Mathf.Abs(v); return a <= band ? 0f : Mathf.Sign(v) * (a - band) / (1f - band); }

        // is this hand's stick busy panning the build menu?
        static bool MenuHas(int hand)
        {
            // (looked for at most once a second, and not with a search of the whole scene every frame)
            if (inventory == null && Time.unscaledTime >= lookAgain)
            {
                lookAgain = Time.unscaledTime + 1f;
                foreach (var i in Resources.FindObjectsOfTypeAll<Inventory>()) if (i.gameObject.scene.IsValid()) inventory = i;
            }
            if (inventory == null || inventory.IsFullyClosed) return false;
            bool leftHanded = (bool)fLeftHanded.GetValue(inventory);
            return hand == (leftHanded ? 1 : 0);
        }

        static bool Trigger(int hand)
        {
            var events = Sticks.Events(hand);                       // (remembered there: not looked up on the controller every frame)
            return events != null && events.triggerPressed;
        }

        static void Frame()
        {
            if (failed || (!move.Value && !turn.Value)) { scaling = false; return; }
            try
            {
                var world = Singleton<WorldSettings>.Instance;
                Transform head = VRTK_DeviceFinder.HeadsetTransform();
                if (world == null || head == null) { scaling = false; return; }
                int first = swap.Value ? 1 : 0, second = 1 - first;                       // which hand moves, and which turns
                Vector2 firstStick = first == 0 ? Sticks.Left : Sticks.Right;

                // how fast you are moving, in metres per second: the first stick along the head's (or that hand's)
                // own forward and sideways, in every direction; the second stick straight up and down
                Vector3 going = Vector3.zero; float pace = Mathf.Clamp(speed.Value, 0.5f, 5f);
                if (move.Value)
                {
                    // sprint: click the stick in while moving, and you go faster until the stick is let go
                    if (firstStick == Vector2.zero) sprinting = false;
                    else if ((first == 0 ? Sticks.LeftClick : Sticks.RightClick) && sprint.Value > 1f) sprinting = true;
                    if (sprinting) pace *= Mathf.Clamp(sprint.Value, 1f, 5f);
                    if (firstStick != Vector2.zero && !MenuHas(first))
                    {
                        Transform from = head;
                        if (direction.Value == 1) { GameObject controller = Sticks.Controller(first); if (controller != null) from = controller.transform; }
                        going += (from.forward * firstStick.y + from.right * firstStick.x) * pace;
                    }
                }

                // the second stick: across turns you, up and down raises and lowers you, and both can happen at once.
                // Each way has a dead band of its own, so pushing nearly straight up does not also turn you (and the
                // other way round); past the band the push counts from zero again, so nothing jumps.
                bool rise = move.Value && upDown.Value, zoom = move.Value && scale.Value;
                Vector2 stick = (turn.Value || rise || zoom) && !MenuHas(second) ? (second == 0 ? Sticks.Left : Sticks.Right) : Vector2.zero;
                bool both = turn.Value && (rise || zoom);
                float across = both ? Band(stick.x, TurnBand) : stick.x, upward = both ? Band(stick.y, RiseBand) : stick.y;

                // zoom: both triggers held, and the stick held up or down for a moment
                bool gripped = zoom && Trigger(0) && Trigger(1);
                if (!gripped) { scaling = false; held = 0f; }
                else if (!scaling) { held = upward != 0f ? held + Time.deltaTime : 0f; if (held >= TakeOver) scaling = true; }
                if (scaling)
                {
                    if (upward != 0f) Gestures.ZoomTo(world.Scale * Mathf.Pow(2f, upward * ZoomRate * Time.deltaTime));
                }
                else if (rise && !gripped) going += Vector3.up * upward * pace;

                // you go one way: the world goes the other. With drift, the world is given that speed and slows down by
                // itself when the stick is let go (as after a grab); without, it is simply moved.
                if (going != Vector3.zero)
                {
                    if (drift.Value) Gestures.StickVelocity(-going);
                    else world.Offset -= going * Time.deltaTime;
                }

                if (turn.Value)
                {
                    float x = across, angle = 0f;
                    if (style.Value == 1) angle = x * Mathf.Clamp(turnSpeed.Value, 30f, 240f) * Time.deltaTime;
                    else if (Mathf.Abs(x) < 0.3f) flicked = false;                                  // back near the middle: ready for the next flick
                    else if (!flicked && Mathf.Abs(x) > 0.7f) { flicked = true; angle = Mathf.Sign(x) * Mathf.Clamp(snapAngle.Value, 15f, 90f); }
                    // you turn right: the world turns left about where your head is (the game's rotate turns it the same way, about your hands)
                    if (angle != 0f) world.RotateAbout(head.position, -angle);
                }
            }
            catch (Exception e) { failed = true; scaling = false; Plugin.Log.LogWarning("StickMove: switched off after an error: " + e); }
        }
    }
}
