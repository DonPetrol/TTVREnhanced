// Tiny Town Enhanced - the main menu's 3D "Tiny Town VR" logo sits a little higher, with a blue box (#00C6FF) under it
// saying "Enhanced" in white, and the mod's version in white beside it, in the game's own Open Sans Bold (hidden behind
// anything in front of it, like the logo itself).
// The logo is the menu's "title" object, which the game places over the town each time it centres the menu
// (MenuState.PositionTitle): it is lifted by about a third of its own height (Lift) just after. The words are a child of the logo,
// so they go wherever the logo goes (also when the menu is turned: MenuTurn.cs); they stay still, facing
// the way you faced when they first appeared (turning with the logo).
using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class MenuLogo : IModule
    {
        public string Name { get { return "MenuLogo"; } }
        const float Lift = 0.35f;                   // how far the logo goes up, in logo heights
        static readonly Color Blue = Colours.Accent;    // #00C6FF
        static bool told;

        public void Start(Plugin plugin)
        {
            plugin.Harmony.Patch(AccessTools.Method(typeof(MenuState), "PositionTitle"), postfix: new HarmonyMethod(typeof(MenuLogo), "Placed"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // the game has just put the logo in place: lift it, and hang the words under it (once)
        static void Placed(GameObject ___title)
        {
            try
            {
                if (___title == null) return;
                Bounds b; if (!Size(___title, out b)) return;
                if (!told) { told = true; Plugin.Log.LogDebug("MenuLogo: logo '" + ___title.name + "' is " + b.size.ToString("F2") + " across, at " + b.center.ToString("F1")); }
                ___title.transform.position += Vector3.up * b.size.y * Lift;
                if (___title.transform.Find("TTE Enhanced") == null) Words(___title, b);
            }
            catch (Exception e) { Plugin.Log.LogWarning("MenuLogo: " + e.Message); }
        }

        // how big the logo is (all its drawn parts together, in the world)
        static bool Size(GameObject go, out Bounds b)
        {
            b = new Bounds(); bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (r.GetComponentInParent<Canvas>() != null) continue;      // (not our own words)
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any && b.size.y > 0.0001f;
        }

        // A blue box with "Enhanced" in white across it, and the version in white just past its bottom right corner. It is a
        // small UI canvas standing in the world (UI text is measured as it is made, so the box can be fitted to the word),
        // drawn with Unity's UI shader told to respect depth: the logo, or anything else in front, hides it.
        const int WordSize = 120, VersionSize = 56;
        const float PadX = 34f, PadY = 4f;
        static void Words(GameObject title, Bounds b)
        {
            Font font = MenuPage.GameFont("OpenSans-Bold");
            bool arial = font.name != "OpenSans-Bold";             // (the game's font was not found: Arial, made bold)
            var material = new Material(Graphic.defaultGraphicMaterial) { name = "TTE Enhanced" };
            material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.LessEqual);

            var go = new GameObject("TTE Enhanced", typeof(RectTransform));
            go.transform.SetParent(title.transform, false);
            go.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var root = (RectTransform)go.transform;

            Text word = Label(root, "Word", "Enhanced", font, arial, WordSize, material);
            float w = word.preferredWidth + PadX * 2f, h = word.preferredHeight + PadY * 2f;
            root.sizeDelta = new Vector2(w, h);

            // the box (first, so the words are drawn over it)
            var box = new GameObject("Box", typeof(RectTransform)).AddComponent<Image>();
            box.transform.SetParent(root, false); box.transform.SetAsFirstSibling();
            Fill(box.rectTransform); box.color = Blue; box.material = material; box.raycastTarget = false;
            Fill(word.rectTransform);

            // the version, just past the box's bottom right corner
            Text version = Label(root, "Version", "v" + Plugin.Version, font, arial, VersionSize, material);
            var vr = version.rectTransform;
            vr.anchorMin = vr.anchorMax = new Vector2(1f, 0f); vr.pivot = new Vector2(0f, 0f);
            vr.sizeDelta = new Vector2(version.preferredWidth + 4f, version.preferredHeight);
            vr.anchoredPosition = new Vector2(10f, -6f);
            version.alignment = TextAnchor.LowerLeft;

            var hover = go.AddComponent<Hover>();
            // under the logo, standing just clear of where the logo's foot was before it was lifted (so the ground does not
            // cut it); the box a little over a quarter of the logo's height tall
            hover.Height = b.size.y * 0.28f; hover.Units = h;
            hover.Home = title.transform.InverseTransformPoint(new Vector3(b.center.x, b.min.y + b.size.y * 0.16f, b.center.z));
        }

        static Text Label(RectTransform parent, string name, string words, Font font, bool arial, int size, Material material)
        {
            var t = new GameObject(name, typeof(RectTransform)).AddComponent<Text>();
            t.transform.SetParent(parent, false);
            t.font = font; t.fontSize = size; t.fontStyle = arial ? FontStyle.Bold : FontStyle.Normal;
            t.text = words; t.color = Color.white; t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.material = material; t.raycastTarget = false;
            return t;
        }

        static void Fill(RectTransform r) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }


        class Hover : MonoBehaviour
        {
            public float Height, Units; public Vector3 Home; bool faced;

            void Start()
            {   // (the canvas is Units tall in its own measure: scaled so that is Height in the world, whatever the logo's scale)
                float parent = transform.parent.lossyScale.y;
                transform.localScale = Vector3.one * (Height / Mathf.Max(Units, 1f) / Mathf.Max(parent, 1e-6f));
            }

            void LateUpdate()
            {
                var head = VRTK.VRTK_DeviceFinder.HeadsetTransform();
                transform.position = transform.parent.TransformPoint(Home);
                if (!faced && head != null)
                {   // (turned once to face you, upright - about the vertical only - and then left facing that way, with the
                    //  logo: it turns only when the logo does)
                    Vector3 away = transform.position - head.position; away.y = 0f;
                    if (away.sqrMagnitude > 0.0001f) { transform.rotation = Quaternion.LookRotation(away, Vector3.up); faced = true; enabled = false; }      // (placed and turned: from here it simply goes with the logo, and this has nothing left to do each frame)
                }
            }
        }
    }
}
