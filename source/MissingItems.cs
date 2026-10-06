// Tiny Town Enhanced - a world that uses Workshop models you are not subscribed to says so before it opens.
// The game simply leaves such models out, and the next Save and Quit then saves the world without them. A world's
// file starts with the list of models it uses, and a Workshop model is listed by its Steam number, so the ones this
// game has not got can be counted before anything is loaded. If there are any, a page asks:
//   Subscribe            subscribes to them on Steam, downloads them and adds them to the game there and then
//                        (FastSync.cs downloads; MoreItems.cs adds), and opens the world
//   Open Without Them    opens the world as the game always did
//   Back
// A world saved without them no longer lists them, so it is not asked about again.
// Without Steam (not running, or offline) the page says so and offers only Open Without Them and Back; the same if
// Steam stops answering part-way.
using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class MissingItems : IModule
    {
        public string Name { get { return "MissingItems"; } }
        static GameObject page, subscribe, openAnyway; static Text words;
        static List<ulong> missing; static Action open, back; static string asked;
        static readonly List<IDisposable> calls = new List<IDisposable>();

        public void Start(Plugin plugin) { }
        // (the page went with the old scene: anything still waiting on Steam for it is to stop, not write on a page that is gone)
        public void SceneLoaded(string scene) { page = null; round++; }
        public void Tick() { }

        /// the Workshop models the world in this file uses that the game has not got
        public static List<ulong> Missing(string file)
        {
            var list = new List<ulong>();
            try
            {
                var models = MeshDataManager.GetInstance(); int objects;
                foreach (string name in WorldCopies.Models(file, out objects))
                {
                    ulong id;
                    if (ulong.TryParse(name, out id) && id > 0 && models.GetMeshData(name) == null) list.Add(id);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("MissingItems: " + e.Message); list.Clear(); }
            return list;
        }

        /// If the world in `file` has models missing, ask (in place of World Select's other pages) and give back true:
        /// `openIt` then runs if the answer is to open it anyway, `goBack` if not. False: nothing missing, nothing shown.
        public static bool Ask(string file, Action openIt, Action goBack)
        {
            var list = Missing(file);
            if (list.Count == 0 || !WorldSelect.Find()) return false;
            if (page == null) Build();
            missing = list; open = openIt; back = goBack; asked = file;
            bool online = Online();
            words.text = "This world uses " + Count(list.Count) + " that you are not subscribed to.\n\n"
                         + (online ? "Subscribe to " + (list.Count == 1 ? "it" : "them") + " on Steam?" : NoSteam);
            subscribe.SetActive(online); openAnyway.SetActive(true);
            WorldSelect.Selection.SetActive(false);
            page.SetActive(true);
            return true;
        }
        const string NoSteam = "Can't reach Steam to get them.\nYou can still open the world without them.";
        const float Patience = 12f;                 // seconds without an answer from Steam before giving up
        static int round;                           // (answers from an attempt that was given up on are ignored)

        static bool Online()
        {
            try { return SteamManager.Initialized && SteamUser.BLoggedOn(); }
            catch (Exception) { return false; }
        }

        static void GiveUp(string why)
        {
            round++;
            FastSync.Progress = null;
            Plugin.Log.LogWarning("MissingItems: gave up: " + why);
            if (words != null) words.text = NoSteam;
            if (openAnyway != null) openAnyway.SetActive(true);
        }

        static System.Collections.IEnumerator Watch(int mine, Func<bool> finished)
        {
            float until = Time.realtimeSinceStartup + Patience;
            while (Time.realtimeSinceStartup < until) { if (mine != round || finished()) yield break; yield return null; }
            if (mine == round && !finished() && page != null && page.activeSelf) GiveUp("no answer from Steam in " + Patience + " s");
        }

        static string Count(int n) { return n + " Workshop item" + (n == 1 ? "" : "s"); }

        static void Leave(Action then)
        {
            page.SetActive(false);
            round++;
            foreach (var c in calls) c.Dispose();
            calls.Clear();
            if (then != null) then();
        }

        // ask Steam to subscribe to each; the answers come back one by one, and then the items are fetched and added
        static void Subscribe()
        {
            subscribe.SetActive(false); openAnyway.SetActive(false);
            int total = missing.Count, answered = 0, unreachable = 0, mine = ++round;
            var subscribed = new List<ulong>();
            if (!Online()) { GiveUp("Steam is not running or is offline"); return; }
            words.text = "Subscribing...";
            try
            {
                foreach (ulong each in missing)
                {
                    ulong id = each;
                    var call = CallResult<RemoteStorageSubscribePublishedFileResult_t>.Create(
                        delegate (RemoteStorageSubscribePublishedFileResult_t r, bool failed)
                        {
                            if (mine != round) return;
                            answered++;
                            if (failed || r.m_eResult == EResult.k_EResultNoConnection || r.m_eResult == EResult.k_EResultTimeout || r.m_eResult == EResult.k_EResultServiceUnavailable || r.m_eResult == EResult.k_EResultNotLoggedOn) unreachable++;
                            if (!failed && r.m_eResult == EResult.k_EResultOK) subscribed.Add(id);
                            words.text = "Subscribing... " + answered + " / " + total;
                            if (answered < total) return;
                            if (subscribed.Count > 0) Plugin.Instance.StartCoroutine(Fetch(subscribed, total, mine));
                            else if (unreachable > 0) GiveUp("Steam refused every subscription for lack of a connection");
                            else { words.text = "None of them could be subscribed to.\n(They may no longer be on the Workshop.)"; openAnyway.SetActive(true); }
                        });
                    call.Set(SteamUGC.SubscribeItem(new PublishedFileId_t(id)));
                    calls.Add(call);
                }
                Plugin.Instance.StartCoroutine(Watch(mine, () => answered >= total));
            }
            catch (Exception e)
            {
                GiveUp(e.Message);
            }
        }

        // download what was subscribed to (the start-up check, run again: it fetches only what is new), add the models
        // to the game, and open the world if it now has everything
        static System.Collections.IEnumerator Fetch(List<ulong> subscribed, int total, int mine)
        {
            string folder = FileUtils.GetWorkshopSubscribedDirectory();
            FastSync.Progress = delegate (string message) { if (words != null) words.text = message; };
            int here = 0;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                words.text = "Downloading...";
                yield return FastSync.Sync();
                if (mine != round) { FastSync.Progress = null; yield break; }       // (left the page meanwhile)
                here = 0;
                foreach (ulong id in subscribed) if (File.Exists(Path.Combine(Path.Combine(folder, "wi_" + id), "data.bytes"))) here++;
                if (here == subscribed.Count || (here == 0 && attempt >= 2)) break;
                yield return new WaitForSeconds(2f);        // (Steam can take a moment to list a new subscription)
                if (mine != round) { FastSync.Progress = null; yield break; }
            }
            FastSync.Progress = null;
            if (here == 0) { GiveUp("subscribed, but nothing could be downloaded"); yield break; }

            words.text = "Adding models...";
            yield return null;
            if (mine != round) yield break;
            System.Collections.IEnumerator adding = MoreItems.AddLive(subscribed);
            while (true)
            {
                bool more;
                try { more = adding.MoveNext(); }
                catch (Exception e) { Plugin.Log.LogWarning("MissingItems: " + e); break; }
                if (!more) break;
                yield return adding.Current;
            }
            if (mine != round) yield break;         // (left the page while the models were being added: they stay added)

            int left = Missing(asked).Count;
            Plugin.Log.LogInfo("MissingItems: subscribed to " + subscribed.Count + " of " + total + ", " + MoreItems.Added + " added, the world still lacks " + left);
            if (left == 0)
            {
                words.text = "Added " + Count(MoreItems.Added) + ".\nOpening...";
                yield return new WaitForSeconds(1.5f);
                if (mine == round && page != null && page.activeSelf) Leave(delegate { WorldSelect.Selection.SetActive(true); if (open != null) open(); });
            }
            else
            {
                words.text = "Added " + MoreItems.Added + " of " + Count(total) + ".\n" + left + " could not be added" + (subscribed.Count < total ? " (some are no longer on the Workshop)" : "") + ".";
                openAnyway.SetActive(true);
            }
        }

        static void Build()
        {
            WristMenu menu = WorldSelect.Menu;
            var rt = WorldSelect.NewPage("TinyTownEnhanced Missing Items");
            page = rt.gameObject;
            MenuPage.Paint(MenuPage.Chevron(MenuPage.Button(menu, rt, "Back", 200f, 60f, -560f, 320f, delegate { Leave(back); })), MenuPage.Role.Quiet);
            words = MenuPage.Label(rt, 1100f, 320f, 0f, 90f, 46);
            subscribe = MenuPage.Button(menu, rt, "Subscribe", 420f, 100f, -240f, -200f, Subscribe);
            MenuPage.Paint(subscribe, MenuPage.Role.Go);
            openAnyway = MenuPage.Button(menu, rt, "Open Without Them", 420f, 100f, 240f, -200f, delegate { Leave(delegate { WorldSelect.Selection.SetActive(true); if (open != null) open(); }); });
            MenuPage.Paint(openAnyway, MenuPage.Role.Choice);
            page.SetActive(false);
        }
    }
}
