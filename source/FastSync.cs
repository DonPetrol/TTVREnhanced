// Tiny Town Enhanced - faster Workshop check at start-up.
// The game asks Steam about every subscribed item one at a time (and gives up on everything at the first answer
// that takes over 5 seconds). This module replaces that routine (SteamWorkshop.SyncItems) with one that:
//   - asks about 50 items per request (falling back to one request per item, 32 at once) and downloads several at a time, writing the same folders and files the game does
//   - deletes the downloaded copy of items you are no longer subscribed to (the game never removes them); an
//     unsubscribed world is also taken out of your worlds list, unless you have edited it
//   - refuses downloads that aren't a real model or world (see Safety.cs)
//   - doesn't crawl when the headset isn't being worn (see Headset.cs): the whole check then runs without waiting
//     for frames. Where "not worn" is only a guess from the frame rate, a couple of frames are let through every few
//     seconds so that the guess can be made again
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace TinyTownEnhanced
{
    public class FastSync : IModule
    {
        public string Name { get { return "FastSync"; } }
        const int DetailsAtOnce = 32, DownloadsAtOnce = 4, ItemsPerQuery = 50, QueriesAtOnce = 4;
        static int batched;
        // what Steam says about one item
        class Entry { public ulong Id; public string Title, FileName; public uint Updated; public UGCHandle_t File; }
        const long MaxUnpacked = 256L << 20;      // no real item comes close

        public void Start(Plugin plugin)
        {
            plugin.Harmony.Patch(AccessTools.Method(typeof(SteamWorkshop), "SyncItems"),
                                 new HarmonyMethod(typeof(FastSync), "SyncItemsPrefix"));
        }
        public void SceneLoaded(string scene) { }
        public void Tick() { }

        // runs instead of the game's SyncItems
        static bool SyncItemsPrefix(ref IEnumerator __result) { __result = Sync(); return false; }

        /// (also run by MissingItems.cs after subscribing to items from inside the game)
        public static IEnumerator Sync()
        {
            batched = 0; slowFrames = false;   // (counted afresh each time this runs)
            float lastFrameAt = Time.realtimeSinceStartup;      // when a frame was last let through (see Recheck)
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string dir = FileUtils.GetWorkshopSubscribedDirectory();
            FileUtils.EnsureDirectoryExists(dir);

            // 1) ids of every subscribed item (Steam returns them in pages)
            var ids = new List<PublishedFileId_t>();
            var subscribedAt = new Dictionary<ulong, uint>();        // when each was subscribed to (the Workshop page of "+" lists worlds in that order)
            int total = -1;
            while (total < 0 || ids.Count < total)
            {
                bool done = false, ok = false; int got = 0;
                var cr = CallResult<RemoteStorageEnumerateUserSubscribedFilesResult_t>.Create(
                    delegate (RemoteStorageEnumerateUserSubscribedFilesResult_t r, bool io)
                    {
                        done = true;
                        if (io || r.m_eResult != EResult.k_EResultOK) return;
                        ok = true; total = r.m_nTotalResultCount; got = r.m_nResultsReturned;
                        for (int i = 0; i < got; i++) { ids.Add(r.m_rgPublishedFileId[i]); subscribedAt[r.m_rgPublishedFileId[i].m_PublishedFileId] = r.m_rgRTimeSubscribed[i]; }
                    });
                cr.Set(SteamRemoteStorage.EnumerateUserSubscribedFiles((uint)ids.Count));
                float t0 = Time.realtimeSinceStartup;
                while (!done && Time.realtimeSinceStartup - t0 < 20f)
                {
                    if (!Headset.Idle) { yield return null; lastFrameAt = Time.realtimeSinceStartup; continue; }
                    Pump(() => !done, 20000);
                    if (Recheck(lastFrameAt)) { yield return null; yield return null; lastFrameAt = Time.realtimeSinceStartup; }
                }
                cr.Dispose();
                if (!ok || got == 0) break;
            }
            if (total < 0) { Plugin.Log.LogWarning("FastSync: Steam didn't list subscriptions; using the items already downloaded"); yield break; }

            // 1b) the full list arrived: remove downloaded items that aren't subscribed any more
            // (Not when Steam says there are no subscriptions at all while items are on disk: that answer may come from
            //  Steam being offline or out of sorts, and acting on it would delete every downloaded item and world.)
            int removed = 0;
            bool nothingListed = total == 0 && Directory.GetDirectories(dir, "wi_*").Length > 0;
            if (nothingListed) Plugin.Log.LogWarning("FastSync: Steam lists no subscriptions, but there are downloaded items; none were removed");
            if (ids.Count == total && !nothingListed)
            {
                var subscribed = new HashSet<ulong>();
                foreach (var id in ids) subscribed.Add(id.m_PublishedFileId);
                foreach (var d in Directory.GetDirectories(dir))
                {
                    string n = Path.GetFileName(d); ulong id;
                    string num = n.StartsWith("wi_world_") ? n.Substring(9) : n.StartsWith("wi_") ? n.Substring(3) : null;
                    if (num == null || !ulong.TryParse(num, out id) || subscribed.Contains(id)) continue;   // other folders are left alone
                    if (n.StartsWith("wi_world_")) RemoveWorldCopy(d, n);
                    try { Directory.Delete(d, true); removed++; }
                    catch (Exception e) { Plugin.Log.LogWarning("FastSync: couldn't remove " + n + ": " + e.Message); }
                }
            }

            // 2) what's already on disk
            var local = new Dictionary<ulong, SteamWorkshop.WorkshopItemInfo>();
            var localDir = new Dictionary<ulong, string>();
            foreach (var d in Directory.GetDirectories(dir))
            {
                try
                {
                    string meta = Path.Combine(d, "metadata.json");
                    if (!File.Exists(meta)) continue;
                    var info = JsonUtility.FromJson<SteamWorkshop.WorkshopItemInfo>(File.ReadAllText(meta));
                    if (info != null) { local[info.id] = info; localDir[info.id] = d; }
                }
                catch (Exception) { }
            }

            // 3) what Steam has: title, last update and file of every item, asked for 50 items per request.
            //    Anything that doesn't come back that way is asked about one by one, as before (step 3b).
            var download = new List<Entry>();
            var calls = new List<IDisposable>();
            var slow = new List<PublishedFileId_t>();
            var queries = new List<UGCQueryHandle_t>();              // requests Steam is still holding (each must be released)
            int next = 0, pending = 0, finished = 0;
            float lastProgress = Time.realtimeSinceStartup;
            Action<Entry> consider = delegate (Entry e)
            {
                SteamWorkshop.WorkshopItemInfo info; string folder;
                bool have = local.TryGetValue(e.Id, out info) && localDir.TryGetValue(e.Id, out folder)
                            && (info.isWorld ? File.Exists(Path.Combine(folder, "data")) && File.Exists(Path.Combine(folder, "preview.jpg"))     // (a world whose files have gone is fetched again)
                                             : File.Exists(Path.Combine(folder, "data.bytes")));
                if (!have || info.updateTime != e.Updated) download.Add(e);
                else if (info.title != e.Title)
                {   // renamed on the Workshop: just update the title
                    info.title = e.Title;
                    try { File.WriteAllText(Path.Combine(localDir[e.Id], "metadata.json"), JsonUtility.ToJson(info)); } catch (Exception) { }
                }
            };
            while ((next < ids.Count || pending > 0) && Time.realtimeSinceStartup - lastProgress < 30f)
            {
                while (next < ids.Count && pending < QueriesAtOnce)
                {
                    var chunk = ids.GetRange(next, Math.Min(ItemsPerQuery, ids.Count - next));
                    next += chunk.Count;
                    var cr = CallResult<SteamUGCQueryCompleted_t>.Create(
                        delegate (SteamUGCQueryCompleted_t r, bool io)
                        {
                            pending--; lastProgress = Time.realtimeSinceStartup;
                            var answered = new HashSet<ulong>();
                            if (!io && r.m_eResult == EResult.k_EResultOK)
                                for (uint k = 0; k < r.m_unNumResultsReturned; k++)
                                {
                                    SteamUGCDetails_t d;
                                    if (!SteamUGC.GetQueryUGCResult(r.m_handle, k, out d) || d.m_eResult != EResult.k_EResultOK) continue;
                                    if (d.m_hFile == UGCHandle_t.Invalid || d.m_hFile.m_UGCHandle == 0) continue;      // no file given: ask the old way
                                    answered.Add(d.m_nPublishedFileId.m_PublishedFileId);
                                    WorkshopInfo.Details(d.m_nPublishedFileId.m_PublishedFileId, d.m_ulSteamIDOwner, d.m_rtimeUpdated);     // (for the build menu's Workshop sorting: BuildMenu.cs)
                                    finished++; batched++;
                                    consider(new Entry { Id = d.m_nPublishedFileId.m_PublishedFileId, Title = d.m_rgchTitle, FileName = d.m_pchFileName, Updated = d.m_rtimeUpdated, File = d.m_hFile });
                                }
                            SteamUGC.ReleaseQueryUGCRequest(r.m_handle); queries.Remove(r.m_handle);
                            foreach (var id in chunk) if (!answered.Contains(id.m_PublishedFileId)) slow.Add(id);
                        });
                    var query = SteamUGC.CreateQueryUGCDetailsRequest(chunk.ToArray(), (uint)chunk.Count);
                    queries.Add(query);
                    cr.Set(SteamUGC.SendQueryUGCRequest(query));
                    calls.Add(cr); pending++;
                }
                Screen("Checking Workshop items... " + finished + " / " + ids.Count);
                if (!Headset.Idle) { yield return null; lastFrameAt = Time.realtimeSinceStartup; continue; }
                Pump(() => pending > 0, 30000);
                if (Recheck(lastFrameAt)) { yield return null; yield return null; lastFrameAt = Time.realtimeSinceStartup; }
            }
            if (pending > 0) { slow.Clear(); slow.AddRange(ids); download.Clear(); finished = 0; batched = 0; }     // no answer in time: do it all the old way
            foreach (var c in calls) c.Dispose();
            calls.Clear();
            // (a request that was never answered is released here; the answered ones were released as they came in)
            foreach (var q in queries) SteamUGC.ReleaseQueryUGCRequest(q);
            queries.Clear();

            // 3b) one request per item, many at once, for whatever is left
            next = 0; pending = 0;
            lastProgress = Time.realtimeSinceStartup;
            while ((next < slow.Count || pending > 0) && Time.realtimeSinceStartup - lastProgress < 30f)
            {
                while (next < slow.Count && pending < DetailsAtOnce)
                {
                    var cr = CallResult<RemoteStorageGetPublishedFileDetailsResult_t>.Create(
                        delegate (RemoteStorageGetPublishedFileDetailsResult_t r, bool io)
                        {
                            pending--; finished++; lastProgress = Time.realtimeSinceStartup;
                            if (io || r.m_eResult != EResult.k_EResultOK || r.m_hFile == UGCHandle_t.Invalid) return;
                            consider(new Entry { Id = r.m_nPublishedFileId.m_PublishedFileId, Title = r.m_rgchTitle, FileName = r.m_pchFileName, Updated = r.m_rtimeUpdated, File = r.m_hFile });
                        });
                    cr.Set(SteamRemoteStorage.GetPublishedFileDetails(slow[next++], 0u));
                    calls.Add(cr); pending++;
                }
                Screen("Checking Workshop items... " + Math.Min(finished, ids.Count) + " / " + ids.Count);
                if (!Headset.Idle) { yield return null; lastFrameAt = Time.realtimeSinceStartup; continue; }
                Pump(() => pending > 0, 30000);
                if (Recheck(lastFrameAt)) { yield return null; yield return null; lastFrameAt = Time.realtimeSinceStartup; }
            }
            foreach (var c in calls) c.Dispose();
            calls.Clear();

            // 4) download the new and updated ones, a few at a time
            int saved = 0, failed = 0; next = 0; pending = 0;
            lastProgress = Time.realtimeSinceStartup;
            while ((next < download.Count || pending > 0) && Time.realtimeSinceStartup - lastProgress < 60f)
            {
                while (next < download.Count && pending < DownloadsAtOnce)
                {
                    var d = download[next++];
                    var cr = CallResult<RemoteStorageDownloadUGCResult_t>.Create(
                        delegate (RemoteStorageDownloadUGCResult_t r, bool io)
                        {
                            pending--; lastProgress = Time.realtimeSinceStartup;
                            try
                            {
                                if (io || r.m_eResult != EResult.k_EResultOK || r.m_nSizeInBytes <= 0) { failed++; return; }
                                var bytes = new byte[r.m_nSizeInBytes];
                                SteamRemoteStorage.UGCRead(r.m_hFile, bytes, r.m_nSizeInBytes, 0u, EUGCReadAction.k_EUGCRead_Close);
                                Save(dir, d, bytes);
                                saved++;
                            }
                            catch (Safety.Blocked b)
                            {
                                failed++;
                                Notices.Error("Blocked Workshop item \"" + d.Title + "\": " + b.Message + ". It was not downloaded; unsubscribe from it on Steam.");
                            }
                            catch (Exception e) { failed++; Plugin.Log.LogWarning("FastSync: item " + d.Id + ": " + e.Message); }
                        });
                    cr.Set(SteamRemoteStorage.UGCDownload(d.File, 0u));
                    calls.Add(cr); pending++;
                }
                Screen("Downloading Workshop items... " + (saved + failed) + " / " + download.Count);
                if (!Headset.Idle) { yield return null; lastFrameAt = Time.realtimeSinceStartup; continue; }
                Pump(() => pending > 0, 60000);
                if (Recheck(lastFrameAt)) { yield return null; yield return null; lastFrameAt = Time.realtimeSinceStartup; }
            }
            foreach (var c in calls) c.Dispose();
            foreach (var kv in subscribedAt) WorkshopInfo.SubscribedAt(kv.Key, kv.Value);
            WorkshopInfo.Save();
            // each downloaded world is told when it was subscribed to
            foreach (var kv in subscribedAt)
                try
                {
                    string folder = Path.Combine(dir, WorldCopies.WorkshopPrefix + kv.Key), note = Path.Combine(folder, WorldCopies.SubscribedFile), when = kv.Value.ToString();
                    if (Directory.Exists(folder) && (!File.Exists(note) || File.ReadAllText(note) != when)) File.WriteAllText(note, when);
                }
                catch (Exception) { }
            Screen("Processing Workshop Items...");
            Plugin.Log.LogInfo("FastSync: " + ids.Count + " subscribed, " + finished + " checked (" + batched + " in batches), " + saved + " downloaded, " + failed + " failed, " + removed + " unsubscribed removed, " + (slowFrames ? "headset idle, " : "") + timer.ElapsedMilliseconds + " ms");
        }

        // headset idle: collect Steam's answers right here instead of waiting for the next (very slow) frame
        static bool slowFrames;

        // Headset idle only by a guess from the frame rate (Headset.cs): while this routine waits without letting
        // frames through, that guess cannot change. So every few seconds two frames are let through - the first is
        // long because of the waiting itself, the second shows how fast frames really come - and Idle is asked again.
        const float RecheckSeconds = 5f;
        static bool Recheck(float lastFrameAt) { return Headset.Guessed && Time.realtimeSinceStartup - lastFrameAt > RecheckSeconds; }

        static void Pump(Func<bool> stillWaiting, int maxMs)
        {
            slowFrames = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (stillWaiting() && sw.ElapsedMilliseconds < maxMs)
            {
                SteamAPI.RunCallbacks();
                System.Threading.Thread.Sleep(5);
            }
        }

        // The game copies a subscribed world into your worlds folder once. When you unsubscribe, that copy goes too,
        // but only while it is still exactly the downloaded world; anything you have saved over it is kept.
        static void RemoveWorldCopy(string download, string name)
        {
            try
            {
                string copy = Path.Combine(FileUtils.GetSaveDirectory(), name);
                if (!Directory.Exists(copy)) return;
                if (WorldCopies.Untouched(copy, Path.Combine(download, "data"), true)) { Directory.Delete(copy, true); Plugin.Log.LogInfo("FastSync: removed unsubscribed world " + name); }
                else Plugin.Log.LogInfo("FastSync: kept world " + name + " (unsubscribed, but it has been edited)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("FastSync: world " + name + ": " + e.Message); }
        }

        // same layout the game writes: wi_<id> (or wi_world_<id>) with the unpacked files + metadata.json
        static void Save(string dir, Entry d, byte[] gz)
        {
            bool world = d.FileName != null && d.FileName.Contains("_world_");
            string name = (world ? "wi_world_" : "wi_") + d.Id;
            string folder = Path.Combine(dir, name);
            var files = Safety.Check(Unpack(gz), world);    // throws on a damaged or unsafe download, before touching the folder
            Directory.CreateDirectory(folder);
            foreach (var kv in files) File.WriteAllBytes(Path.Combine(folder, kv.Key), kv.Value);
            var info = new SteamWorkshop.WorkshopItemInfo();
            info.id = d.Id; info.title = d.Title; info.directory = name;
            info.updateTime = d.Updated; info.isWorld = world;
            File.WriteAllText(Path.Combine(folder, "metadata.json"), JsonUtility.ToJson(info));
        }

        // the game's container: gzip of [int32 name length][UTF-16 name][int32 size][bytes], repeated
        static List<KeyValuePair<string, byte[]>> Unpack(byte[] gz)
        {
            byte[] raw;
            using (var src = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                var buf = new byte[65536]; int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxUnpacked) throw new Exception("download unpacks to more than " + (MaxUnpacked >> 20) + " MB");
                }
                raw = ms.ToArray();
            }
            var list = new List<KeyValuePair<string, byte[]>>();
            int p = 0;
            while (p + 4 <= raw.Length)
            {
                int len = BitConverter.ToInt32(raw, p); p += 4;
                if (len < 0 || len > 1024 || p + len * 2 + 4 > raw.Length) throw new Exception("damaged download");
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++) { sb.Append(BitConverter.ToChar(raw, p)); p += 2; }
                int size = BitConverter.ToInt32(raw, p); p += 4;
                if (size < 0 || p + size > raw.Length) throw new Exception("damaged download");
                var data = new byte[size]; Buffer.BlockCopy(raw, p, data, 0, size); p += size;
                list.Add(new KeyValuePair<string, byte[]>(sb.ToString(), data));
            }
            if (list.Count == 0) throw new Exception("empty download");
            return list;
        }

        // progress on the game's own loading message
        static Text screenText; static bool looked;
        public static Action<string> Progress;      // told each progress message as well, if set
        static void Screen(string msg)
        {
            if (!looked)
            {
                looked = true;
                try
                {
                    var init = UnityEngine.Object.FindObjectOfType<InitState>();
                    if (init != null) screenText = AccessTools.Field(typeof(InitState), "screenMessage").GetValue(init) as Text;
                }
                catch (Exception) { }
            }
            if (screenText != null) screenText.text = msg;
            if (Progress != null) Progress(msg);
        }
    }
}
