// Tiny Town Enhanced - entry point. BepInEx loads this DLL when the game starts; no game file is modified.
// Each feature is a small "module" in its own file. To add one: write a class with Start/SceneLoaded/Tick
// (see FastSync.cs) and add it to the Modules list below.
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine.SceneManagement;

namespace TinyTownEnhanced
{
    public interface IModule
    {
        string Name { get; }
        void Start(Plugin plugin);          // once, when the game starts
        void SceneLoaded(string scene);     // every time a scene finishes loading
        void Tick();                        // about twice a second
    }

    [BepInPlugin("donpetrol.tinytownenhanced", "Tiny Town Enhanced", Plugin.Version)]
    [BepInProcess("Tiny Town VR.exe")]      // never runs inside Workshop.exe
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "1.0";      // release 1.0

        // The mod used to be called Tiny Town Plus. Before anything is read, the settings kept under that name are
        // carried over to this one (once: only while there are none under the new name yet).
        static Plugin()
        {
            try
            {
                foreach (string[] pair in new[] { new[] { "donpetrol.tinytown" + "plus.cfg", "donpetrol.tinytownenhanced.cfg" },
                                                  new[] { "TinyTown" + "Plus.untouched.txt", "TinyTownEnhanced.untouched.txt" } })
                {
                    string old = Path.Combine(Paths.ConfigPath, pair[0]), now = Path.Combine(Paths.ConfigPath, pair[1]);
                    if (File.Exists(old) && !File.Exists(now)) File.Copy(old, now);
                }
            }
            catch (Exception) { }
        }

        // ---- the list of features. Remove a line to switch a feature off.
        readonly List<IModule> Modules = new List<IModule>
        {
            new Notices(),      // error messages in the corner of the desktop window
            new FastSync(),     // quicker Workshop check when the game starts
            new FastLoad(),     // quicker loading of Workshop models
            new TextureCache(), // Workshop textures from a cache instead of decoding every PNG
            new MoreItems(),    // more than 256 Workshop models per texture size
            new FastRebuild(),  // no stutter when editing next to heavy models
            new FastWorldLoad(), // worlds open in fewer frames
            new GroundWalls(),  // ground edge walls in worlds with a lot of ground
            new SpawnFix(),     // a world opens with you standing just outside its edge, whatever its size and with or without ground
            new Gestures(),     // zoom, glide and rotate work the same at low frame rates; zoom keeps you in place
            new PanelSize(),    // wrist menu panel a little taller
            new SettingsTabs(), // Settings page split into Gameplay / Controls / Video / Audio / Accessibility
            new SettingsInWorld(), // Settings tab on the wrist menu inside worlds too
            new VideoOptions(), // Settings > Video: anti-aliasing, shadow quality and distance, resolution scale
            new FpsCounter(),   // Settings > Video: FPS counter on the desktop window and the wrist menu
            new WorldFile(),    // settings that belong to a world, saved inside the world's own file
            new WorldTab(),     // "Snapping" tab renamed "World", with Snapping / Presets / Atmosphere / Lighting / Misc inside it
            new WorldOptions(), // the Presets, Atmosphere and Lighting settings (saved with the world)
            new WaterPlane(),   // optional sheet of water under the world (World tab > Misc)
            new CloudField(),   // clouds repeated further out, and moving clouds (World tab > Atmosphere)
            new Stars(),        // stars (World tab > Atmosphere; the Night preset and Time of Day bring them out)
            new Fog(),          // fog, drawn as faint shells around the viewer (World tab > Atmosphere)
            new WallOptions(),  // ground shadows on/off; ground walls on/off and their colour, per world (World tab > Misc)
            new ZoomOption(),   // Settings > Controls: "Head-based Zoom" and "Grab Movement Drift" checkboxes
            new StickMove(),    // Settings > Controls: move, turn, rise and zoom with the thumbsticks
            new CopyUpDown(),   // Object Copying also offers a copy above and below (Settings > Gameplay)
            new Looks(),        // a placed model can have its own smoothness, metallic, tint and glow (drawn and saved here)
            new EditTab(),      // the wrist menu's Speech tab becomes "Edit": the look of the model you picked up, or the speech page
            new ScaleReadout(), // the zoom shown in front of you while you change it
            new Vignette(),     // Settings > Accessibility: the edges of the view darken while you are being moved
            new BuildMenu(),    // the build menu: search, Workshop sorting and authors, arrow buttons
            new Welcome(),      // the main menu's "Welcome!" names the mod and its version
            new BuildMenuFix(), // the build menu can no longer be left open when quitting a world (it blocked the main menu)
            new Saves(),        // autosaves in a file of their own, apart from the manual save; autosave settings and countdown
            new Unsaved(),      // keeps track of whether the open world has changes that are not saved (Quit asks before discarding them)
            new StartView(),    // a world opens where you were standing, and at the scale, when it was saved (Settings > Gameplay)
            new WorldSelect(),  // shared by the World Select pages below: finding the game's page, making our own, reading the list of worlds
            new WorldPage(),    // World Select: a page per world to open its save, autosave or a backup, or delete it
            new WorldGrid(),    // World Select: three rows of worlds instead of two
            new WorldNames(),   // worlds can be named; the name shows on the thumbnails
            new WorldSort(),    // World Select: a button for the order of the worlds (last played, name, date created)
            new CreateWorld(),  // World Select: "+" > Blank, Template (the game's ready-made worlds) or Workshop
            new MissingItems(), // opening a world that uses Workshop models you are not subscribed to asks first
            new MenuSwipe(),    // build menu swipes work the same at low frame rates
            new QuitMenu(),     // Quit tab: "Save and Quit" / "Quit without Saving" instead of a checkbox
            new LoadingText(),  // the loading message shown inside the headset too
            new PhysicsPace(),  // physics step matched to the headset's refresh rate
        };

