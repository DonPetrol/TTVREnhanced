// Tiny Town Enhanced - "busy" words in front of you while a big job is done (the Group Tool's large groups).
// Picking up, putting down, copying, deleting or undoing a large group means the game re-sorting every one of those
// objects for drawing, which it can only do all at once, so the picture stops for a moment. Rather than freezing with
// no warning, a short line ("Placing 140 objects...") is shown first: the job waits two frames so the words are on
// screen - the headset keeps showing the last picture while the game is busy - and they stay for a few frames after,
// while the game finishes sorting. Small jobs are simply done straight away.
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public static class Busy
    {
        /// fewer objects than this: no words, done at once (picking a group up is quicker than the rest, and passes its
        /// own, larger number: PickUpThreshold)
        public const int Threshold = 200, PickUpThreshold = 1000;
        /// a job is waiting or being done (the Group Tool holds undo back meanwhile)
        public static bool Working { get { return jobs > 0; } }
        static int jobs;
        static GameObject board; static Text words;

        /// do `work` (about `count` objects): at once if it is small, otherwise with the words shown first
        public static void Run(string doing, int count, Action work, int threshold = Threshold)
        {
            if (count < threshold || Plugin.Instance == null) { Safe(work); return; }
            Plugin.Instance.StartCoroutine(Later(doing, work));
        }

        static IEnumerator Later(string doing, Action work)
        {
            jobs++;
            // (whatever happens the count comes back down: undo is held back while it is above zero)
            try
            {
                Show(doing);
                yield return null; yield return null;           // (two frames: the words are drawn before the game stops)
                Safe(work);
                for (int i = 0; i < 3; i++) yield return null;  // (the game sorts the objects for drawing in the frames after)
            }
            finally
            {
                jobs--;
                if (jobs == 0 && board != null) board.SetActive(false);
            }
        }

        static void Safe(Action work)
        {
            try { work(); } catch (Exception e) { Plugin.Log.LogWarning("Busy: " + e); }
        }

        // the words: white on a dark strip, a metre and a half in front of your eyes, facing you
        static void Show(string doing)
        {
            Camera cam = Eye.Camera;
            if (cam == null) return;
            if (board == null)
            {
                board = new GameObject("TinyTownEnhanced Busy");
                UnityEngine.Object.DontDestroyOnLoad(board);
                var canvas = board.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.sortingOrder = 1000;
                var rt = (RectTransform)board.transform;
                rt.sizeDelta = new Vector2(1400f, 160f);
                rt.localScale = Vector3.one * (0.9f / 1400f);
                var back = new GameObject("Back", typeof(RectTransform), typeof(Image));
                back.transform.SetParent(rt, false);
                var brt = (RectTransform)back.transform; brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = brt.offsetMax = Vector2.zero;
                back.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.8f);
                words = new GameObject("Words", typeof(RectTransform)).AddComponent<Text>();
                words.transform.SetParent(rt, false);
                var wrt = words.rectTransform; wrt.anchorMin = Vector2.zero; wrt.anchorMax = Vector2.one; wrt.offsetMin = new Vector2(30f, 10f); wrt.offsetMax = new Vector2(-30f, -10f);
                words.font = MenuPage.GameFont(); words.fontSize = 80; words.color = Color.white;
                words.alignment = TextAnchor.MiddleCenter; words.raycastTarget = false;
                words.resizeTextForBestFit = true; words.resizeTextMinSize = 20; words.resizeTextMaxSize = 80;
                // (always drawn over what is around it, so a building in the way cannot hide it)
                foreach (var g in board.GetComponentsInChildren<Graphic>())
                {
                    var m = new Material(g.materialForRendering);
                    m.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
                    m.renderQueue = 4000;
                    g.material = m;
                }
            }
            words.text = doing;
            Vector3 forward = cam.transform.forward; forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            board.transform.position = cam.transform.position + forward * 1.5f;
            board.transform.rotation = Quaternion.LookRotation(forward);
            board.SetActive(true);
        }
    }
}
