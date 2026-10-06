// Tiny Town Enhanced - Video settings: Anti-aliasing, Shadow Quality, Shadow Distance and Resolution Scale (Settings > Video).
// The defaults are what the game itself uses (8x, Very High, 1.00x), so nothing changes until you change it.
// Lower settings are how to get frames back in heavy worlds; a higher resolution scale is sharper but costs frames.
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace TinyTownEnhanced
{
    public class VideoOptions : IModule
    {
        public string Name { get { return "VideoOptions"; } }

        static readonly string[] AaNames = { "Off", "2x", "4x", "8x" };
        static readonly int[] AaSamples = { 0, 2, 4, 8 };
        static readonly string[] ShadowNames = { "Off", "Low", "Medium", "High", "Very High" };

        static ConfigEntry<int> aa, shadows; static ConfigEntry<float> scale, shadowDistance;
        static float wantedScale = 1f, appliedScale = -1f, scaleChangedAt;

        public void Start(Plugin plugin)
        {
            aa = plugin.Config.Bind("Video", "AntiAliasing", 3, "0 = Off, 1 = 2x, 2 = 4x, 3 = 8x (the game's own setting)");
            shadows = plugin.Config.Bind("Video", "ShadowQuality", 4, "0 = Off, 1 = Low, 2 = Medium, 3 = High, 4 = Very High (the game's own setting)");
            scale = plugin.Config.Bind("Video", "ResolutionScale", 1f, "0.5 to 2.0; 1.0 is the headset's normal resolution");
            SettingsTabs.AddChoice("Video", "Anti-aliasing", AaNames, aa, SetAntiAliasing);
            SettingsTabs.AddChoice("Video", "Shadow Quality", ShadowNames, shadows, SetShadows);
            // Shadow Distance: how far from you shadows are drawn, as a share of the game's own distance (which the game
            // changes with how far the world is zoomed: this multiplies whatever it sets)
            shadowDistance = plugin.Config.Bind("Video", "ShadowDistance", 100f, "Percent of the game's own shadow distance (25 to 400).");
            SettingsTabs.AddSlider("Video", "Shadow Distance", shadowDistance, 25f, 400f, 25f, v => v.ToString("0") + "%", delegate { Refresh(); }, () => shadows.Value != 0);
            plugin.Harmony.Patch(AccessTools.Method(typeof(PlayState), "UpdateShadowDistance"), null, new HarmonyMethod(typeof(VideoOptions), "ShadowReach"));
            SettingsTabs.AddSlider("Video", "Resolution Scale", scale, 0.5f, 2f, 0.05f, v => v.ToString("0.00") + "x", SetScale);
        }

        // the game or the headset software may put these back when a scene loads: apply them again
        public void SceneLoaded(string name)
        {
            SetAntiAliasing(aa.Value); SetShadows(shadows.Value); appliedScale = -1f;
        }

        // The resolution is only changed once the slider has been still for a moment: every change makes the headset's
        // picture be rebuilt, which would stutter if done all the way along a drag.
        public void Tick()
        {
            if (Mathf.Approximately(wantedScale, appliedScale) || Time.unscaledTime - scaleChangedAt < 0.4f) return;
            try
            {
                if (!Mathf.Approximately(XRSettings.eyeTextureResolutionScale, wantedScale)) XRSettings.eyeTextureResolutionScale = wantedScale;
                appliedScale = wantedScale;
            }
            catch (Exception e) { appliedScale = wantedScale; Plugin.Log.LogWarning("VideoOptions: " + e.Message); }
        }

        static void SetScale(float v) { wantedScale = Mathf.Clamp(v, 0.5f, 2f); scaleChangedAt = Time.unscaledTime; }

        static void SetAntiAliasing(int index)
        {
            int samples = AaSamples[Mathf.Clamp(index, 0, AaSamples.Length - 1)];
            if (QualitySettings.antiAliasing == samples) return;
            QualitySettings.antiAliasing = samples;
        }

        static float gameDistance = -1f;                            // what the game last set, before the setting was applied
        static void ShadowReach()
        {
            gameDistance = QualitySettings.shadowDistance;
            // (Gestures.Beyond: with the world zoomed past the game's limit, shadows reach that much further too)
            QualitySettings.shadowDistance = gameDistance * Mathf.Clamp(shadowDistance.Value, 25f, 400f) / 100f * Gestures.Beyond;
        }
        static void Refresh() { if (gameDistance > 0f) QualitySettings.shadowDistance = gameDistance * Mathf.Clamp(shadowDistance.Value, 25f, 400f) / 100f * Gestures.Beyond; }

        static void SetShadows(int index)
        {
            switch (Mathf.Clamp(index, 0, 4))
            {
                case 0: QualitySettings.shadows = ShadowQuality.Disable; break;
                case 1: Shadows(ShadowQuality.HardOnly, ShadowResolution.Low, 2); break;
                case 2: Shadows(ShadowQuality.All, ShadowResolution.Medium, 2); break;
                case 3: Shadows(ShadowQuality.All, ShadowResolution.High, 4); break;
                default: Shadows(ShadowQuality.All, ShadowResolution.VeryHigh, 4); break;
            }
        }

        static void Shadows(ShadowQuality kind, ShadowResolution resolution, int cascades)
        {
            QualitySettings.shadows = kind; QualitySettings.shadowResolution = resolution; QualitySettings.shadowCascades = cascades;
        }
    }
}