        public static ManualLogSource Log;
        /// for modules that change how a game method behaves (Harmony patches); all removed when the mod is uninstalled
        public readonly HarmonyLib.Harmony Harmony = new HarmonyLib.Harmony("donpetrol.tinytownenhanced");
        /// folder this DLL is in (BepInEx\plugins\TinyTownEnhanced): a module's data files go here
        public string Folder { get { return Path.GetDirectoryName(Info.Location); } }

        /// true once the Workshop items are loaded and the game moves on to the menu
        public static bool LoadingDone;
        static void LoadingFinished() { LoadingDone = true; }

        float nextTick;

        public static Plugin Instance;      // (for modules that need something that is always there to run a routine over several frames)

        void Awake()
        {
            Log = Logger; Instance = this;
            try { Harmony.Patch(HarmonyLib.AccessTools.Method(typeof(InitState), "CopyWorkshopWorlds"), new HarmonyLib.HarmonyMethod(typeof(Plugin), "LoadingFinished")); }
            catch (Exception e) { Log.LogError("loading hook: " + e); }
            foreach (var m in Modules) Run(m, "Start", delegate { m.Start(this); });
            SceneManager.sceneLoaded += delegate (Scene s, LoadSceneMode mode)
            {
                foreach (var m in Modules) Run(m, "SceneLoaded", delegate { m.SceneLoaded(s.name); });
            };
            Log.LogInfo("Tiny Town Enhanced " + Version + " loaded, " + Modules.Count + " module(s)");
        }

        void Update()
        {
            if (UnityEngine.Time.unscaledTime < nextTick) return;
            nextTick = UnityEngine.Time.unscaledTime + 0.5f;
            foreach (var m in Modules) Run(m, "Tick", m.Tick);
        }

        // one broken module must never take the game or the other modules down
        static void Run(IModule m, string what, Action a)
        {
            try { a(); } catch (Exception e) { Log.LogError(m.Name + " " + what + ": " + e); }
        }
    }
}
