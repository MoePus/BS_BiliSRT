using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using IPA;
using Logger = IPA.Logging.Logger;
using SongCore;
using System.Collections.Concurrent;
using System.Linq;
using System.IO.Pipes;
using System.IO;
using System;
using System.Collections;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace BiliSRT
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        public static Logger log { get; private set; }
        [Init]
        public Plugin(Logger ipaLogger)
        {
            ipaLogger.Info($"BiliBSR Loaded.");
            log = ipaLogger;
            Harmony harmony = new Harmony("BiliBSR");

            var ProcessSongList_MethodInfo = typeof(SongBrowser.SongBrowserModel)
                .GetMethod("ProcessSongList", BindingFlags.Public | BindingFlags.Instance);

            harmony.Patch(ProcessSongList_MethodInfo,
                new HarmonyMethod(typeof(BiliSRT).
                    GetMethod(nameof(BiliSRT.ProcessSongList_Prefix),
                    BindingFlags.Public | BindingFlags.Static))
            );
        }

        [OnStart]
        public void OnApplicationStart()
        {
            BiliSRT.Instance.IsInvoking();
        }
    }

    public class BiliSRT : MonoBehaviour
    {
        public static BiliSRT Instance
        {
            get
            {
                if (!BiliSRT._instance)
                {
                    BiliSRT._instance = new GameObject("BiliSRT").AddComponent<BiliSRT>();
                }
                return BiliSRT._instance;
            }
            private set
            {
                BiliSRT._instance = value;
            }
        }
        public static BeatSaverSharp.BeatSaver BeatSaver
        {
            get 
            {
                return BeatSaverDownloader.Plugin.BeatSaver;
            }
        }
        private static BiliSRT _instance;

        private HashSet<string> downloadedSongHashes;
        private Dictionary<string, CustomPreviewBeatmapLevel> downloadedSongs;
        private HashSet<string> requestedSongs;
        private HashSet<string> downloadQueue;
        private HashSet<string> adjustQueue;

        private BeatSaverDownloader.UI.MoreSongsFlowCoordinator moreSongsFlowCooridinator;

        private NamedPipeServerStream DanmakuHimePipe;
        public void ReconnectPipe()
        {
            DanmakuHimePipe = new NamedPipeServerStream("BiliSRT-Pipe");
            DanmakuHimePipe.BeginWaitForConnection(new AsyncCallback((res) => {
                DanmakuHimePipe.EndWaitForConnection(res);
                ReadKeyFromPipe();
            }), null);
        }
        public void Awake()
        {
            downloadedSongHashes = new HashSet<string>();
            downloadedSongs = new Dictionary<string, CustomPreviewBeatmapLevel>();
            requestedSongs = new HashSet<string>();
            downloadQueue = new HashSet<string>();
            adjustQueue = new HashSet<string>();

            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            ReconnectPipe();
            if (!Loader.AreSongsLoaded)
            {
                Loader.SongsLoadedEvent += this.SongLoader_SongsLoadedEvent;
                return;
            }
            this.SongLoader_SongsLoadedEvent(null, Loader.CustomLevels);
        }
        public void OnDestroy()
        {
        }
        async private void ReadKeyFromPipe()
        {
            using (StreamReader sr = new StreamReader(DanmakuHimePipe))
            {
                string key = await sr.ReadLineAsync();
                if (downloadedSongs.Count() > 0 && key != null)
                {
                    key = key.Trim();
                    if (key.Length > 1)
                    {
                        Plugin.log.Info($"received song request key: {key}");
                        if (MapExists(key))
                        {
                            Plugin.log.Info($"key {key} exists, adjust its ts");
                            adjustQueue.Add(key);
                        }
                        else
                        {
                            Plugin.log.Info($"key {key} doesnt exist, add to download queue");
                            downloadQueue.Add(key);
                        }
                    }
                }
            }
            ReconnectPipe();
        }
        private void FetchMoreSongsMenu()
        {
            var BeatSaverDownloaderUI = BeatSaverDownloader.UI.PluginUI.instance;
            moreSongsFlowCooridinator = AccessTools.FieldRefAccess<BeatSaverDownloader.UI.MoreSongsFlowCoordinator>
                (BeatSaverDownloaderUI.GetType(), "_moreSongsFlowCooridinator")(BeatSaverDownloaderUI);
        }
        public void Update()
        {
            if (moreSongsFlowCooridinator == null)
            {
                FetchMoreSongsMenu();
                return;
            }
            if (!moreSongsFlowCooridinator.isActivated)
                return;
            if (downloadQueue.Count() == 0)
                return;

            var bakQueue = downloadQueue.ToList();
            downloadQueue.Clear();
            foreach (var key in bakQueue)
            {
                Plugin.log.Info($"prepare song {key} for download");
                DownloadSong(key);
            }
        }
        private void SongLoader_SongsLoadedEvent(Loader sender, ConcurrentDictionary<string, CustomPreviewBeatmapLevel> levels)
        {
            downloadedSongHashes = new HashSet<string>(from x in levels.Values
                                                               select Collections.hashForLevelID(x.levelID));

            string pattern = "CustomLevels\\\\([0-9a-zA-Z]+) \\(";
            foreach (var x in levels.Values)
            {
                if (x.customLevelPath != null && x.customLevelPath.Length > 28)
                {
                    var ms = Regex.Match(x.customLevelPath, pattern);
                    if (ms.Success)
                    {
                        downloadedSongs[ms.Groups[1].Value] = x;
                    }
                }
            }
        }
        public static bool ProcessSongList_Prefix(System.Object __instance)
        {
            if (Instance.adjustQueue.Count == 0)
                return true;

            var cachedLastWriteTimes = AccessTools.FieldRefAccess<Dictionary<string, double>>(__instance.GetType(), "_cachedLastWriteTimes")(__instance);
            var epoch = new DateTime(1970, 1, 1);
            foreach (var key in Instance.adjustQueue)
            {
                var levelID = Instance.downloadedSongs[key].levelID;
                Plugin.log.Info($"Lookup {key}-{levelID}'s ts");
                if (cachedLastWriteTimes.ContainsKey(levelID))
                {
                    cachedLastWriteTimes[levelID] = (DateTime.UtcNow - epoch).TotalMilliseconds;
                    Plugin.log.Info($"Adjust song {key}'s ts");
                }
            }
            Instance.adjustQueue.Clear();
            return true;
        }
        private async void DownloadSong(string key)
        {
            try
            {
                var song = new BeatSaverSharp.Beatmap(BeatSaver, key);
                await song.Populate();
                Plugin.log.Info($"checking song {key}-({song.Name})-{song.Hash} for download");
                if (ShouldDownload(song.Hash))
                {
                    var downloadQueueView = AccessTools.FieldRefAccess<BeatSaverDownloader.UI.ViewControllers.DownloadQueueViewController>
                        (moreSongsFlowCooridinator.GetType(), "_downloadQueueView")(moreSongsFlowCooridinator);

                    var EnqueueSong = AccessTools.Method(
                        typeof(BeatSaverDownloader.UI.ViewControllers.DownloadQueueViewController),
                        "EnqueueSong");
                    EnqueueSong.Invoke(downloadQueueView, new object[] { song, null });
                }
            }
            catch (Exception e)
            {
                Plugin.log.Error($"{e}");
            }
        }
        private bool ShouldDownload(string levelHash)
        {
            return !(requestedSongs.Contains(levelHash.ToUpper()) || downloadedSongHashes.Contains(levelHash.ToUpper()));
        }

        private bool MapExists(string key)
        {
            return downloadedSongs.ContainsKey(key);
        }
    }
}
