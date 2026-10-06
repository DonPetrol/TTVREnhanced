// Tiny Town Enhanced - frames-per-second counter (Settings > Video > "FPS Counter").
// Shown in two places: the top right corner of the desktop window, and the top right of the wrist menu's red header.
// The number is the average over the last half second.
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class FpsCounter : IModule
    {
        public string Name { get { return "FpsCounter"; } }

        public static float Fps;                // latest half-second average
        static ConfigEntry<bool> show;
        static Text wristText;
        static Desktop desktop;
        static string words = "";               // "72 fps": made when the number changes, not every time it is drawn

        public void Start(Plugin plugin)
        {
            show = plugin.Config.Bind("Video", "FpsCounter", true, "Show frames per second on the desktop window and the wrist menu.");
            SettingsTabs.AddCheckbox("Video", "FPS Counter", "Shown here in the header and on the desktop window.", show, delegate (bool on) { Apply(); });
            plugin.gameObject.AddComponent<Counter>();
            desktop = plugin.gameObject.AddComponent<Desktop>();
            Apply();
            // the wrist menu's label is made the first time the menu is set up or opened
            var me = new HarmonyMethod(typeof(FpsCounter), "MenuReady");
            foreach (var m in new[] { "ResetForPlay", "ResetForMainMenu", "OnOpenBegin" })
                plugin.Harmony.Patch(AccessTools.Method(typeof(WristMenu), m), null, me);
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // (the desktop drawing is switched off as a whole with the option: merely having it makes the engine run its
        // window-drawing passes every frame)
        static void Apply()
        {
            if (wristText != null) wristText.gameObject.SetActive(show.Value);
            if (desktop != null) desktop.enabled = show.Value;
        }

        // a copy of the header's title text, moved to the right-hand end of the header
        static void MenuReady(WristMenu __instance)
        {
            if (wristText != null) return;
            try
            {
                Text title = __instance.title;
                var go = (GameObject)UnityEngine.Object.Instantiate(title.gameObject, title.transform.parent, false);
                go.name = "TinyTownEnhanced FPS";
                var rt = (RectTransform)go.transform;
                float height = Mathf.Max(rt.rect.height, 60f);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0.5f);
                rt.sizeDelta = new Vector2(320f, height);
                rt.anchoredPosition = new Vector2(-175f, 0f);          // clear of the World Select tab's "+" button in the corner
                wristText = go.GetComponent<Text>();
                wristText.alignment = TextAnchor.MiddleRight;
                wristText.fontSize = Mathf.Max(10, Mathf.RoundToInt(title.fontSize * 0.55f));
                wristText.resizeTextForBestFit = false;
                wristText.horizontalOverflow = HorizontalWrapMode.Overflow;
                wristText.raycastTarget = false;
                wristText.text = "";
                Apply();
            }
            catch (Exception e) { Plugin.Log.LogWarning("FpsCounter: " + e.Message); }
        }

        public class Counter : MonoBehaviour
        {
            int frames; float time;

            void Update()
            {
                frames++; time += Time.unscaledDeltaTime;
                if (time < 0.5f) return;
                Fps = frames / time;
                frames = 0; time = 0f;
                if (!show.Value) return;
                words = Fps.ToString("0") + " fps";
                if (wristText != null && wristText.isActiveAndEnabled) wristText.text = words;
            }
        }

        // desktop window only (this kind of drawing never appears in the headset)
        public class Desktop : MonoBehaviour
        {
            GUIStyle style;

            void Awake() { useGUILayout = false; }     // (nothing here is laid out automatically: the engine can skip that pass)

            void OnGUI()
            {
                if (Event.current.type != EventType.Repaint) return;
                if (style == null) { style = new GUIStyle(GUI.skin.label); style.fontSize = 22; style.fontStyle = FontStyle.Bold; style.alignment = TextAnchor.UpperRight; }
                string text = words;
                var r = new Rect(Screen.width - 212f, 10f, 200f, 32f);
                style.normal.textColor = Color.black;
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);
                style.normal.textColor = Color.white;
                GUI.Label(r, text, style);
            }
        }
    }
}
