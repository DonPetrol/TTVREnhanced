// Tiny Town Enhanced - error messages in the bottom left corner of the desktop window.
// Drawn with Unity's immediate-mode GUI, which only appears on the monitor, not inside the headset.
// Any module can call Notices.Error("...").
// The drawing part is switched off while there is nothing to show: Unity runs its GUI pass for it every frame
// otherwise, messages or not. Error only raises a flag (it may be called from any thread); Tick switches the part on.
using System.Collections.Generic;
using UnityEngine;

namespace TinyTownEnhanced
{
    public class Notices : IModule
    {
        public string Name { get { return "Notices"; } }
        const float ShowSeconds = 60f; const int MaxLines = 6;

        static readonly List<string> lines = new List<string>();
        static float lastAdded;
        static volatile bool wake;          // a message has been added: the view is to be switched on
        static View view;

        public static void Error(string message)
        {
            Plugin.Log.LogWarning(message);
            lock (lines) { lines.Add(message); lastAdded = -1f; wake = true; }       // (the clock starts at the next drawn frame)
        }

        public void Start(Plugin plugin)
        {
            view = plugin.gameObject.AddComponent<View>();
            view.useGUILayout = false;      // (it only draws: no layout pass needed)
            view.enabled = false;
        }
        public void SceneLoaded(string scene) { }
        public void Tick()
        {
            if (!wake || view == null) return;
            wake = false; view.enabled = true;
        }

        public class View : MonoBehaviour
        {
            GUIStyle style;
            void OnGUI()
            {
                if (Event.current.type != EventType.Repaint) return;
                lock (lines)
                {
                    if (lines.Count == 0) { enabled = false; return; }
                    if (lastAdded < 0f) lastAdded = Time.unscaledTime;
                    if (Time.unscaledTime - lastAdded > ShowSeconds) { lines.Clear(); enabled = false; return; }
                    if (style == null) { style = new GUIStyle(GUI.skin.label); style.fontSize = 15; style.wordWrap = true; style.alignment = TextAnchor.LowerLeft; }
                    string text = "";
                    int shown = Mathf.Min(lines.Count, MaxLines);
                    for (int i = 0; i < shown; i++) text += (i > 0 ? "\n" : "") + lines[i];
                    if (lines.Count > shown) text += "\n...and " + (lines.Count - shown) + " more (see BepInEx\\LogOutput.log)";
                    var r = new Rect(12f, 12f, Mathf.Min(Screen.width - 24f, 900f), Screen.height - 24f);
                    style.normal.textColor = Color.black;
                    GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);
                    style.normal.textColor = new Color(1f, 0.35f, 0.3f);
                    GUI.Label(r, text, style);
                }
            }
        }
    }
}
