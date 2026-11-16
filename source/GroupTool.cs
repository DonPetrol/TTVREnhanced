// Tiny Town Enhanced - Group Tool: select several objects and move, copy, delete or edit them as one.
// (In several files: this one has the menu strip, the brush and the every-frame work; GroupTool.Groups.cs, .Carry.cs,
//  .Copy.cs, .Ground.cs and .Undo.cs have the parts their names say.)
//
// The World tab's Placement page has two buttons under the snapping controls: "Group Tool" and "Clear Selection".
//   Group Tool: the free hand (the one pressing it, not the one wearing the menu) becomes a selection brush; press it
//     again to give the hand back. While it is a brush, that hand does not pick things up, copy, delete or move the world
//     (the other hand still does: it can grab and move the world on its own while the brush is in use). Both triggers
//     held turn and scale the world as usual (and so does the stick, with Stick Scale): while that is going on the
//     brush selects nothing and is shown at the dot's own size, until its trigger is let go.
//     The hand's red dot becomes a see-through red ball, the brush:
//       - hold the trigger and sweep it through objects: everything it touches is selected (and outlined);
//       - hold the trigger and push that hand's stick up or down: the ball grows or shrinks (from 3 times the
//         dot to start with; between 1 and 100 times). It grows away from the hand
//         (its near side stays at the dot), so it never swallows the hand; nothing is selected while it is resized;
//       - hold the delete button and sweep: what it touches is taken out of the selection.
//       - while a wrist menu is open the ball is shown at the dot's own size, so the menu can be used without
//         shrinking it first; it goes back to its size when the menu closes;
//       - while the build menu is open the brush is put away, and it comes back (on the same hand, the same size) when
//         the build menu closes, so the build menu can be used as usual;
//       - both of those wait until the menu has been open for half a second, so a menu opened by accident changes nothing.
//   Clear Selection: nothing selected any more.
// With the brush put away, the selection works with the ordinary hands:
//   - pick up any selected object and the rest come with it, keeping their places around it (snapping, scaling and
//     all: while carried they are one object, so the other hand can take hold of any of them to scale the lot;
//     however many there are, a frame moves only one thing - see Carried); put it down and they are put down too. One undo puts all of them back. (Thrown away: the rest go back
//     where they were.)
//   - delete any one of a group and the rest go with it; one undo brings them all back. A carried group cannot be
//     thrown away (no flick counts as a throw while one is carried).
//   - put a carried selection down and Object Copying copies all of it: the ghosts are spaced by the whole group, and
//     touching one copies the group there (in one undo). Characters are left out of such copies.
//   - the Edit tab changes every selected object at once when the model it shows is one of them.
//   - Edit tab: selecting shows the selected objects there straight away (no need to pick one up), and its first row,
//     Create Group, makes them a group that stays together: pick up, delete or brush any one of it and the rest come
//     along. The row says Break Group for a group, and undoes it. Groups are saved with the world.
// The hands' dots are blue, and red while the delete button is held; the brush ball is the same.
// The selection itself is only kept while the world is open: it is not saved, and loading or leaving a world empties it.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public partial class GroupTool : IModule
    {
        public string Name { get { return "GroupTool"; } }

        // ---- the game's own parts
        static readonly Type P = typeof(PlayState);
        static readonly FieldInfo fHands = AccessTools.Field(P, "hands"), fGraph = AccessTools.Field(P, "graph"), fGround = AccessTools.Field(P, "groundManager"),
            fUndo = AccessTools.Field(P, "undoStack"), fMask = AccessTools.Field(P, "selectionMask"), fAttached = AccessTools.Field(P, "attachedLayer"),
            fAttachedGround = AccessTools.Field(P, "attachedGroundLayer"), fSpeechLayer = AccessTools.Field(P, "speechBubbleLayer"), fSpeech = AccessTools.Field(P, "speechBubbleManager"),
            fCreated = AccessTools.Field(typeof(CreateUndoOperation), "createdGO"),
            fRoot = AccessTools.Field(typeof(SceneGraph), "rootTransform"),
            fMoved = AccessTools.Field(typeof(TransformUndoOperation), "gameObject"), fDeleted = AccessTools.Field(typeof(DestroyUndoOperation), "gameObject");
        static PlayState play;
        // (the game's own parts are read once for each world, not by reflection every frame: the hands are one array made
        //  when the game starts, and the scene graph and ground manager do not change while a world is open)
        static PlayState boundTo; static PlayState.HandData[] hands; static SceneGraph graph; static GroundManager ground;
        static int selectionMask, attachedLayer, attachedGroundLayer;
        static void Bind()
        {
            if (boundTo == play) return;
            boundTo = play; hands = null; graph = null; ground = null; dotParts.Clear();
            if (play == null) return;
            hands = (PlayState.HandData[])fHands.GetValue(play); graph = (SceneGraph)fGraph.GetValue(play); ground = (GroundManager)fGround.GetValue(play);
            selectionMask = (int)fMask.GetValue(play); attachedLayer = (int)fAttached.GetValue(play); attachedGroundLayer = (int)fAttachedGround.GetValue(play);
        }
        static PlayState.HandData[] Hands { get { Bind(); return hands; } }
        static SceneGraph Graph { get { Bind(); return graph; } }
        static GroundManager Ground { get { Bind(); return ground; } }

        // ---- what is selected, and how it is shown
        static readonly List<GameObject> selected = new List<GameObject>();
        // (the same objects again as a set, for "is this one selected?", which is asked for everything the brush touches)
        static readonly HashSet<GameObject> selectedSet = new HashSet<GameObject>();
        static int removals;                    // counts up whenever something leaves the selection (the outlines are then made afresh)
        static bool failed;

        // ---- the brush
        static bool toolOn, toolLeft;           // the brush is on, and on which hand
        static float grow = StartGrow;          // how much larger than the hand's own dot the ball is
        const float StartGrow = 3f, MaxGrow = 100f, StickBand = 0.2f;
        static GameObject ball; static Material ballMaterial;
        static readonly List<Renderer> dotHidden = new List<Renderer>();
        static readonly Collider[] touching = new Collider[512];

        // ---- the two buttons on the World tab's Placement page (WorldTab.cs places them)
        static Text toolLabel;

        public void Start(Plugin plugin)
        {
            pickObjects = ForThisGame.Fresh(plugin.Config.Bind("GroupTool", "SelectObjects", true, "The Group Tool's brush picks up objects."));
            pickGround = ForThisGame.Fresh(plugin.Config.Bind("GroupTool", "SelectGroundTiles", true, "The Group Tool's brush picks up ground tiles."));
            pickCharacters = ForThisGame.Fresh(plugin.Config.Bind("GroupTool", "SelectCharacters", true, "The Group Tool's brush picks up characters."));
            var h = plugin.Harmony; var me = typeof(GroupTool);
            h.Patch(AccessTools.Method(P, "Early"), postfix: new HarmonyMethod(me, "Frame"));
            h.Patch(AccessTools.Method(P, "Enter"), prefix: new HarmonyMethod(me, "WorldOpening"));
            h.Patch(AccessTools.Method(P, "Exit"), prefix: new HarmonyMethod(me, "WorldClosing"));
            // the brush hand does not pick up, delete, copy or move the world
            foreach (var m in new[] { "GetCurrentSelection", "GetCurrentSelectionExpanded", "GetHeldObjectWithLargeRadius" })
                h.Patch(AccessTools.Method(P, m), postfix: new HarmonyMethod(me, "NothingForBrush"));
            h.Patch(AccessTools.Method(P, "OnGripEnabled"), prefix: new HarmonyMethod(me, "NoCopyForBrush"));
            // (the world gestures do not see the brush hand's trigger at all, so the other hand can still grab and move the
            //  world on its own; two-handed turning, scaling and lifting need both triggers, so they do not start)
            foreach (var g in new[] { typeof(ControllerPan), typeof(ControllerPinch), typeof(ControllerRotate), typeof(ControllerLift) })
                h.Patch(AccessTools.Method(g, "UpdateWorldTransform"), prefix: new HarmonyMethod(me, "GestureStarts"), finalizer: new HarmonyMethod(me, "GestureEnds"));
            foreach (var t in typeof(Controllers).Assembly.GetTypes())
                if (t.IsSubclassOf(typeof(Controllers)) && !t.IsAbstract)
                {
                    var trigger = AccessTools.DeclaredMethod(t, "GetTriggerValue");
                    if (trigger != null) h.Patch(trigger, postfix: new HarmonyMethod(me, "BrushTriggerHidden"));
                }
            // the selection carried, deleted and undone together
            h.Patch(AccessTools.Method(P, "PickupObject"), postfix: new HarmonyMethod(me, "PickedUp"));
            h.Patch(AccessTools.Method(P, "SnapObject"), prefix: new HarmonyMethod(me, "LevelSnapped"));
            h.Patch(AccessTools.Method(typeof(GroundManager), "Add"), prefix: new HarmonyMethod(me, "GroundAdding"));
            h.Patch(AccessTools.Method(typeof(GroundManager), "Remove"), prefix: new HarmonyMethod(me, "GroundRemoving"));
            h.Patch(AccessTools.Method(P, "UnsnapObject"), postfix: new HarmonyMethod(me, "LevelFree"));
            h.Patch(AccessTools.Method(P, "OnUndoButtonPressed"), prefix: new HarmonyMethod(me, "UndoWhenFree"));
            h.Patch(AccessTools.Method(P, "UpdateHand"), prefix: new HarmonyMethod(me, "NoThrowStart"), finalizer: new HarmonyMethod(me, "NoThrowEnd"));
            h.Patch(AccessTools.Method(typeof(UndoStack), "Push"), prefix: new HarmonyMethod(me, "Pushing"));
            WorldFile.BeforeSave += WriteGroups;
            // Object Copying, for a group: its ghosts spaced by the whole group, and a touch copies all of it
            var proxies = typeof(PaintingProxies);
            // (the prefix runs after GroundScale's, which may have swapped the bounds already: GhostsForGroup goes by the
            //  model's own bounds, and puts those back)
            h.Patch(AccessTools.Method(proxies, "SetObject"), prefix: new HarmonyMethod(AccessTools.Method(me, "GhostsForGroup")) { priority = Priority.Last },
                    postfix: new HarmonyMethod(AccessTools.Method(me, "GhostsDone")) { priority = Priority.Last });
            h.Patch(AccessTools.Method(proxies, "Reset"), prefix: new HarmonyMethod(me, "CopyingEnds"));
        }
        public void SceneLoaded(string scene) { ClearAll(); ToolOff(); play = null; }
        public void Tick() { }

        static void WorldOpening(PlayState __instance) { play = __instance; ClearAll(); ToolOff(); }
        static void WorldClosing() { DropCarry(false); ClearAll(); ToolOff(); }

        // ================================================================ the buttons
        // The strip under the snapping controls (WorldTab.cs gives its place on the page), in three columns:
        //   left:   Group Tool (blue, green while the brush is on) above Clear Selection
        //   middle: how many objects, ground tiles and characters are selected
        //   right:  Select Objects / Select Ground Tiles / Select Characters - which of them the brush picks up
        static Text objectsCount, groundCount, charactersCount; static GameObject toolButton;
        static BepInEx.Configuration.ConfigEntry<bool> pickObjects, pickGround, pickCharacters;
        static int countedFor = -1;
        public static void AddPanel(WristMenu menu, Transform page, Rect strip)
        {
            float third = strip.width / 3f, mid = strip.center.y, rowStep = strip.height / 3f;
            float left = strip.xMin + third * 0.5f, centre = strip.center.x, right = strip.xMax - third * 0.5f;
            // Group Tool is the larger of the two, with Clear Selection smaller underneath and a clear gap between them;
            // the pair sits in the middle of the strip's height. The words are bold and fill most of each button.
            const float Wide = 360f, ToolTall = 84f, ClearTall = 56f, Gap = 16f;
            float top = mid + (ToolTall + Gap + ClearTall) * 0.5f;
            toolButton = MenuPage.Button(menu, page, "Group Tool", Wide, ToolTall, left, top - ToolTall * 0.5f, TogglePressed, MenuPage.Role.Action);
            var clear = MenuPage.Button(menu, page, "Clear Selection", Wide, ClearTall, left, top - ToolTall - Gap - ClearTall * 0.5f, ClearPressed, MenuPage.Role.Danger);
            toolLabel = toolButton.GetComponentInChildren<Text>(true);
            var clearLabel = clear.GetComponentInChildren<Text>(true);
            foreach (var words in new[] { toolLabel, clearLabel })
            {   // (only a little room round the words, which shrink if they would not fit)
                if (words == null) continue;
                bool big = words == toolLabel;
                var r = words.rectTransform; r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = new Vector2(14f, 6f); r.offsetMax = new Vector2(-14f, -6f);
                words.fontStyle = FontStyle.Bold;
                words.horizontalOverflow = HorizontalWrapMode.Wrap; words.verticalOverflow = VerticalWrapMode.Truncate;
                words.fontSize = big ? 50 : 38;
                words.resizeTextForBestFit = true; words.resizeTextMinSize = 14; words.resizeTextMaxSize = big ? 50 : 38;
            }
            Color grey = MenuPage.LabelColour(menu);
            objectsCount = Count(page, centre, mid + rowStep, grey);
            groundCount = Count(page, centre, mid, grey);
            charactersCount = Count(page, centre, mid - rowStep, grey);
            float boxX = right - third * 0.42f;
            MenuPage.Checkbox(menu, page, "Select Objects", boxX, mid + rowStep, third * 0.72f, () => pickObjects.Value, v => pickObjects.Value = v, 0.6f, 28);
            MenuPage.Checkbox(menu, page, "Select Ground Tiles", boxX, mid, third * 0.72f, () => pickGround.Value, v => pickGround.Value = v, 0.6f, 28);
            MenuPage.Checkbox(menu, page, "Select Characters", boxX, mid - rowStep, third * 0.72f, () => pickCharacters.Value, v => pickCharacters.Value = v, 0.6f, 28);
            countedFor = -1; shownOn = !toolOn;
            Words();
        }
        static Text Count(Transform page, float x, float y, Color colour)
        {
            var t = MenuPage.Label(page, 360f, 48f, x, y, 30);
            t.color = colour;
            t.resizeTextForBestFit = true; t.resizeTextMinSize = 14; t.resizeTextMaxSize = 30;
            return t;
        }

        static bool shownOn;
        static void Words()
        {
            // (this runs every frame: the button is only written to when the brush has been switched on or off)
            if (toolButton != null && shownOn != toolOn)
            {
                shownOn = toolOn; MenuPage.Tone(toolButton, toolOn ? MenuPage.Green : MenuPage.Blue);
                if (toolLabel != null) toolLabel.text = toolOn ? "Group Tool: On" : "Group Tool";
            }
            // the counts: worked out again only when the selection has changed (and the page is showing)
            if (objectsCount == null || !objectsCount.gameObject.activeInHierarchy) return;
            int stamp = selected.Count * 7919 + changes;
            if (stamp == countedFor) return;
            countedFor = stamp;
            int objects = 0, ground = 0, characters = 0;
            foreach (var go in selected)
            {
                var md = Game.Model(go);
                if (md == null) continue;
                if (md.type == MeshDataType.Ground) ground++; else if (md.type == MeshDataType.Character) characters++; else objects++;
            }
            objectsCount.text = objects + (objects == 1 ? " Object" : " Objects");
            groundCount.text = ground + (ground == 1 ? " Ground Tile" : " Ground Tiles");
            charactersCount.text = characters + (characters == 1 ? " Character" : " Characters");
        }
        static int changes;                         // (counts up whenever the selection changes, for Words)

        /// does the brush pick up this kind of model (the three checkboxes)
        static bool Picks(GameObject node)
        {
            var md = Game.Model(node);
            if (md == null) return pickObjects.Value;
            return md.type == MeshDataType.Ground ? pickGround.Value : md.type == MeshDataType.Character ? pickCharacters.Value : pickObjects.Value;
        }

        /// "Group Tool" pressed: by the hand that is not wearing the wrist menu
        static void TogglePressed()
        {
            var hands = Hands;
            if (hands == null) return;
            bool left = false;                                      // (the right hand, if neither has the menu open)
            foreach (var d in hands) if (d != null && d.hand != null && d.hand.IsMenuFullyOpen()) left = !d.hand.leftHanded;
            if (toolOn && toolLeft == left) ToolOff();
            else { ToolOff(); toolOn = true; toolLeft = left; grow = StartGrow; }
            pausedForBuildMenu = false;
            Words();
        }
        static void ClearPressed() { ClearAll(); Words(); }

        /// StickMove.cs: this hand's stick is the brush's while its trigger is held
        public static bool OwnsStick(int hand)
        {
            if (!toolOn || (hand == 0) != toolLeft || Gripping()) return false;      // (both triggers: the stick is Stick Scale's)
            var d = ToolHand();
            return d != null && d.TriggerValue > 0.5f;
        }

        /// The objects that go with `node`, itself included: the selection, when it is one of it; otherwise the group
        /// made with the Group Tool that it is one of; otherwise none
        static List<GameObject> Members(GameObject node)
        {
            var list = new List<GameObject>();
            if (node == null) return list;
            if (selectedSet.Contains(node)) { foreach (var go in selected) if (go != null && go.activeInHierarchy) list.Add(go); }
            else
            {
                int id;
                if (groupOf.TryGetValue(node, out id)) foreach (var m in InGroup(id)) if (m != null && m.activeInHierarchy) list.Add(m);
            }
            return list;
        }

        /// EditTab.cs (and deleting, carrying): the others that go with `node` (see Members)
        public static List<GameObject> OthersWith(GameObject node)
        {
            var list = Members(node);
            list.Remove(node);
            return list;
        }

        // ================================================================ the brush hand keeps out of the game's way
        static void NothingForBrush(PlayState.HandData data, ref Collider __result)
        {
            if (data == null || data.hand == null) return;
            if (IsBrush(data.hand)) { __result = null; return; }
            if (carrying.Count > 0 && held != null)
            {
                // while a group is carried it is one object: touching any of it is touching the held one (so the other
                // hand can scale it from anywhere). Its colliders are left behind while it is carried, so it is found by
                // where its parts are drawn, and the colliders left behind count for nothing.
                if (__result != null)
                    for (Transform t = __result.transform; t != null; t = t.parent)
                        if (carrying.ContainsKey(t.gameObject)) { __result = null; break; }
                if (__result == null && data != carrier && data.selectionTransform != null && OnCarried(data.selectionTransform.position))
                {
                    if (heldColliderOf != held) { heldColliderOf = held; heldCollider = held.GetComponent<Collider>(); }
                    __result = heldCollider;
                }
                return;
            }
        }

        static GameObject heldColliderOf; static Collider heldCollider;     // (the held object's collider, looked for once for each object held)
        // a carried group cannot be thrown away: while one is carried, no throw is fast enough
        static readonly FieldInfo fThrow = AccessTools.Field(P, "deleteVelocityThreshold");
        static float throwSpeed = -1f;
        static void NoThrowStart(PlayState __instance)
        {
            if (carrying.Count == 0 || throwSpeed >= 0f) return;
            throwSpeed = (float)fThrow.GetValue(__instance);
            fThrow.SetValue(__instance, float.MaxValue);
        }
        static void NoThrowEnd(PlayState __instance)
        {
            if (throwSpeed < 0f) return;
            fThrow.SetValue(__instance, throwSpeed);
            throwSpeed = -1f;
        }

        static bool NoCopyForBrush(int index)
        {
            var hands = Hands;
            return !(hands != null && index < hands.Length && IsBrush(hands[index].hand));
        }
        // while the game asks its world gestures whether to move the world, the brush hand's trigger reads as let go
        static bool inGesture;
        static void GestureStarts() { inGesture = true; }
        static Exception GestureEnds(Exception __exception) { inGesture = false; return __exception; }
        static void BrushTriggerHidden(Controllers.Side side, ref float __result)
        {
            // (only while the brush is alone: with the other trigger held too, the world is being turned or scaled)
            if (inGesture && toolOn && (side == Controllers.Side.Left) == toolLeft && !Gripping()) __result = 0f;
        }

        // Both triggers held: the two-handed turn and scale (or Stick Scale) of the world. Once it has started, the brush
        // stands aside - it selects nothing, and is the dot's own size - until its own trigger is let go, so letting go
        // of the other hand first does not start a selection where the brush hand happens to be.
        static bool worldGrip; static int grippingFrame = -1;
        static bool Gripping()
        {
            // (asked many times a frame - the game reads the triggers over and over while it checks its gestures - and
            //  worked out once)
            if (grippingFrame == Time.frameCount) return worldGrip;
            grippingFrame = Time.frameCount;
            var hands = Hands;
            if (!toolOn || hands == null || hands.Length < 2 || play == null) return worldGrip = false;
            PlayState.HandData brush = null, other = null;
            foreach (var d in hands) if (d != null && d.hand != null) { if (IsBrush(d.hand)) brush = d; else other = d; }
            if (brush == null || other == null) return worldGrip = false;
            bool brushHeld = brush.TriggerValue > play.pinchThreshold;
            if (!brushHeld) return worldGrip = false;
            if (other.TriggerValue > play.pinchThreshold && other.state == PlayState.HandState.Idle && brush.state == PlayState.HandState.Idle) worldGrip = true;
            return worldGrip;
        }

        // ================================================================ every frame (after the game's hands)
        // the hands' dots: blue (the colour of the "Enhanced" words on the main menu), red while the delete button is held
        static readonly Color Blue = Colours.Accent;
        static Color Red = new Color(1f, 0.15f, 0.15f);
        static readonly Dictionary<Renderer, Material> dotMaterials = new Dictionary<Renderer, Material>();
        // (each hand's dot parts, found once for each world: looking for them every frame made a new list every frame)
        static readonly Dictionary<Transform, Renderer[]> dotParts = new Dictionary<Transform, Renderer[]>();
        static Renderer[] DotParts(Transform dot)
        {
            Renderer[] parts;
            if (!dotParts.TryGetValue(dot, out parts)) dotParts[dot] = parts = dot.GetComponentsInChildren<Renderer>(true);
            return parts;
        }
        static void Dots()
        {
            var hands = Hands;
            if (hands == null) return;
            foreach (var d in hands)
            {
                if (d == null || d.selectionTransform == null) continue;
                bool deleting = d.state == PlayState.HandState.Deleting;
                foreach (var r in DotParts(d.selectionTransform))
                {
                    if (r == null) continue;
                    Material m;
                    if (!dotMaterials.TryGetValue(r, out m) || m == null)
                    {
                        m = r.material;                                         // (its own copy, so nothing else changes colour)
                        if (m.HasProperty("_Color")) Red = m.color;            // (the game's own red, for deleting)
                        dotMaterials[r] = m;
                    }
                    if (!m.HasProperty("_Color")) continue;
                    // (written only when it is not that colour already)
                    Color now = m.color, want = deleting ? new Color(Red.r, Red.g, Red.b, now.a) : new Color(Blue.r, Blue.g, Blue.b, now.a);
                    if (now != want) m.color = want;
                }
            }
        }

        static void Frame(PlayState __instance)
        {
            if (failed) return;
            play = __instance;
            try
            {
                Dots();
                Prune();
                Carry();
                BuildMenuCheck();
                Brush();
                Outlines();
                Words();
            }
            catch (Exception e)
            {
                failed = true; Plugin.Log.LogWarning("GroupTool: switched off after an error: " + e);
                // (a group being carried is put back where it was: with the tool off, nothing else would ever put it back)
                try { DropCarry(false); } catch (Exception again) { Plugin.Log.LogWarning("GroupTool: the carried group could not be put back: " + again.Message); }
            }
        }

        // ---- the brush: the ball, its size, and sweeping
        /// the hand that is the brush (null when the tool is off)
        static bool IsBrush(Hand hand) { return toolOn && hand != null && hand.leftHanded == toolLeft; }
        static PlayState.HandData ToolHand()
        {
            var hands = Hands;
            if (!toolOn || hands == null) return null;
            foreach (var d in hands) if (d != null && IsBrush(d.hand)) return d;
            return null;
        }

        static void ToolOff()
        {
            toolOn = false; pausedForBuildMenu = false;
            foreach (var r in dotHidden) if (r != null) r.enabled = true;
            dotHidden.Clear();
            if (ball != null) ball.SetActive(false);
            Words();
        }
        static void Brush()
        {
            var d = ToolHand();
            if (d == null || d.selectionTransform == null) { if (ball != null) ball.SetActive(false); return; }
            Transform dot = d.selectionTransform;
            // the hand's own dot is hidden; the ball stands in for it
            if (dotHidden.Count == 0)
                foreach (var r in DotParts(dot)) if (r != null && r.enabled && r.gameObject.activeInHierarchy) { r.enabled = false; dotHidden.Add(r); }
            var sphere = dot.GetComponent<SphereCollider>();
            float r0 = (sphere != null ? sphere.radius : 0.5f) * dot.lossyScale.x;

            // size: trigger held and the stick pushed up or down (not while a wrist menu is open: the ball is then the
            // dot's own size, so the menu can be used, and its own size comes back when the menu closes)
            bool menuOpen = WristMenuOpen(), gripping = Gripping();
            bool trigger = d.TriggerValue > play.pinchThreshold;
            float stick = trigger && !menuOpen && !gripping ? (toolLeft ? Sticks.Left.y : Sticks.Right.y) : 0f;
            bool resizing = Mathf.Abs(stick) > StickBand;
            if (resizing) grow = Mathf.Clamp(grow * Mathf.Exp(stick * 1.6f * Time.deltaTime), 1f, MaxGrow);

            // the ball grows away from the hand: its near side stays where the dot is
            float radius = r0 * (menuOpen || gripping ? 1f : grow);
            Vector3 away = dot.position - d.hand.transform.position;
            away = away.sqrMagnitude > 1e-8f ? away.normalized : dot.forward;
            Vector3 centre = dot.position + away * (radius - r0);
            Ball(centre, radius, d.state == PlayState.HandState.Deleting);

            // sweeping
            bool removing = d.state == PlayState.HandState.Deleting;
            if (!resizing && !gripping && (removing || (trigger && d.state == PlayState.HandState.Idle))) Sweep(centre, radius, !removing);
        }

        // A menu only counts once it has been open this long: one opened by accident and closed again changes nothing.
        const float MenuWait = 0.5f;

        /// `open` has been true for MenuWait (`since`: when it last became true; -1 while it is false)
        static bool OpenLongEnough(bool open, ref float since)
        {
            if (!open) { since = -1f; return false; }
            if (since < 0f) since = Time.time;
            return Time.time - since >= MenuWait;
        }

        // a wrist menu has been open (or opening or closing) on either hand for half a second
        static float wristMenuSince = -1f;
        static bool WristMenuOpen()
        {
            var hands = Hands;
            bool open = false;
            if (hands != null) foreach (var h in hands) if (h != null && h.hand != null && h.hand.IsMenuInUse()) open = true;
            return OpenLongEnough(open, ref wristMenuSince);
        }

        // The build menu has been open for half a second: the brush is put away until it closes, then it comes back on
        // the same hand at the same size. (Pressing Group Tool, or leaving the world, in between forgets it.)
        static float buildMenuSince = -1f;
        static bool pausedForBuildMenu;
        static void BuildMenuCheck()
        {
            var menu = play != null ? play.inventory : null;
            bool open = menu != null && !menu.IsFullyClosed;
            if (!open && pausedForBuildMenu) { pausedForBuildMenu = false; toolOn = true; Words(); }
            if (OpenLongEnough(open && toolOn, ref buildMenuSince)) { buildMenuSince = -1f; ToolOff(); pausedForBuildMenu = true; }
        }

        static void Ball(Vector3 centre, float radius, bool removing)
        {
            if (ball == null)
            {
                ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = "TTE Group Brush";
                UnityEngine.Object.Destroy(ball.GetComponent<Collider>());
                // lit like the world (not glowing), and see-through: the Standard shader in its "fade" way, as Looks.cs
                // makes see-through models
                ballMaterial = new Material(ball.GetComponent<MeshRenderer>().sharedMaterial);
                var standard = Shader.Find("Standard");
                if (standard != null) ballMaterial.shader = standard;
                ballMaterial.shaderKeywords = new string[0];
                ballMaterial.SetFloat("_Glossiness", 0.3f); ballMaterial.SetFloat("_Metallic", 0f);
                ballMaterial.SetFloat("_Mode", 2f);
                ballMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                ballMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                ballMaterial.SetInt("_ZWrite", 1);
                ballMaterial.EnableKeyword("_ALPHABLEND_ON");
                ballMaterial.renderQueue = 2999;           // (like the dot: drawn before the menus and marked in the depth buffer, so a menu
                                                           //  behind it is hidden by it and one in front of it covers it)
                ball.GetComponent<MeshRenderer>().sharedMaterial = ballMaterial;
                ball.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ball.GetComponent<MeshRenderer>().receiveShadows = false;
                UnityEngine.Object.DontDestroyOnLoad(ball);
            }
            ball.SetActive(true);
            ball.transform.position = centre;
            ball.transform.localScale = Vector3.one * radius * 2f;
            ballMaterial.color = removing ? new Color(Red.r, Red.g, Red.b, 0.35f) : new Color(Blue.r, Blue.g, Blue.b, 0.35f);
        }

        // everything the ball touches is selected (or, deleting, let go). The objects are in the world's own space;
        // the ball is where you see it, so it is taken into that space first.
        static void Sweep(Vector3 centre, float radius, bool add)
        {
            var world = Singleton<WorldSettings>.Instance;
            Transform display = world.DisplayWorld;
            Vector3 at = display.InverseTransformPoint(centre);
            float r = radius / Mathf.Max(display.lossyScale.x, 1e-6f);
            Bind();
            int attached = attachedLayer, attachedGround = attachedGroundLayer;
            int n = Physics.OverlapSphereNonAlloc(at, r, touching, selectionMask, QueryTriggerInteraction.Collide);
            bool changed = false; GameObject last = null;
            swept.Clear();
            for (int i = 0; i < n; i++)
            {
                Transform t = touching[i].transform;
                GameObject node = null;
                // (an object's box is a child of the object; ground and its own collider may be the object itself)
                for (Transform s = t; s != null && node == null; s = s.parent)
                    if (s.gameObject.layer == attached || s.gameObject.layer == attachedGround) node = s.gameObject;
                if (node == null || !swept.Add(node)) continue;          // (an object with several colliders is dealt with once)
                // (one of a group made with Create Group brings the rest of its group with it)
                int id; List<GameObject> with = groupOf.TryGetValue(node, out id) ? InGroup(id) : null;
                for (int k = -1; k < (with != null ? with.Count : 0); k++)
                {
                    GameObject each = k < 0 ? node : with[k];
                    if (k >= 0 && (each == null || each == node || !each.activeInHierarchy || !swept.Add(each))) continue;
                    if (add && !Picks(each)) continue;                      // (a kind the brush is told to leave alone)
                    if (add) { if (Select(each)) { changed = true; last = each; } }
                    else if (Deselect(each)) changed = true;
                }
            }
            if (changed) changes++;
            if (changed && add) { Haptics.TriggerHapticPulse(toolLeft); EditTab.Choose(last); }
        }

        static readonly HashSet<GameObject> swept = new HashSet<GameObject>();     // what one sweep has already dealt with
        // the selection is changed in these three places only, so the list and the set always hold the same objects
        static bool Select(GameObject go) { if (go == null || !selectedSet.Add(go)) return false; selected.Add(go); return true; }
        static bool Deselect(GameObject go) { if (!selectedSet.Remove(go)) return false; selected.Remove(go); removals++; return true; }
        public static void ClearAll()
        {
            selected.Clear(); selectedSet.Clear(); removals++; changes++;
            Outlines(true);
        }

        // deleted (or undone out of existence) objects leave the selection
        static void Prune()
        {
            for (int i = selected.Count - 1; i >= 0; i--)
            {
                var go = selected[i];
                if (go == null || (!go.activeInHierarchy && !carrying.ContainsKey(go)))
                {
                    selected.RemoveAt(i); removals++; changes++;
                    // (a destroyed object cannot be looked up in the set any more: the set is made again from the list)
                    selectedSet.Clear(); foreach (var left in selected) if (left != null) selectedSet.Add(left);
                }
            }
        }

        // ---- the outlines: the game's own highlight on every selected object, drawn as a few combined meshes (one per
        // 60,000 points) instead of one object each - a few hundred selected cost a few draws, not hundreds. They are
        // made again only when the selection changes or one of it moves (Transform.hasChanged), and hang from the
        // carried group's stand-in while it is carried.
        static Transform outlines; static readonly List<Mesh> outlineMeshes = new List<Mesh>();
        static int outlinedFor = -1, outlinedCount, outlinedRemovals = -1, outlineExtras;
        // While the brush is only adding objects, the outlines already made are kept and one small mesh is added for the
        // new objects (sweeping 300 objects used to combine the meshes of 1, 2, 3 ... 300 objects in turn). Everything
        // is made afresh when something leaves the selection or moves, or when that many small meshes have been added.
        const int MostExtras = 24;
        static void Outlines(bool now = false)
        {
            if (carrying.Count > 0 || lifting) return;
            // (objects not outlined yet are new, not "moved": a new object's hasChanged is always set)
            bool onlyAdded = !now && outlines != null && removals == outlinedRemovals && selected.Count >= outlinedCount;
            int known = onlyAdded ? outlinedCount : selected.Count;
            bool moved = false;
            for (int i = 0; i < known; i++) { var node = selected[i]; if (node != null && node.transform.hasChanged) { moved = true; break; } }
            if (!now && !moved && outlinedFor == changes) return;
            outlinedFor = changes;
            bool grow = onlyAdded && !moved && selected.Count > outlinedCount && outlineExtras < MostExtras;
            if (!grow)
            {
                foreach (var m in outlineMeshes) if (m != null) UnityEngine.Object.Destroy(m);
                outlineMeshes.Clear();
                if (outlines != null) UnityEngine.Object.Destroy(outlines.gameObject);
                outlines = null; outlinedCount = 0; outlineExtras = 0;
            }
            outlinedRemovals = removals;
            var hands = Hands;
            if (selected.Count == 0 || hands == null || hands.Length == 0) return;
            var look = hands[0].SelectionHighlight;
            var lookRenderer = look != null ? look.GetComponent<MeshRenderer>() : null;
            if (lookRenderer == null) return;
            if (outlines == null)
            {
                outlines = new GameObject("TTE Group Outlines").transform;
                outlines.SetParent(Singleton<WorldSettings>.Instance.DisplayWorld, false);
            }
            else outlineExtras++;
            var parts = new List<CombineInstance>(); int points = 0;
            Action flush = delegate
            {
                if (parts.Count == 0) return;
                var mesh = new Mesh { name = "TTE Group Outline" };
                mesh.CombineMeshes(parts.ToArray(), true, true);
                outlineMeshes.Add(mesh);
                var go = new GameObject("Outline", typeof(MeshFilter), typeof(MeshRenderer));
                go.layer = look.layer;
                go.transform.SetParent(outlines, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = lookRenderer.sharedMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                parts.Clear(); points = 0;
            };
            for (int i = outlinedCount; i < selected.Count; i++)
            {
                var node = selected[i];
                if (node == null) continue;
                node.transform.hasChanged = false;
                if (!node.activeInHierarchy) continue;
                var md = Game.Model(node);
                if (md == null || md.mesh == null) continue;
                if (points + md.mesh.vertexCount > 60000) flush();
                parts.Add(new CombineInstance { mesh = md.mesh, transform = node.transform.localToWorldMatrix });
                points += md.mesh.vertexCount;
            }
            flush();
            outlinedCount = selected.Count;
        }

    }
}
