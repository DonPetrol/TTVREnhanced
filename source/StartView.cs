// Tiny Town Enhanced - a world opens where you left it.
// The game always opens a world the same way: at scale 0.7, set down in front of you. With this, every save also
// notes where you were standing in the world, which way you were facing and the scale (one line, "view", among the
// world's settings - see WorldFile.cs), and opening the world puts you back there, wherever in the room you happen
// to be standing. A world saved without the mod, or a new one, opens the game's way.
// Settings > Gameplay: Save Last Position (on by default).
using System;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VRTK;

namespace TinyTownEnhanced
{
    public class StartView : IModule
    {
        public string Name { get { return "StartView"; } }
        const string Key = "view";
        static ConfigEntry<bool> on;

        public void Start(Plugin plugin)
        {
            on = plugin.Config.Bind("Gameplay", "ReopenWhereYouLeft", true, "A world opens where you were standing, facing the same way and at the same scale, as when it was last saved.");
            SettingsTabs.AddCheckbox("Gameplay", "Save Last Position", "A world opens at the place and scale it was saved at.", on, null);
            WorldFile.BeforeSave += Note;
            // the game has set the world down in front of you (it does this for every world it opens): now move it
            plugin.Harmony.Patch(AccessTools.Method(typeof(MenuState), "CenterWorldInFrontOfHeadset"), null, new HarmonyMethod(typeof(StartView), "Restore"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static Transform Head()
        {
            Transform head = VRTK_DeviceFinder.HeadsetTransform();
            return head != null ? head : (Eye.Camera != null ? Eye.Camera.transform : null);
        }

        // which way (degrees) the head faces, measured in the world's own directions
        static float Facing(Transform world, Transform head)
        {
            Vector3 forward = head.forward; forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = head.up;      // (looking straight down)
            Vector3 local = world.InverseTransformDirection(forward);
            return Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        }

        // a save is being made: where the head is in the world, which way it faces there, and the world's scale
        static void Note()
        {
            try
            {
                var settings = Singleton<WorldSettings>.Instance; Transform head = Head();
                if (settings == null || settings.DisplayWorld == null || head == null) return;
                Vector3 at = settings.DisplayWorld.InverseTransformPoint(head.position);
                var c = CultureInfo.InvariantCulture;
                WorldFile.SetText(Key, at.x.ToString("R", c) + "," + at.y.ToString("R", c) + "," + at.z.ToString("R", c) + ","
                                       + Facing(settings.DisplayWorld, head).ToString("R", c) + "," + settings.Scale.ToString("R", c));
            }
            catch (Exception e) { Plugin.Log.LogWarning("StartView: " + e.Message); }
        }

        static void Restore()
        {
            try
            {
                string text = WorldFile.GetText(Key);
                if (!on.Value || string.IsNullOrEmpty(text)) return;
                string[] parts = text.Split(',');
                var numbers = new float[5];
                if (parts.Length != 5) return;
                for (int i = 0; i < 5; i++) if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])) return;
                var settings = Singleton<WorldSettings>.Instance; Transform head = Head();
                if (settings == null || settings.DisplayWorld == null || head == null) return;
                Transform world = settings.DisplayWorld;

                settings.Velocity = Vector3.zero;
                settings.Scale = numbers[4];
                // Turn the world about the head until the head faces the way it did, then slide the place it stood to
                // the head. The turning is done in two steps. First the world is turned by the angle between the way
                // the head faced then and the way it faces now. Whether that turn brings the two together or moves
                // them twice as far apart depends on which way round the angle was measured, so the facing is
                // measured again: if it is still off, the first turn went the wrong way, and turning back by twice as
                // much undoes it and makes the same turn in the right direction.
                float turn = Mathf.DeltaAngle(numbers[3], Facing(world, head));
                world.RotateAround(head.position, Vector3.up, turn);
                float left = Mathf.DeltaAngle(numbers[3], Facing(world, head));
                if (Mathf.Abs(left) > 1f) world.RotateAround(head.position, Vector3.up, -2f * turn);
                world.position += head.position - world.TransformPoint(new Vector3(numbers[0], numbers[1], numbers[2]));
            }
            catch (Exception e) { Plugin.Log.LogWarning("StartView: " + e.Message); }
        }
    }
}
