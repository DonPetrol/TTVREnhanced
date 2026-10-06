// Tiny Town Enhanced - the wrist menu's Speech tab becomes "Edit".
// Pick a model up (or take a new one from the build menu) and the tab shows that model: its name in the header, and
// Smoothness, Metallic, Tint, Glow Color, Transparency, Unlit and Cast Shadows for it (Looks.cs draws
// and saves them). The model stays chosen after you
// put it down, until you pick up something else. Reset, in the header, gives it the game's own look back; Copy and
// Paste, at the bottom, take one model's look to another (pick the first up, Copy, pick the second up, Paste).
// Pick a speech bubble up and the tab is the game's own speech page for it, as before. Whichever you picked up
// last is what the tab shows.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class EditTab : IModule
    {
        public string Name { get { return "EditTab"; } }
        const string Model = "Model", Speech = "Speech", TabName = "Edit";
        static readonly MenuPage page = new MenuPage("EditTab", new[] { Model, Speech }, null);
        static GameObject chosen; static WristMenu menu; static WristSpeechPage built; static Text hint; static GameObject reset;

        static bool Has() { return chosen != null; }

        public void Start(Plugin plugin)
        {
            Func<float, string> percent = v => Mathf.RoundToInt(v * 100f) + "%";
            page.AddSlider(Model, "Smoothness", 0f, 1f, 0.05f, percent, () => Looks.Read(chosen).Smooth, v => Change(delegate (ref Looks.Values l) { l.Smooth = v; }), Has);
            page.AddSlider(Model, "Metallic", 0f, 1f, 0.05f, percent, () => Looks.Read(chosen).Metal, v => Change(delegate (ref Looks.Values l) { l.Metal = v; }), Has);
            // ("Costs FPS": these give the model a material and a copy of its texture all of its own - see Looks.cs)
            page.AddColor(Model, "Tint (Costs FPS)", () => Looks.Read(chosen).Tint, c => Change(delegate (ref Looks.Values l) { l.Tint = c; }), Has);
            // the glow is either one colour over the whole model, or the model's own colours (its texture) at a strength
            page.AddColor(Model, "Glow Color (Costs FPS)", () => Looks.Read(chosen).Glow, c => Change(delegate (ref Looks.Values l) { l.Glow = c; }), () => Has() && !Looks.Read(chosen).GlowMap);
            page.AddCheckbox(Model, "Glow From Texture (Costs FPS)", "The model glows in its own colours.", () => Looks.Read(chosen).GlowMap, on => Change(delegate (ref Looks.Values l) { l.GlowMap = on; }), Has);
            page.AddSlider(Model, "Glow Strength", 0.1f, Looks.MaxGlow, 0.1f, percent, () => Looks.Read(chosen).GlowStrength, v => Change(delegate (ref Looks.Values l) { l.GlowStrength = v; }), () => Has() && Looks.Read(chosen).GlowMap);
            page.AddSlider(Model, "Transparency (Costs FPS)", 0f, 0.9f, 0.05f, percent, () => 1f - Looks.Read(chosen).Alpha, v => Change(delegate (ref Looks.Values l) { l.Alpha = 1f - v; }), Has);
            page.AddCheckbox(Model, "Unlit (Costs FPS)", "Full brightness whatever the world's light.", () => Looks.Read(chosen).Unlit, on => Change(delegate (ref Looks.Values l) { l.Unlit = on; }), Has);
            page.AddCheckbox(Model, "Cast Shadows", "This model casts a shadow.", () => !Looks.Read(chosen).NoShadow, on => Change(delegate (ref Looks.Values l) { l.NoShadow = !on; }), Has);
            page.Shown = delegate { Dress(); };
            page.NoButtons = true;

            var h = plugin.Harmony; var me = typeof(EditTab);
            h.Patch(AccessTools.Method(typeof(WristMenu), "ResetForPlay"), null, new HarmonyMethod(me, "Rename"));
            h.Patch(AccessTools.Method(typeof(WristMenu), "OnSpeechTabClicked"), null, new HarmonyMethod(me, "Dress"));
            h.Patch(AccessTools.Method(typeof(WristSpeechPage), "Activate"), null, new HarmonyMethod(me, "Opened"));
            h.Patch(AccessTools.Method(typeof(WristSpeechPage), "Deactivate"), null, new HarmonyMethod(me, "Closed"));
            h.Patch(AccessTools.Method(typeof(PlayState), "PickupObject"), null, new HarmonyMethod(me, "Picked"));
            h.Patch(AccessTools.PropertySetter(typeof(WristSpeechPage), "SpeechBubble"), null, new HarmonyMethod(me, "Bubble"));
            // (the routine the game runs for anything picked up, speech bubbles included; it comes in two forms, and this is the one with four inputs)
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(PlayState)))
                if (m.Name == "SetPickupState" && m.GetParameters().Length == 4) h.Patch(m, null, new HarmonyMethod(me, "Held"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        delegate void Edit(ref Looks.Values look);
        static void Change(Edit edit)
        {
            if (chosen == null) return;
            Looks.Values look = Looks.Read(chosen);
            edit(ref look);
            Looks.Write(chosen, look);
        }

        // a model was picked up (from the world, or new from the build menu): it is the one the page is about
        static void Picked(GameObject go)
        {
            try
            {
                if (go == null) return;
                GameObject node = go.GetComponent<MeshCollider>() == null && go.transform.parent != null && go.transform.parent.GetComponent<MeshCollider>() != null ? go.transform.parent.gameObject : go;
                MeshData data = MeshDataManager.GetInstance().GetMeshData(node.name);
                if (data == null) return;
                // (a posed character has a name of its own, "Name_00001": the character it is one of is what is remembered,
                // found the way the game does in CharacterManager.Assign - the name without its last six letters)
                string model = node.name;
                if (data.type == MeshDataType.Character && model.Length > 6) model = model.Substring(0, model.Length - 6);
                chosen = node; Looks.LastHeld = node; Recent.Used(model);
                Turn(Model);
            }
            catch (Exception e) { Plugin.Log.LogWarning("EditTab: " + e.Message); }
        }

        // a speech bubble was picked up, or the game chose one (which it does when a bubble is put down)
        static bool speechHeld;
        static void Held(GameObject go) { try { speechHeld = go != null && go.GetComponent<SpeechBubble>() != null; if (speechHeld) Turn(Speech); } catch (Exception) { } }
        // (told there is no bubble - one was thrown away, or the world was left - none is held any more: without this the
        // tab would go on opening at an empty speech page instead of the last model)
        static void Bubble(SpeechBubble value) { if (value != null) Turn(Speech); else speechHeld = false; }

        static void Turn(string category)
        {
            page.Current = category;
            if (page.Visible) page.Show();
        }

        static string NameOf(GameObject node)
        {
            MeshData data = MeshDataManager.GetInstance().GetMeshData(node.name);
            string name = data != null && !string.IsNullOrEmpty(data.displayName) ? data.displayName : node.name;
            return name.Replace('_', ' ').Trim();
        }

        // the tab's wording, the header, the hint and the Reset button, to suit what is showing
        static void Rename(WristMenu __instance)
        {
            try { var text = __instance.speechPageTabButton.GetChild(0).GetComponent<Text>(); if (text != null) text.text = TabName; }
            catch (Exception e) { Plugin.Log.LogWarning("EditTab: " + e.Message); }
        }

        static void Dress()
        {
            try
            {
                if (menu == null || built == null || !built.gameObject.activeSelf) return;
                bool model = page.Current == Model;
                if (menu.title != null)
                {
                    if (titleSize == 0) titleSize = menu.title.fontSize;
                    menu.title.fontSize = titleSize;
                    if (model && Has()) MenuPage.FitTitle(menu.title, NameOf(chosen), TitleRoom, titleSize, Mathf.RoundToInt(titleSize * 0.55f));
                    else menu.title.text = model ? TabName : Speech;
                }
                if (hint != null) hint.gameObject.SetActive(model && !Has());
                if (reset != null) reset.SetActive(model && Has());
                if (copy != null) copy.SetActive(model && Has());
                if (paste != null) paste.SetActive(model && Has() && copied.HasValue);
            }
            catch (Exception) { }
        }

        static void Closed()
        {
            if (reset != null) reset.SetActive(false);
            if (menu != null && menu.title != null && titleSize > 0) menu.title.fontSize = titleSize;      // (the other tabs' titles are the usual size)
        }
        static GameObject copy, paste; static Looks.Values? copied; const float BottomY = -362f;      // (either side of the page arrows)
        static int titleSize; const float TitleRoom = 760f;       // the header's usual font size; the room a name has before the frame-rate counter

        static void Opened(WristSpeechPage __instance)
        {
            try
            {
                if (built != __instance) Prepare(__instance);
                if (page.Current == Speech && __instance.SpeechBubble == null && !speechHeld) page.Current = Model;       // (no bubble to show: the hint, or the last model)
                bool fresh = reset == null;
                page.Open(__instance.transform, menu);
                // (Open has shown the page; it is shown again only the first time, for the button that has just been made)
                if (fresh) { reset = page.HeaderButton("Reset", delegate { Looks.Clear(chosen); page.Show(); }); page.Show(); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("EditTab: " + e); }
        }

        static void Prepare(WristSpeechPage speechPage)
        {
            Transform t = speechPage.transform;
            menu = speechPage.GetComponentInParent<WristMenu>();
            if (menu == null) menu = UnityEngine.Object.FindObjectOfType<WristMenu>();
            if (menu == null) throw new Exception("wrist menu not found");

            // everything the game put on this page goes into one holder, shown as the "Speech" part
            var holder = Holder(t, "Speech (game)");
            var existing = new List<Transform>();
            foreach (Transform child in t) if (child != holder) existing.Add(child);
            foreach (var child in existing) child.SetParent(holder, false);
            page.SetCustom(Speech, holder.gameObject);

            // the "Model" part has, besides its rows, a line for when no model has been picked up yet
            var words = Holder(t, "Model hint");
            hint = MenuPage.Label(words, 1000f, 200f, 0f, 0f, 44);
            hint.text = "Pick up a model to edit how it looks,\nor a speech bubble to edit what it says.";
            // and, at the bottom, Copy (remember this model's look) and Paste (give the remembered look to this model)
            copy = MenuPage.Paint(MenuPage.Button(menu, words, "Copy", 240f, 64f, -520f, BottomY, delegate { if (Has()) { copied = Looks.Read(chosen); Dress(); } }), MenuPage.Role.Action);
            paste = MenuPage.Paint(MenuPage.Button(menu, words, "Paste", 240f, 64f, 520f, BottomY, delegate { if (Has() && copied.HasValue) { Looks.Write(chosen, copied.Value); page.Show(); } }), MenuPage.Role.Action);
            copy.SetActive(false); paste.SetActive(false);
            page.SetCustom(Model, words.gameObject);
            built = speechPage;
        }

        static RectTransform Holder(Transform page, string name)
        {
            var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rt.SetParent(page, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = ((RectTransform)page).sizeDelta;
            rt.localPosition = Vector3.zero; rt.localRotation = Quaternion.identity; rt.localScale = Vector3.one;
            return rt;
        }
    }
}
