// Tiny Town Enhanced - the build menu gets search, Workshop sorting and an author filter, and arrow buttons.
// All of it is extra buttons beside the game's own row of tabs (All, Town, Farm...), made as copies of those tabs:
//   Search / Clear   (above "All")   a keyboard to type on; the menu then shows, in one place, every model whose name
//                                     has what was typed in it (Workshop models: their title or their author's
//                                     name), whichever category it is from
//   Sort             (above Search)  steps through Default, Name and Last Used (the models you picked up last come
//                                     first); in the Workshop category also Newest (subscribed to last), Updated
//                                     and Author
//   Author           (beside Sort, and only while the Workshop category is showing)
//                                     shows the people whose models you have, to pick one (a search looks
//                                     through everyone's models all the same)
//   arrows           up / down, right of the models: the category before / after;
//                    left / right, side by side under those: the models one column along
// Under the name of the model being pointed at, a Workshop model's author is shown (when the title leaves no doubt
// whose it is). Every button, the game's tabs included, has a dark see-through backing so that it can be read against
// the world.
// How it works: the game asks one routine, MeshDataManager.GetNamesInCategory, which models each category has; this
// module changes its answer, then has the menu lay its categories out again - the step the game itself runs when a
// tab is touched.
// Should something go wrong in the every-frame work three times, or the buttons fail to be made, the module switches
// itself off and the menu goes back to the game's own lists.
// Recent (below) keeps the models used last; WorkshopInfo keeps who made each Workshop model and when: FastSync.cs
// is told by Steam at startup. SafeFile (at the end) writes their two text files.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class BuildMenu : IModule
    {
        public string Name { get { return "BuildMenu"; } }
        const string Workshop = "Workshop"; const int Longest = 24, AuthorsPerPage = 10;
        // the orders Sort steps through: in the Workshop category, and in the others (the numbers are places in Sorts)
        static readonly string[] Sorts = { "Default", "Name", "Newest", "Updated", "Author", "Last Used" };
        static readonly int[] OtherSorts = { 0, 1, 5 };
        const int ByName = 1, ByNewest = 2, ByUpdated = 3, ByAuthor = 4, ByLastUsed = 5;
        static readonly Type I = typeof(Inventory), C = I.GetNestedType("Category", BindingFlags.NonPublic);
        static readonly FieldInfo fCategories = AccessTools.Field(I, "categories"), fFilter = AccessTools.Field(I, "filter"),
                                  fY = AccessTools.Field(I, "positionY"), fHeight = AccessTools.Field(I, "categoryHeight"), fPrevious = AccessTools.Field(I, "updatePreviousCategory"),
                                  fLeftHanded = AccessTools.Field(I, "isLeftHanded"), fLeft = AccessTools.Field(I, "leftHand"), fRight = AccessTools.Field(I, "rightHand"),
                                  fOriginal = AccessTools.Field(typeof(PushButton), "originalColor"),
                                  // (one of the menu's categories: its name, its models and where it has been moved along to)
                                  cName = AccessTools.Field(C, "name"), cModels = AccessTools.Field(C, "meshData"), cPosition = AccessTools.Field(C, "position"),
                                  cMin = AccessTools.Field(C, "minPositionX"), cMax = AccessTools.Field(C, "maxPositionX"), cColumns = AccessTools.Field(C, "numMeshCols");
        static readonly MethodInfo mModels = AccessTools.Method(I, "CreateMeshDataArray");

        // what is being asked for
        static string term = ""; static ulong author; static int sort, otherSort;      // (otherSort: a place in OtherSorts)

        // the menu and what was added to it
        static Inventory inv; static bool failed, built; static int errors; static GameObject template; static Transform canvas, overlay;
        static GameObject search, clear, sortButton, authorButton, keys; static Text typed;
        static readonly List<Image> arrows = new List<Image>(); static readonly List<GameObject> authorButtons = new List<GameObject>();
        static readonly List<ulong> authors = new List<ulong>(); static int authorPage; static string[] names;
        static bool typing, picking, onWorkshop; static float shownAlpha = -1f, shownNameAlpha = -1f;
        static Image templateImage; static TextMesh byline; static Sprite chevron;
        // the dark backings: one for each button, on a sheet of their own behind the buttons
        // (backsDirty: a button was shown or hidden, so the backings are to be looked at again)
        const float BackAlpha = 0.45f; static bool backsDirty;
        static Transform backSheet; static readonly List<Image> backs = new List<Image>(); static readonly List<GameObject> backed = new List<GameObject>(); static readonly List<bool> backFades = new List<bool>();
        // where things go, in the tab row's own units: the row, a tab's size, the step to the next tab and to the tab above
        static float rowY, width, height, stepX, stepY, left, span;

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(BuildMenu);
            h.Patch(AccessTools.Method(typeof(MeshDataManager), "GetNamesInCategory"), null, new HarmonyMethod(me, "Names"));
            h.Patch(AccessTools.Method(I, "Update"), null, new HarmonyMethod(me, "After"));
            h.Patch(AccessTools.PropertySetter(I, "ObjectText"), null, new HarmonyMethod(me, "Pointed"));
            h.Patch(AccessTools.Method(I, "Open"), new HarmonyMethod(me, "Opening"), new HarmonyMethod(me, "Opened"));
            WorldFile.BeforeSave += Recent.Save;
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // the menu is about to be opened from fully closed: a keyboard or list of authors left up is put away. (The game
        // switches the closed menu off, which can happen before the every-frame routine below has seen it closed; the
        // menu would then come back with the keyboard still up and no models. Turning the hand away and back while
        // typing does not get here: the menu is then not fully closed.)
        static void Opening(Inventory __instance)
        {
            try { if (__instance == inv && !failed && built && (typing || picking) && __instance.IsFullyClosed) Close(); }
            catch (Exception e) { Plugin.Log.LogWarning("BuildMenu: " + e.Message); }
        }

        // the menu is opened: with models in order of use, the order is brought up to date
        static void Opened()
        {
            try { if (inv != null && !failed && Recent.Changed && (sort == ByLastUsed || OtherSorts[otherSort] == ByLastUsed)) { Recent.Changed = false; Refresh(); } }
            catch (Exception e) { Plugin.Log.LogWarning("BuildMenu: " + e.Message); }
        }

        // the game writes the name of the model being pointed at: a Workshop model's author goes under it.
        // The game gives the title, not which model. So the author is shown only when the title can be of one person's
        // Workshop models alone: every Workshop model of that title has the same known owner, and (while searching,
        // when the game's own models are shown too) none of the game's models has that title.
        static string pointedAt;
        static void Pointed(string value)
        {
            if (byline == null || failed || value == pointedAt) return;
            pointedAt = value;
            try
            {
                string by = "";
                if ((onWorkshop || term.Length > 0) && !string.IsNullOrEmpty(value))
                {
                    if (titles == null) Titles();
                    List<string> models;
                    if (titles.TryGetValue(value, out models) && !(term.Length > 0 && ownTitles.Contains(value)))
                    {
                        // (the owners are looked up now, not kept: Steam may have told us since the titles were listed)
                        ulong owner = WorkshopInfo.Of(models[0]).Owner;
                        for (int i = 1; i < models.Count && owner != 0; i++) if (WorkshopInfo.Of(models[i]).Owner != owner) owner = 0;
                        if (owner != 0) by = "by " + WorkshopInfo.Author(owner);
                    }
                }
                byline.text = by;
            }
            catch (Exception e) { Plugin.Log.LogWarning("BuildMenu: " + e.Message); }
        }
        // the Workshop models of each title, and the titles of the game's own models (made when first wanted, and
        // again after the menu has been laid out afresh)
        static Dictionary<string, List<string>> titles; static HashSet<string> ownTitles;
        static void Titles()
        {
            var manager = MeshDataManager.GetInstance();
            var workshop = new Dictionary<string, List<string>>(); var own = new HashSet<string>();
            foreach (string category in manager.GetCategories())
            {
                List<string> all = AsTheyAre(manager, category, "All");
                if (all == null) continue;
                foreach (string name in all)
                {
                    MeshData data = manager.GetMeshData(name);
                    if (data == null || data.displayName == null) continue;
                    if (category != Workshop) { own.Add(data.displayName); continue; }
                    List<string> same;
                    if (!workshop.TryGetValue(data.displayName, out same)) workshop[data.displayName] = same = new List<string>();
                    same.Add(name);
                }
            }
            titles = workshop; ownTitles = own;
        }

        // ---- which models a category has
        // While something is being searched for, every category shows the same thing: all the models found, whichever
        // category they are from (category by category, in the game's order), under the title "Search".
        static bool asking;      // (true while this module asks the game for a category's models as they are)
        // a category's models as the game has them, whatever is being searched for or sorted by
        static List<string> AsTheyAre(MeshDataManager manager, string category, string filter)
        {
            bool was = asking; asking = true;
            try { return manager.GetNamesInCategory(category, filter); } finally { asking = was; }
        }
        // the models found by the search, kept for the rest of the frame: the menu asks once for each category, and
        // the answer is the same for all of them (Refresh starts afresh, so a change made in between is never missed)
        static List<string> kept; static int keptFrame, keptOrder; static string keptTerm, keptFilter;
        static void Names(string category, string filter, ref List<string> __result)
        {
            if (asking || __result == null) return;
            bool workshop = category == Workshop, searching = term.Length > 0;
            int order = workshop && !searching ? sort : OtherSorts[otherSort];
            if (!searching && order == 0 && !(workshop && author != 0)) return;
            try
            {
                if (searching && kept != null && keptFrame == Time.frameCount && keptOrder == order && keptTerm == term && keptFilter == filter) { __result = kept; return; }
                var manager = MeshDataManager.GetInstance();
                var list = new List<string>(__result.Count);              // (a list of our own: the one given may be the game's)
                if (searching)
                {
                    foreach (string each in manager.GetCategories())
                    {
                        List<string> part = AsTheyAre(manager, each, filter);
                        if (part != null) Take(manager, part, each == Workshop, list);
                    }
                }
                else Take(manager, __result, workshop, list);
                if (order != 0)
                {
                    Comparison<string> byTitle = (a, b) => string.Compare(manager.GetMeshData(a).displayName, manager.GetMeshData(b).displayName, StringComparison.OrdinalIgnoreCase);
                    if (order == ByName) list.Sort(byTitle);
                    else if (order == ByNewest) list.Sort((a, b) => WorkshopInfo.Of(b).Subscribed.CompareTo(WorkshopInfo.Of(a).Subscribed));
                    else if (order == ByUpdated) list.Sort((a, b) => WorkshopInfo.Of(b).Updated.CompareTo(WorkshopInfo.Of(a).Updated));
                    else if (order == ByAuthor) list.Sort((a, b) =>
                    {
                        // (WorkshopInfo.Author asks Steam once for each person in a frame, not at every comparison)
                        int c = string.Compare(WorkshopInfo.Author(WorkshopInfo.Of(a).Owner), WorkshopInfo.Author(WorkshopInfo.Of(b).Owner), StringComparison.OrdinalIgnoreCase);
                        return c != 0 ? c : byTitle(a, b);
                    });
                    else
                    {
                        // the models picked up last come first, the latest first of all; the rest follow as they were
                        var place = new Dictionary<string, int>();
                        for (int k = 0; k < list.Count; k++) { int used = Recent.Place(list[k]); place[list[k]] = used < int.MaxValue ? used - 100000 : k; }
                        list.Sort((a, b) => place[a].CompareTo(place[b]));
                    }
                }
                if (searching) { kept = list; keptFrame = Time.frameCount; keptOrder = order; keptTerm = term; keptFilter = filter; }
                __result = list;
            }
            catch (Exception e) { Plugin.Log.LogWarning("BuildMenu: " + e.Message); }
        }
        // the models of `from` that pass what is being asked for
        // (a search looks through every author's models: the Author button is only there in the Workshop category, so
        // a choice made there would otherwise hide models from a search without anything saying so)
        static void Take(MeshDataManager manager, List<string> from, bool workshop, List<string> into)
        {
            bool searching = term.Length > 0;
            foreach (string name in from)
            {
                MeshData data = manager.GetMeshData(name);
                if (data == null) continue;
                if (workshop && author != 0 && !searching && WorkshopInfo.Of(name).Owner != author) continue;
                if (searching && !Has(data.displayName, term) && !(workshop && Has(WorkshopInfo.Author(WorkshopInfo.Of(name).Owner), term))) continue;
                into.Add(name);
            }
        }
        static bool Has(string words, string part) { return words != null && words.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }

        // the menu lays every category out again (what the game does when one of its tabs is touched); the number of models found
        static int Refresh()
        {
            int found = 0;
            kept = null; titles = null; pointedAt = null;                 // (what was worked out from the models before is worked out again)
            var manager = MeshDataManager.GetInstance(); string filter = (string)fFilter.GetValue(inv);
            foreach (object category in (IList)fCategories.GetValue(inv))
            {
                List<string> list = manager.GetNamesInCategory((string)cName.GetValue(category), filter);
                if (list == null) continue;                               // (Speech: not models)
                if (term.Length > 0) found = list.Count; else found += list.Count;      // (searching: every category has the same models)
                int perGroup = inv.numCols - 1, inGroup = inv.numRows * perGroup, columns = Mathf.CeilToInt((float)list.Count / inGroup) * perGroup;
                cModels.SetValue(category, mModels.Invoke(inv, new object[] { list, perGroup, inGroup, columns }));
                cPosition.SetValue(category, 0f);
                cMin.SetValue(category, 0f);
                cMax.SetValue(category, Mathf.Max(1, columns) * inv.colWidth);      // (never 0, which the game cannot move along: no models)
                cColumns.SetValue(category, columns);
            }
            return found;
        }

        // ---- every frame the menu is switched on, after the menu's own work
        // (___currentCategoryIndex: the menu's own count of where it is, handed over by Harmony - no looking up each frame)
        static void After(Inventory __instance, int ___currentCategoryIndex)
        {
            if (failed) return;
            try
            {
                if (inv != __instance)
                {
                    if (fCategories.GetValue(__instance) == null) return;      // (not set up yet)
                    Build(__instance);
                }
                bool workshop = names[___currentCategoryIndex] == Workshop;
                if (workshop != onWorkshop) { onWorkshop = workshop; authorButton.SetActive(workshop); if (!workshop && picking) Close(); Label(); byline.text = ""; pointedAt = null; }
                // (the game writes the category's name every frame, so "Search" is written over it every frame - but not while nothing is to be seen)
                if (term.Length > 0 && !inv.IsFullyClosed) inv.categoryText.text = "Search";
                if (typing || picking)
                {
                    if (inv.IsFullyClosed) Close();
                    else inv.itemRoot.gameObject.SetActive(false);              // (the models make way for the keyboard or the list)
                }
                // the arrows' pictures, the backings and the author's name fade in and out with the game's own things:
                // they are only touched when that fade has moved on, or a button has been shown or hidden
                float alpha = templateImage.color.a;
                if (alpha != shownAlpha || backsDirty)
                {
                    shownAlpha = alpha; backsDirty = false;
                    foreach (Image arrow in arrows) arrow.color = new Color(1f, 1f, 1f, alpha);
                    for (int i = 0; i < backs.Count; i++)
                    {
                        bool there = backed[i].activeInHierarchy;
                        if (backs[i].enabled != there) backs[i].enabled = there;
                        if (there) backs[i].color = new Color(0f, 0f, 0f, BackAlpha * (backFades[i] ? alpha : 1f));
                    }
                }
                Color nameColor = inv.objectText.color;
                if (nameColor.a != shownNameAlpha) { shownNameAlpha = nameColor.a; byline.color = nameColor; }
            }
            catch (Exception e)
            {
                // one passing error does not switch the module off; a menu that could not be made, or the third error, does.
                // What was being asked for is then dropped: with the buttons dead there would be no way to clear it.
                if (built && ++errors < 3) { Plugin.Log.LogWarning("BuildMenu: " + e.Message); return; }
                failed = true; Plugin.Log.LogWarning("BuildMenu switched off: " + e);
                term = ""; author = 0; sort = otherSort = 0; kept = null;
                try { if (overlay != null) Close(); if (inv != null) Refresh(); } catch (Exception) { }
            }
        }

        // ---- making the buttons
        static void Build(Inventory inventory)
        {
            built = false; inv = inventory; typing = picking = onWorkshop = false; shownAlpha = shownNameAlpha = -1f; backsDirty = true;
            arrows.Clear(); authorButtons.Clear(); backs.Clear(); backed.Clear(); backFades.Clear();
            var categories = (IList)fCategories.GetValue(inv);
            titles = null; pointedAt = null; kept = null;
            names = new string[categories.Count];
            for (int i = 0; i < names.Length; i++) names[i] = (string)cName.GetValue(categories[i]);

            canvas = inv.filterCanvasTransform;
            Transform all = canvas.Find("All");
            if (all == null || all.GetComponent<PushButton>() == null) throw new Exception("the build menu's tabs are not as expected");
            template = all.gameObject; templateImage = template.GetComponent<Image>();
            chevron = MenuPage.SpriteNamed("chevron");                        // (looked for once: it is a search through every picture)
            Rect size = ((RectTransform)all).rect; width = size.width; height = size.height;
            rowY = all.localPosition.y;

            // the row of tabs, and the tabs stacked above its end: where they are tells the spacing
            var row = new List<float>(); var above = new List<Vector3>();
            foreach (Transform child in canvas)
            {
                Vector3 p = child.localPosition;
                if (Mathf.Abs(p.y - rowY) < height * 0.5f) row.Add(p.x); else above.Add(p);
            }
            row.Sort();
            left = row[0] - width * 0.5f; span = row[row.Count - 1] - row[0] + width;
            stepX = row.Count > 1 ? row[1] - row[0] : width * 1.15f;
            stepY = float.MaxValue;
            foreach (Vector3 p in above) if (p.y > rowY) stepY = Mathf.Min(stepY, p.y - rowY);
            if (stepY == float.MaxValue || stepY < height) stepY = height * 1.2f;

            // above the left end of the row: Search and Clear, and above those Sort and Author
            float x0 = row[0], x1 = row[0] + stepX;
            search = Make(canvas, "TinyTownEnhanced Search", "Search", x0, rowY + stepY, width, Type);
            clear = Make(canvas, "TinyTownEnhanced Clear", "Clear", x1, rowY + stepY, width, delegate { term = ""; Apply(); });
            sortButton = Make(canvas, "TinyTownEnhanced Sort", "", x0, rowY + 2f * stepY, width, delegate { if (onWorkshop && term.Length == 0) sort = (sort + 1) % Sorts.Length; else otherSort = (otherSort + 1) % OtherSorts.Length; Apply(); });
            authorButton = Make(canvas, "TinyTownEnhanced Author", "", x1, rowY + 2f * stepY, width, PickAuthor);
            authorButton.SetActive(false);

            // the arrows. Where the models are, in the tabs' own units: the menu shows them across its width, from its
            // foot (where the pointed-at model's name is written) up to its height.
            float wide = inv.numCols * inv.colWidth, tall = (float)fHeight.GetValue(inv);
            Vector3 c0 = At(-wide * 0.5f, 0f), c1 = At(wide * 0.5f, tall);
            float leftEdge = Mathf.Min(c0.x, c1.x), rightEdge = Mathf.Max(c0.x, c1.x), foot = Mathf.Min(c0.y, c1.y), top = Mathf.Max(c0.y, c1.y), middle = (foot + top) * 0.5f;
            float box = height * 1.15f;
            Arrow("Up", rightEdge + box * 0.75f, middle + box * 0.6f, box, -90f, delegate { Category(-1f); });
            Arrow("Down", rightEdge + box * 0.75f, middle - box * 0.6f, box, 90f, delegate { Category(1f); });
            Arrow("Left", rightEdge + box * 0.3f, middle - box * 1.75f, box * 0.85f, 0f, delegate { inv.ShiftHorizontal(-inv.colWidth); });
            Arrow("Right", rightEdge + box * 1.2f, middle - box * 1.75f, box * 0.85f, 180f, delegate { inv.ShiftHorizontal(inv.colWidth); });

            // the author of the Workshop model being pointed at, under its name and a little smaller
            var line = (GameObject)UnityEngine.Object.Instantiate(inv.objectText.gameObject, inv.objectText.transform.parent, false);
            line.name = "TinyTownEnhanced Author";
            byline = line.GetComponent<TextMesh>();
            byline.text = ""; byline.characterSize = inv.objectText.characterSize * 0.7f;
            // (a fixed step under the name. It is not measured from how the name is drawn: that measure is taken along
            // the world's axes, and the menu is turned with the hand, so it came out too large whenever the hand was tilted.)
            line.transform.localPosition = inv.objectText.transform.localPosition - new Vector3(0f, inv.rowHeight * 0.14f, 0f);

            // a sheet of our own over the models, for the keyboard and the list of authors (a copy of the tabs' sheet, emptied)
            var sheet = (GameObject)UnityEngine.Object.Instantiate(canvas.gameObject, canvas.parent, false);
            sheet.name = "TinyTownEnhanced Build Menu";
            overlay = sheet.transform;
            var old = new List<Transform>();
            foreach (Transform child in overlay) old.Add(child);
            foreach (Transform child in old) { child.SetParent(null, false); UnityEngine.Object.Destroy(child.gameObject); }

            // and one behind the tabs, for the dark backings
            var behind = (GameObject)UnityEngine.Object.Instantiate(sheet, canvas.parent, false);
            behind.name = "TinyTownEnhanced Build Menu Backings";
            backSheet = behind.transform;
            backSheet.SetSiblingIndex(canvas.GetSiblingIndex());
            var mine = behind.GetComponent<Canvas>(); var theirs = canvas.GetComponent<Canvas>();
            if (mine != null && theirs != null) { mine.overrideSorting = true; mine.sortingLayerID = theirs.sortingLayerID; mine.sortingOrder = theirs.sortingOrder - 1; }
            foreach (Transform child in canvas) Back(child.gameObject, true);

            typed = new GameObject("Typed", typeof(RectTransform)).AddComponent<Text>();
            typed.transform.SetParent(overlay, false);
            typed.gameObject.layer = sheet.layer;
            var words = template.transform.GetChild(0).GetComponent<Text>();
            typed.font = words.font; typed.fontSize = Mathf.RoundToInt(words.fontSize * 1.4f); typed.color = Color.white; typed.alignment = TextAnchor.MiddleCenter; typed.raycastTarget = false;
            typed.horizontalOverflow = HorizontalWrapMode.Overflow; typed.verticalOverflow = VerticalWrapMode.Overflow;
            typed.rectTransform.sizeDelta = new Vector2(span, stepY * 0.8f);
            typed.rectTransform.localPosition = new Vector3(left + span * 0.5f, rowY - stepY * 1.05f, 0f);

            // the keyboard: a copy of the speech bubble page's, as wide as most of the row, under the typed words
            keys = null;
            TouchKeyboard original = null;
            foreach (var page in Resources.FindObjectsOfTypeAll<WristSpeechPage>())
                if (page.gameObject.scene.IsValid()) original = AccessTools.Field(typeof(WristSpeechPage), "keyboard").GetValue(page) as TouchKeyboard;
            if (original != null)
            {
                keys = (GameObject)UnityEngine.Object.Instantiate(original.gameObject, overlay, false);
                keys.name = "Keyboard";
                keys.transform.localRotation = Quaternion.identity; keys.transform.localScale = Vector3.one; keys.transform.localPosition = Vector3.zero;
                keys.SetActive(true);
                Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(keys.transform);      // (the keys stay on their own layer: it is the one hands can press)
                float scale = b.size.x > 0f ? span * 0.62f / b.size.x : 1f;
                keys.transform.localScale = Vector3.one * scale;
                keys.transform.localPosition = new Vector3(left + span * 0.5f - b.center.x * scale, rowY - stepY * 1.6f - b.size.y * scale * 0.5f - b.center.y * scale, 0f);
                var keyboard = keys.GetComponent<TouchKeyboard>();
                keyboard.onKeyPressed = new TouchKeyboard.OnKeyPressed();
                keyboard.onKeyPressed.AddListener(Key);
            }
            else Plugin.Log.LogWarning("BuildMenu: the keyboard was not found (no typing: Search does nothing)");

            // the list of authors: three across, four down; the last two places are the page buttons
            float cell = span / 3f;
            for (int i = 0; i < AuthorsPerPage + 2; i++)
            {
                int place = i;
                authorButtons.Add(Make(overlay, "Author " + i, "", left + cell * (i % 3 + 0.5f), rowY - stepY * (1.5f + (i / 3) * 1.1f), cell - 12f, delegate { AuthorPressed(place); }));
                Back(authorButtons[i], false);
            }
            overlay.gameObject.SetActive(false);
            Label();
            built = true; errors = 0;
        }

        // a point of the menu (across, up: in the menu's own measure) in the tabs' units
        static Vector3 At(float x, float y) { return canvas.InverseTransformPoint(inv.transform.TransformPoint(new Vector3(x, y, inv.depthOffset))); }

        // a dark see-through backing behind a button (`fades`: with the game's tabs, as the menu opens and closes)
        static void Back(GameObject button, bool fades)
        {
            var image = new GameObject("Backing", typeof(RectTransform)).AddComponent<Image>();
            image.gameObject.layer = backSheet.gameObject.layer;
            image.transform.SetParent(backSheet, false);
            var rt = (RectTransform)button.transform;
            image.rectTransform.sizeDelta = rt.rect.size;
            image.rectTransform.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
            image.color = new Color(0f, 0f, 0f, 0f); image.raycastTarget = false;
            backs.Add(image); backed.Add(button); backFades.Add(fades);
        }

        // a copy of one of the game's tabs, with our own wording and what it does
        static GameObject Make(Transform parent, string name, string label, float x, float y, float wide, Action pressed)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(template, parent, false);
            go.name = name;
            var rt = (RectTransform)go.transform;
            rt.localPosition = new Vector3(x, y, template.transform.localPosition.z);
            rt.sizeDelta = new Vector2(rt.sizeDelta.x + wide - width, rt.sizeDelta.y);
            var image = go.GetComponent<Image>();
            image.sprite = inv.boxButtonSprite;
            image.color = parent == canvas ? templateImage.color : Color.white;
            var text = go.transform.GetChild(0).GetComponent<Text>();
            text.text = label; text.color = image.color;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 8; text.resizeTextMaxSize = Mathf.Max(8, text.fontSize);
            fOriginal.SetValue(go.GetComponent<PushButton>(), Color.white);       // (what it goes back to after flashing when pressed)
            MenuPage.OnPress(go, "BuildMenu", delegate
            {
                if (!Idle()) return;                                            // (the game's rule for its tabs: not with a hand that is busy)
                inv.soundManager.Play(SoundManager.SoundEffect.BUTTON_CLICK_POSITIVE);
                pressed();
            });
            go.SetActive(true);
            return go;
        }

        static void Arrow(string name, float x, float y, float wide, float turn, Action pressed)
        {
            GameObject go = Make(canvas, "TinyTownEnhanced " + name, "", x, y, wide, pressed);
            if (chevron == null) { go.transform.GetChild(0).GetComponent<Text>().text = name == "Up" ? "^" : name == "Down" ? "v" : name == "Left" ? "<" : ">"; return; }
            var icon = new GameObject("Arrow", typeof(RectTransform)).AddComponent<Image>();
            icon.gameObject.layer = go.layer;
            icon.transform.SetParent(go.transform, false);                      // (after the words, which the game expects to come first)
            icon.rectTransform.sizeDelta = new Vector2(wide * 0.5f, wide * 0.5f);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, rt.sizeDelta.y + wide - height);       // (square)
            go.GetComponent<PushButton>().UpdateBoxCollider();
            icon.rectTransform.localRotation = Quaternion.Euler(0f, 0f, turn);  // (the picture points left)
            icon.sprite = chevron; icon.preserveAspect = true; icon.raycastTarget = false;
            arrows.Add(icon);
        }

        static bool Idle()
        {
            var hand = (bool)fLeftHanded.GetValue(inv) ? fRight.GetValue(inv) as Hand : fLeft.GetValue(inv) as Hand;
            return hand == null || (hand.IsHandIdle && !hand.IsRecentlyIdle);
        }

        // ---- what the buttons do
        // the category before or after, in one step (as the thumbstick does it)
        static void Category(float direction)
        {
            float tall = (float)fHeight.GetValue(inv), all = tall * names.Length;
            fY.SetValue(inv, Mathf.Repeat((float)fY.GetValue(inv) + direction * tall, all));
            fPrevious.SetValue(inv, true);
        }

        static void Apply()
        {
            int found = Refresh();
            Label();
            if (typing) typed.text = (term.Length > 0 ? term : "Type a name") + "_      " + found + " found";
        }

        static void Label()
        {
            search.transform.GetChild(0).GetComponent<Text>().text = term.Length > 0 ? "\"" + term + "\"" : "Search";
            clear.SetActive(term.Length > 0); backsDirty = true;
            sortButton.transform.GetChild(0).GetComponent<Text>().text = "Sort: " + Sorts[onWorkshop && term.Length == 0 ? sort : OtherSorts[otherSort]];
            authorButton.transform.GetChild(0).GetComponent<Text>().text = author != 0 ? WorkshopInfo.Author(author) : "Author: All";
            authorButton.GetComponent<Image>().sprite = author != 0 ? inv.boxButtonFilledSprite : inv.boxButtonSprite;
            search.GetComponent<Image>().sprite = term.Length > 0 ? inv.boxButtonFilledSprite : inv.boxButtonSprite;
        }

        static void Close()
        {
            typing = picking = false; backsDirty = true;
            overlay.gameObject.SetActive(false);
        }

        static void Type()
        {
            if (typing || keys == null) { Close(); return; }
            typing = true; picking = false; backsDirty = true;
            overlay.gameObject.SetActive(true);
            keys.SetActive(true); typed.gameObject.SetActive(true);
            foreach (GameObject b in authorButtons) b.SetActive(false);
            Apply();
        }

        static void Key(KeyCode code, bool capital)
        {
            try
            {
                inv.soundManager.Play(SoundManager.SoundEffect.BUTTON_CLICK_POSITIVE);
                if (code == KeyCode.Return) { Close(); return; }
                if (code == KeyCode.Backspace) { if (term.Length > 0) term = term.Substring(0, term.Length - 1); }
                else if (term.Length < Longest) term += TouchKeyboard.KeyCodeToString(code, capital);
                Apply();
            }
            catch (Exception e) { Plugin.Log.LogWarning("BuildMenu: " + e.Message); }
        }

        // the people whose Workshop models you have, the one with the most first
        static void PickAuthor()
        {
            if (picking) { Close(); return; }
            var count = new Dictionary<ulong, int>();
            List<string> all = AsTheyAre(MeshDataManager.GetInstance(), Workshop, "All");      // (every model, whatever is being asked for at the moment)
            if (all != null) foreach (string name in all) { ulong owner = WorkshopInfo.Of(name).Owner; if (owner != 0) { int n; count.TryGetValue(owner, out n); count[owner] = n + 1; } }
            authors.Clear(); authors.AddRange(count.Keys);
            authors.Sort((a, b) => count[b] != count[a] ? count[b].CompareTo(count[a]) : string.Compare(WorkshopInfo.Author(a), WorkshopInfo.Author(b), StringComparison.OrdinalIgnoreCase));
            authorCounts = count;
            WorkshopInfo.Save();                                                 // (names Steam has told us since startup are kept)
            picking = true; typing = false; authorPage = 0;
            overlay.gameObject.SetActive(true);
            if (keys != null) keys.SetActive(false);
            typed.gameObject.SetActive(authors.Count == 0);
            typed.text = "Nothing is known yet about who made your Workshop models.\nIt is asked of Steam when the game starts.";
            ShowAuthors();
        }
        static Dictionary<ulong, int> authorCounts;

        // place 0 of the first page is "All Authors"; the last two places turn the pages
        static void ShowAuthors()
        {
            backsDirty = true;
            int pages = Mathf.Max(1, (authors.Count + 1 + AuthorsPerPage - 1) / AuthorsPerPage);
            for (int i = 0; i < authorButtons.Count; i++)
            {
                GameObject b = authorButtons[i]; string label = null; bool chosen = false;
                if (i == AuthorsPerPage) label = pages > 1 && authorPage > 0 ? "< Previous" : null;
                else if (i == AuthorsPerPage + 1) label = pages > 1 && authorPage < pages - 1 ? "Next >" : null;
                else
                {
                    int at = authorPage * AuthorsPerPage + i - 1;                // (-1: "All Authors" comes first)
                    if (at == -1) { label = "All Authors"; chosen = author == 0; }
                    else if (at < authors.Count) { label = WorkshopInfo.Author(authors[at]) + " (" + authorCounts[authors[at]] + ")"; chosen = author == authors[at]; }
                }
                b.SetActive(label != null && authors.Count > 0);
                if (label == null) continue;
                b.transform.GetChild(0).GetComponent<Text>().text = label;
                b.GetComponent<Image>().sprite = chosen ? inv.boxButtonFilledSprite : inv.boxButtonSprite;
            }
        }

        static void AuthorPressed(int place)
        {
            if (place == AuthorsPerPage) { authorPage--; ShowAuthors(); return; }
            if (place == AuthorsPerPage + 1) { authorPage++; ShowAuthors(); return; }
            int at = authorPage * AuthorsPerPage + place - 1;
            author = at < 0 || at >= authors.Count ? 0UL : authors[at];
            Close();
            Apply();
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - the models picked up last (for the build menu's "Last Used" order), the latest first.
    // EditTab.cs says when a model is picked up; kept in "TTVREnhanced/RecentModels.txt" in the game's folder, written
    // whenever the world is saved.
    public static class Recent
    {
        const int Most = 300;
        public static bool Changed;
        static List<string> names; static Dictionary<string, int> places; static bool unsaved;
        static string FileName { get { return Path.Combine(Path.Combine(BepInEx.Paths.GameRootPath, "TTVREnhanced"), "RecentModels.txt"); } }

        static void Load()
        {
            if (names != null) return;
            names = new List<string>();
            try { if (File.Exists(FileName)) foreach (string line in File.ReadAllLines(FileName)) if (line.Length > 0 && !names.Contains(line)) names.Add(line); }
            catch (Exception e) { Plugin.Log.LogWarning("Recent: " + e.Message); }
        }

        public static void Used(string model)
        {
            Load();
            if (names.Count > 0 && names[0] == model) return;
            names.Remove(model); names.Insert(0, model);
            if (names.Count > Most) names.RemoveAt(names.Count - 1);
            places = null; Changed = true; unsaved = true;
        }

        /// 0 for the model picked up last, 1 for the one before... and int.MaxValue for one not in the list
        public static int Place(string model)
        {
            if (places == null) { Load(); places = new Dictionary<string, int>(); for (int i = 0; i < names.Count; i++) places[names[i]] = i; }
            int place;
            return places.TryGetValue(model, out place) ? place : int.MaxValue;
        }

        public static void Save()
        {
            if (!unsaved || names == null) return;
            try { SafeFile.Write(FileName, names.ToArray()); unsaved = false; }
            catch (Exception e) { Plugin.Log.LogWarning("Recent: " + e.Message); }
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - who made each Workshop model, and when.
    // Steam says so when the mod checks your Workshop items at startup (FastSync.cs); it is kept in
    // "TTVREnhanced/WorkshopItems.txt" in the game's folder so that it is still known when Steam cannot be reached.
    // One line for each item: id|owner|updated|subscribed|author's name
    public static class WorkshopInfo
    {
        public class Item { public ulong Owner; public uint Updated, Subscribed; }
        static readonly Item none = new Item();
        static Dictionary<ulong, Item> items; static readonly Dictionary<ulong, string> authors = new Dictionary<ulong, string>();
        static bool changed;
        static string FileName { get { return Path.Combine(Path.Combine(BepInEx.Paths.GameRootPath, "TTVREnhanced"), "WorkshopItems.txt"); } }

        static void Load()
        {
            if (items != null) return;
            items = new Dictionary<ulong, Item>();
            try
            {
                if (!File.Exists(FileName)) return;
                foreach (string line in File.ReadAllLines(FileName))
                {
                    string[] p = line.Split(new[] { '|' }, 5); ulong id, owner; uint updated, subscribed;
                    if (p.Length < 5 || !ulong.TryParse(p[0], out id) || !ulong.TryParse(p[1], out owner) || !uint.TryParse(p[2], out updated) || !uint.TryParse(p[3], out subscribed)) continue;
                    items[id] = new Item { Owner = owner, Updated = updated, Subscribed = subscribed };
                    if (owner != 0 && p[4].Length > 0) authors[owner] = p[4];
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorkshopInfo: " + e.Message); }
        }

        static Item Get(ulong id) { Load(); Item item; if (!items.TryGetValue(id, out item)) items[id] = item = new Item(); return item; }

        /// Steam's answer about one item
        public static void Details(ulong id, ulong owner, uint updated)
        {
            Item item = Get(id);
            if (item.Owner != owner || item.Updated != updated) { item.Owner = owner; item.Updated = updated; changed = true; }
            // (a name is asked for now, so that Steam has it by the time it is wanted)
            try { if (owner != 0 && !authors.ContainsKey(owner) && SteamReady()) SteamFriends.RequestUserInformation(new CSteamID(owner), true); } catch (Exception) { }
        }
        public static void SubscribedAt(ulong id, uint when) { Item item = Get(id); if (item.Subscribed != when) { item.Subscribed = when; changed = true; } }

        /// what is known of a Workshop model, by the name the game gives it (its id)
        public static Item Of(string name)
        {
            Load(); ulong id; Item item;
            return ulong.TryParse(name, out id) && items.TryGetValue(id, out item) ? item : none;
        }

        static bool SteamReady() { try { return SteamManager.Initialized; } catch (Exception) { return false; } }

        /// a person's name: Steam's if it can say, else the one kept from before
        // Steam is asked once for each person in a frame, and not at all when it is not running: sorting and searching
        // ask for the same names thousands of times over (and a name changing half-way would upset a sort).
        static int askedFrame = -1; static bool steamReady; static readonly Dictionary<ulong, string> asked = new Dictionary<ulong, string>();
        public static string Author(ulong owner)
        {
            if (owner == 0) return "Unknown";
            Load();
            if (askedFrame != Time.frameCount) { askedFrame = Time.frameCount; asked.Clear(); steamReady = SteamReady(); }
            string name;
            if (asked.TryGetValue(owner, out name)) return name;
            if (steamReady)
                try
                {
                    name = SteamFriends.GetFriendPersonaName(new CSteamID(owner));
                    if (!string.IsNullOrEmpty(name) && name != "[unknown]")
                    {
                        // (made fit for one line of the file first, then compared: otherwise a name with such a
                        // character in it would never match the one kept, and the file be written again every time)
                        name = name.Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' ');
                        string had;
                        if (!authors.TryGetValue(owner, out had) || had != name) { authors[owner] = name; changed = true; }
                    }
                }
                catch (Exception) { }
            if (!authors.TryGetValue(owner, out name)) name = "Unknown";
            asked[owner] = name;
            return name;
        }

        /// write what is known, if anything is new (FastSync.cs, when it has finished)
        public static void Save()
        {
            if (items == null) return;
            try
            {
                foreach (Item item in items.Values) Author(item.Owner);         // (names Steam has by now)
                if (!changed) return;
                var lines = new List<string>();
                foreach (var kv in items)
                {
                    string name; authors.TryGetValue(kv.Value.Owner, out name);
                    lines.Add(kv.Key + "|" + kv.Value.Owner + "|" + kv.Value.Updated + "|" + kv.Value.Subscribed + "|" + (name ?? ""));
                }
                SafeFile.Write(FileName, lines.ToArray());
                changed = false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("WorkshopInfo: " + e.Message); }
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Tiny Town Enhanced - a text file written whole: first beside itself under another name, then moved into place,
    // so that a crash or a power cut while writing cannot leave half a file where the good one was.
    static class SafeFile
    {
        public static void Write(string file, string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string fresh = file + ".new";
            File.WriteAllLines(fresh, lines);
            if (File.Exists(file)) File.Delete(file);      // (the game's .NET cannot move a file onto another)
            File.Move(fresh, file);
        }
    }
}
