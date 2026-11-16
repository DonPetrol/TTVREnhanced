// Tiny Town Enhanced - the Help tab opens on a list of its topics instead of the first one.
// The list is two buttons to a row, a page at a time (arrows at the bottom): the game's own topics first, then this
// mod's. Touching one shows it: the game's own with its video, as the game shows it (without its own arrows); this
// mod's as a title and a page or more of text, with arrows when there is more than one. Back (bottom left) returns to
// the list.
// A Changelog button in the header (top right) shows what each version of the mod changed, a version at a time, with
// arrows to step between them (it opens on the newest).
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class HelpIndex : IModule
    {
        public string Name { get { return "HelpIndex"; } }

        // ---- this mod's topics: a title, then its pages (the arrows at the bottom step through them)
        class Topic { public string Title; public string[] Pages; public Topic(string title, params string[] pages) { Title = title; Pages = pages; } }
        static readonly Topic[] Topics =
        {
            new Topic("Group Tool",
                "The group tool lets you select multiple objects and then edit them as if they were one. This includes <b>moving, scaling, duplicating, deleting</b> and <b>editing</b> them via the <b>Edit</b> menu.\n\nOpen <b>World > Placement</b> and press <b>Group Tool</b> with your free hand, this will then toggle that free hand into group mode. The selection ball then becomes a selection brush with the following controls;",
                "To select objects hold the <b>Grab</b> button and sweep the brush through them.\n\nTo scale the brush size hold the <b>Grab</b> button and push up/down with the <b>Stick/Touchpad.</b>\n\nTo remove objects from the selection hold the <b>Delete</b> button while group mode is toggled on.\n\nPress <b>Clear Selection</b> in the <b>Placement</b> menu to deselect everything.",
                "If you want to create a permanent group out of your current selection go into the <b>Edit</b> menu and press <b>Create Group</b> to make your current selection stay together, if you want to break this group up again you can press <b>Break Group</b> in the same menu on a created group. Created groups are saved to the world file."),
            new Topic("Edit Tab",
                "The edit tab lets you change how a model looks. Pick up a model, or select some with the <b>Group Tool</b>, then open the <b>Edit</b> menu.\n\nFrom here you can change a model's <b>Smoothness</b> and <b>Metallic</b> values, give it a <b>Tint</b> colour, make it <b>Glow</b>, make it see-through with <b>Transparency</b>, make it <b>Unlit</b> so it is always fully bright, and choose whether it <b>Casts Shadows</b>.",
                "Press <b>Reset</b> to give the model its usual look back. To copy a look onto another model press <b>Copy</b>, pick up the other model and press <b>Paste</b>.\n\nSettings marked <b>Costs FPS</b> can lower your frame rate when used on a lot of models. All looks are saved to the world file."),
            new Topic("World Tab",
                "The world tab lets you change various properties of how your world looks or functions.\n\n<b>Presets</b> gives you ready-made looks such as <b>Sunrise</b> or <b>Night</b> in one press. <b>Atmosphere</b> controls the sky, clouds and fog. <b>Lighting</b> controls the sun, its shadows and the fill light. <b>Misc</b> has the remaining world options.\n\nAll of these are saved to the world file. Press <b>Reset</b> in the top right to put them back to the game's defaults.",
                "<b>Placement</b> holds the game's snapping options and the <b>Group Tool</b>. Its second page has <b>World Scale</b> options to change how big the world is shown.\n\nPress <b>Save</b> in the top right to save your world without leaving it."),
            new Topic("Stick Controls",
                "Stick controls let you move around your town with the thumbsticks. Turn them on with <b>Stick Movement</b> and <b>Stick Turning</b> in <b>Settings > Controls</b>.\n\nThe <b>Left Stick</b> moves you around; click it in while moving to go faster. Push the <b>Right Stick</b> left or right to turn, or up and down to move up and down.\n\nTo zoom using only the sticks hold both <b>Grab</b> buttons and push the <b>Right Stick</b> up or down.\n\n<b>Swap Sticks</b> inverts what the two sticks do to the other controller."),
        };

        // ---- the changelog: one page per version, oldest first
        // Each version a list: "- Added" for new features and settings, "- Changed" (or Moved, Renamed...) and "- Fixed" for what players of the
        // game or of an earlier version would notice (not what was put right within the same version). A line is about
        // 75 letters across the page; a longer entry is broken by hand and goes on without a "-". About 10 lines to a page.
        static readonly Topic[] Changes =
        {
            new Topic("Version 1.0", "- Initial release."),
            new Topic("Version 1.1",
                "- Added the Group Tool; select objects with a brush to move, copy, delete or\n" +
                "edit together, and make groups that are saved with the world.\n" +
                "- Added Ignore Copy Collision to World > Placement.\n" +
                "- Added Scale Ground; ground pieces can be resized.\n" +
                "- Added a Help list with pages for this mod, and this changelog.\n" +
                "- Added turning in the main menu with a stick or both triggers.\n" +
                "- Added Fix Oculus Controls for Quest and Rift controllers through SteamVR.\n" +
                "- Added Blue Header to Settings > Video.",
                "- Added arrows to the ends of sliders for fine adjusting.\n" +
                "- Added support for Workshop items that come with their own look.\n" +
                "- Moved Head-based Scale and Copy Up and Down to World > Placement.\n" +
                "- Changed Placement settings to reset each time the game starts.\n" +
                "- Changed the main menu to sunrise.\n" +
                "- Changed the hand dots to blue, and red while deleting.\n" +
                "- Changed the camera screen so it is always bright.",
                "- Moved the build menu arrows to your hand's side.\n" +
                "- Changed the default autosave time to 5 minutes.\n" +
                "- Moved the loading text higher.\n" +
                "- Fixed copy ghosts staying round undone objects.\n" +
                "- Fixed copy ghosts staying where a moved object was after undoing the move.\n" +
                "- Fixed posed characters' heads turning after loading."),
        };

        enum View { List, GameTopic, OwnTopic, Changelog }
        static View view;
        static WristHelpPage built; static WristMenu menu;
        static Transform gameHolder, listHolder, textHolder;
        static readonly List<GameObject> listButtons = new List<GameObject>();
        static MenuPage.Pager listPager, readPager;
        static string[] readTitles = new string[0], readPages = new string[0];      // what the text view is showing, page by page
        static GameObject back, changelog;
        static Text topicTitle, topicText;
        static bool ours;                       // the game's own Activate is being run by this module (for a topic)
        static string[] gameTitles = new string[0];
        const int PerPage = 10;

        static readonly Type H = typeof(WristHelpPage);
        static readonly FieldInfo fIndex = AccessTools.Field(H, "index"), fPrev = AccessTools.Field(H, "prevIndex"), fTitles = AccessTools.Field(H, "titles"),
                                  fSound = AccessTools.Field(H, "soundManager");

        public void Start(Plugin plugin)
        {
            var h = plugin.Harmony; var me = typeof(HelpIndex);
            h.Patch(AccessTools.Method(H, "Activate"), postfix: new HarmonyMethod(me, "Opened"));
            h.Patch(AccessTools.Method(H, "Deactivate"), postfix: new HarmonyMethod(me, "Closed"));
            h.Patch(AccessTools.Method(H, "OnOpen"), prefix: new HarmonyMethod(me, "VideoWanted"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        static void Click(WristHelpPage page)
        {
            var sound = fSound.GetValue(page) as SoundManager;
            if (sound != null) sound.Play(SoundManager.SoundEffect.BUTTON_CLICK_POSITIVE);
        }

        // the tab was opened (or this module showed a topic of the game's)
        static void Opened(WristHelpPage __instance)
        {
            if (ours) return;
            try
            {
                if (built != __instance) Prepare(__instance);
                if (changelog != null) changelog.SetActive(true);
                Show(View.List);
            }
            catch (Exception e) { Plugin.Log.LogWarning("HelpIndex: " + e); }
        }
        // the wrist menu opened on this tab: the game's video only plays while one of its topics is showing
        static bool VideoWanted(WristHelpPage __instance) { return ours || built != __instance || view == View.GameTopic; }

        static void Closed()
        {
            if (changelog != null) changelog.SetActive(false);
        }

        static void Prepare(WristHelpPage page)
        {
            Transform t = page.transform;
            menu = MenuPage.MenuOf(page);
            if (menu == null) throw new Exception("wrist menu not found");
            gameTitles = (fTitles.GetValue(page) as string[]) ?? new string[0];

            // everything the game put on the page goes into one holder, shown for its own topics
            gameHolder = MenuPage.Gather(t, "Help (game)");

            // the list: two buttons to a row, five rows, and the page arrows under them
            listHolder = MenuPage.Holder(t, "Help list");
            listButtons.Clear();
            const float Wide = 600f, Tall = 92f, Across = 320f, Top = 250f, Step = 112f;
            for (int i = 0; i < PerPage; i++)
            {
                int place = i;
                var b = MenuPage.Button(menu, listHolder, "", Wide, Tall, (i % 2 == 0 ? -Across : Across), Top - (i / 2) * Step, delegate { Click(page); Pick(page, listPager.First + place); }, MenuPage.Role.Action);
                listButtons.Add(b);
            }
            listPager = new MenuPage.Pager(menu, listHolder, Top - 5 * Step - 20f, PerPage, () => gameTitles.Length + Topics.Length, FillList, delegate { Click(page); });

            // a topic of this mod's, and the changelog: a title, and a page of text under it
            textHolder = MenuPage.Holder(t, "Help text");
            topicTitle = MenuPage.Label(textHolder, 1200f, 90f, 0f, 285f, 56);
            topicTitle.fontStyle = FontStyle.Bold;
            topicText = MenuPage.Label(textHolder, 1150f, 500f, 0f, -10f, 36);
            topicText.alignment = TextAnchor.UpperLeft; topicText.lineSpacing = 1.1f;
            topicText.horizontalOverflow = HorizontalWrapMode.Wrap; topicText.verticalOverflow = VerticalWrapMode.Truncate;
            topicText.resizeTextForBestFit = true; topicText.resizeTextMinSize = 20; topicText.resizeTextMaxSize = 36;
            readPager = new MenuPage.Pager(menu, textHolder, Top - 5 * Step - 20f, 1, () => readPages.Length, FillText, delegate { Click(page); });

            // the game's own arrows (from one of its topics to the next) are not shown: the list does that now. They are
            // still there, hidden, as the pattern for every page arrow (MenuPage.PageArrow).
            foreach (var arrow in gameHolder.GetComponentsInChildren<PushButton>(true))
                if (arrow.name == "Prev" || arrow.name == "Next") arrow.gameObject.SetActive(false);

            // Back, bottom left: to the list (on the page itself, so it shows over the game's topics as well)
            back = MenuPage.Chevron(MenuPage.Button(menu, t, "Back", 220f, 64f, -560f, Top - 5 * Step - 20f, delegate { Click(page); Show(View.List); }, MenuPage.Role.Quiet));

            // Changelog, in the header's right-hand corner
            // (as wide as there is room for beside the frame-rate counter)
            changelog = MenuPage.HeaderButton(menu, "TinyTownEnhanced Changelog", "Changelog", MenuPage.Green, 150f, delegate
            {
                Click(page);
                if (view == View.Changelog) { Show(View.List); return; }
                var pages = new List<string>(); var pageTitles = new List<string>();
                foreach (var c in Changes) foreach (var p in c.Pages) { pageTitles.Add(c.Title); pages.Add(p); }
                Read(View.Changelog, pageTitles.ToArray(), pages.ToArray(), pages.Count - Changes[Changes.Length - 1].Pages.Length);      // (opens on the newest version)
            });
            built = page;
        }

        static void Show(View to)
        {
            view = to;
            gameHolder.gameObject.SetActive(to == View.GameTopic);
            listHolder.gameObject.SetActive(to == View.List);
            textHolder.gameObject.SetActive(to == View.OwnTopic || to == View.Changelog);
            back.SetActive(to != View.List);
            if (to != View.GameTopic && built != null) built.OnClose();          // (the game's video stops while it is not shown)
            if (to == View.List) listPager.Refresh();
            menu.title.text = to == View.Changelog ? "Changelog" : "Help";
        }

        // the text view: pages of text, each with its title, from page `first`
        static void Read(View kind, string[] titles, string[] pages, int first)
        {
            readTitles = titles; readPages = pages;
            Show(kind);
            readPager.First = Mathf.Clamp(first, 0, pages.Length - 1);
            readPager.Refresh();
        }
        static void FillText()
        {
            if (readPages.Length == 0) return;
            int n = Mathf.Clamp(readPager.First, 0, readPages.Length - 1);
            topicTitle.text = readTitles[n];
            topicText.text = readPages[n];
        }

        static void FillList()
        {
            int total = gameTitles.Length + Topics.Length;
            for (int i = 0; i < listButtons.Count; i++)
            {
                int n = listPager.First + i;
                listButtons[i].SetActive(n < total);
                if (n >= total) continue;
                var text = listButtons[i].GetComponentInChildren<Text>(true);
                if (text != null) text.text = n < gameTitles.Length ? gameTitles[n] : Topics[n - gameTitles.Length].Title;
                // (the game's own topics blue, this mod's green)
                MenuPage.Paint(listButtons[i], n < gameTitles.Length ? MenuPage.Role.Action : MenuPage.Role.Go);
            }
        }

        // a topic touched in the list
        static void Pick(WristHelpPage page, int n)
        {
            if (n < gameTitles.Length)
            {
                // the game's own: its page, as the game shows it (Activate puts its words and video in place)
                fPrev.SetValue(page, fIndex.GetValue(page));
                fIndex.SetValue(page, n);
                Show(View.GameTopic);
                ours = true;
                try { page.Activate(); page.OnOpen(); } finally { ours = false; }
            }
            else
            {
                var topic = Topics[n - gameTitles.Length];
                var titles = new string[topic.Pages.Length];
                for (int i = 0; i < titles.Length; i++) titles[i] = topic.Title;
                Read(View.OwnTopic, titles, topic.Pages, 0);
            }
        }
    }
}
