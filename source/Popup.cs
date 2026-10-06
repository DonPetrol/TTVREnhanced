// Tiny Town Enhanced - a short message that floats in front of your face in the headset.
// Any module can call Popup.Show("text", seconds). It is a small sign in the 3D world (like the loading message),
// but fastened to the headset so it stays in view wherever you look, and it removes itself when the time is up.
// Both are drawn over everything else, so nothing in the world can cover them.
// Popup.Note("text", seconds) is a smaller one, lower down, for a value that keeps changing (the zoom, for example).
using System;
using UnityEngine;
using UnityEngine.UI;
using VRTK;

namespace TinyTownEnhanced
{
    public static class Popup
    {
        const float Distance = 1.2f, Width = 0.9f, Below = 0.12f;      // metres: how far away, how wide, how far below eye level

        static GameObject current;

        public static void Show(string message, float seconds)
        {
            try
            {
                Transform head = VRTK_DeviceFinder.HeadsetTransform();
                if (head == null && Eye.Camera != null) head = Eye.Camera.transform;
                if (head == null) return;
                if (current != null) UnityEngine.Object.Destroy(current);

                var go = new GameObject("TinyTownEnhanced Popup");
                Text text = Sign(go, head, 1200f, 300f, Width, Below, 70);
                text.rectTransform.offsetMin = new Vector2(40f, 20f); text.rectTransform.offsetMax = new Vector2(-40f, -20f);
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.text = message;

                current = go;
                UnityEngine.Object.Destroy(go, seconds);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Popup: " + e.Message); }
        }

        // ---- a small readout, a little below the middle of the view: for a value that is changing. Calling it again
        // changes the words and restarts the clock; it hides itself when the time runs out.
        const float NoteBelow = 0.33f, NoteWidth = 0.42f;             // metres at Distance: how far below eye level, how wide
        static GameObject note; static Text noteText; static float noteUntil; static int noteRank;

        /// rank: while a readout of a higher rank is still showing, one of a lower rank is not shown at all
        public static void Note(string message, float seconds, int rank = 0)
        {
            try
            {
                if (note != null && note.activeSelf && Time.unscaledTime <= noteUntil && rank < noteRank) return;
                noteRank = rank;
                if (note == null)
                {
                    Transform head = VRTK_DeviceFinder.HeadsetTransform();
                    if (head == null && Eye.Camera != null) head = Eye.Camera.transform;
                    if (head == null) return;
                    note = new GameObject("TinyTownEnhanced Note");
                    noteText = Sign(note, head, 700f, 150f, NoteWidth, NoteBelow, 80);
                    note.AddComponent<Timer>();
                }
                if (noteText.text != message) noteText.text = message;
                noteUntil = Time.unscaledTime + seconds;
                if (!note.activeSelf) note.SetActive(true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Popup: " + e.Message); }
        }

        // what both kinds are made of: a canvas fastened to the headset (so many units across, drawn so many metres
        // wide, so far below eye level), a dark panel so the words can be read against anything, and the words
        // (white, in the middle, free to run over the edge unless told otherwise). Returns the words.
        static Text Sign(GameObject go, Transform head, float unitsWide, float unitsHigh, float metresWide, float below, int fontSize)
        {
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 30000;                    // drawn after every other canvas: the wrist menu cannot cover it
            var rt = (RectTransform)go.transform;
            rt.SetParent(head, false);
            rt.sizeDelta = new Vector2(unitsWide, unitsHigh);
            rt.localScale = Vector3.one * (metresWide / unitsWide);
            rt.localPosition = new Vector3(0f, -below, Distance);
            rt.localRotation = Quaternion.identity;

            var panel = new GameObject("Panel").AddComponent<Image>();
            panel.transform.SetParent(rt, false);
            Fill(panel.rectTransform);
            panel.color = new Color(0f, 0f, 0f, 0.6f);
            panel.raycastTarget = false;
            OnTop(panel);

            var text = new GameObject("Text").AddComponent<Text>();
            text.transform.SetParent(rt, false);
            Fill(text.rectTransform);
            text.font = MenuPage.GameFont();
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow; text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            OnTop(text);
            return text;
        }

        class Timer : MonoBehaviour
        {
            void Update() { if (Time.unscaledTime > noteUntil) gameObject.SetActive(false); }
        }

        // drawn over everything, so nothing in the world can hide the words: a copy of the ordinary menu material
        // that skips the "is something in front of me?" test, and is drawn last
        static Material onTop;
        static void OnTop(Graphic g)
        {
            if (onTop == null)
            {
                onTop = new Material(Canvas.GetDefaultCanvasMaterial()) { name = "TinyTownEnhanced on top", renderQueue = 4000 };
                onTop.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            g.material = onTop;
        }

        static void Fill(RectTransform r) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }
    }
}
