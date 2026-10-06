// Tiny Town Enhanced - loading message inside the headset.
// While the game loads Workshop items it shows "Processing Workshop Items..." only in the desktop window; in VR you
// stand in a black void. This module puts a copy of that message in the 3D world, about two metres in front of you,
// and keeps it in step with the original (including FastSync's progress counts). It removes itself when loading ends.
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class LoadingText : IModule
    {
        public string Name { get { return "LoadingText"; } }
        const float Distance = 2f, Width = 1.6f;        // metres

        bool done;

        public void Start(Plugin plugin) { }
        public void SceneLoaded(string scene) { TryCreate(); }
        public void Tick() { TryCreate(); }

        void TryCreate()
        {
            if (done || Plugin.LoadingDone) return;                       // nothing to do once the menu is up
            var init = Object.FindObjectOfType<InitState>();           // only found while the loading step is active
            if (init == null) return;
            var source = AccessTools.Field(typeof(InitState), "screenMessage").GetValue(init) as Text;
            Camera cam = HeadCamera();
            if (source == null || cam == null) return;
            done = true;

            // a small world-space canvas facing the player, at eye height
            var go = new GameObject("TinyTownEnhanced Loading Text");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(1600f, 400f);
            rt.localScale = Vector3.one * (Width / 1600f);
            Vector3 forward = cam.transform.forward; forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            rt.position = cam.transform.position + forward * Distance;
            rt.rotation = Quaternion.LookRotation(forward);

            var tgo = new GameObject("Text");
            tgo.transform.SetParent(rt, false);
            var text = tgo.AddComponent<Text>();
            var trt = text.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
            text.font = source.font;
            text.fontSize = 90;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.text = source.text;

            var mirror = go.AddComponent<Mirror>();
            mirror.Source = source; mirror.Target = text; mirror.Owner = init.gameObject;
        }

        // the camera that follows the headset
        static Camera HeadCamera()
        {
            Camera main = Eye.Camera;
            if (main != null) return main;
            foreach (var c in Camera.allCameras)
                if (c.stereoTargetEye != StereoTargetEyeMask.None) return c;
            return null;
        }

        // copies the message every frame; goes away when the loading step is switched off
        public class Mirror : MonoBehaviour
        {
            public Text Source, Target; public GameObject Owner;
            void LateUpdate()
            {
                if (Owner == null || !Owner.activeInHierarchy || Source == null) { Destroy(gameObject); return; }
                if (Target.text != Source.text) Target.text = Source.text;
            }
        }
    }
}
