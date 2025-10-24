using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using Mirror; // host/server check (no networking added by us)

namespace ValhATLYSS
{
    [BepInPlugin("cconx.ValhATLYSS", "ValhATLYSS", "0.4.17-hotfix")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private FileSystemWatcher _watcher;
        private string _profilesRoot;
        private DateTime _lastEventUtc = DateTime.MinValue;
        private bool _bootstrapped;

        private void Awake()
        {
            Log = this.Logger;
            Log.LogInfo("[ValhATLYSS] Awake");

            _profilesRoot = GetProfilesRoot();
            if (_profilesRoot == null)
            {
                Log.LogWarning("[ValhATLYSS] Could not locate profileCollections path. Mod will idle.");
            }
            else
            {
                Log.LogInfo("[ValhATLYSS] Profiles root: " + _profilesRoot);
                TryEnableWatcher(_profilesRoot);
            }

            StartCoroutine(Bootstrap());
        }

        private IEnumerator Bootstrap()
        {
            // Small delay to let GameManager/StatLogics/Player spawn
            yield return new WaitForSeconds(2.0f);
            _bootstrapped = true;

            // Our file-based vault remains as-is
            HoddKista.EnsureReady(Log);

            // >>> NEW: host-only level-cap raise (no reflection, no fingerprinting) <<<
            TryRaiseServerLevelCap(64); // change 64 to your desired cap

            // (optional) first reconcile pass on the most recent profile
            if (TryGetMostRecentProfile(out var path))
            {
                Log.LogInfo("[ValhATLYSS] Initial reconcile of most recent profile: " + path);
                TryReconcileProfile(path);
            }
            else
            {
                Log.LogInfo("[ValhATLYSS] No recent profile found to reconcile at bootstrap.");
            }
        }

        /// <summary>
        /// If we are HOST (listen server / singleplayer), bump StatLogics._maxMainLevel.
        /// This is the server-side gate checked by PlayerStats.GainExp / OnLevelUp.
        /// Does nothing on remote servers (client-only).
        /// </summary>
        private void TryRaiseServerLevelCap(int desiredCap)
        {
            try
            {
                var gm = GameManager._current;
                var stat = gm != null ? gm._statLogics : null;
                if (stat == null)
                {
                    Log?.LogInfo("[ValhATLYSS] Level-cap bump skipped: StatLogics not ready.");
                    return;
                }

                // Are we actually the host/server in this process?
                bool isHost = false;
                try { isHost = NetworkServer.active; } catch { /* Mirror not active yet */ }
                if (!isHost && Player._mainPlayer != null)
                    isHost = Player._mainPlayer.isServer;

                if (!isHost)
                {
                    Log?.LogInfo("[ValhATLYSS] Level-cap bump skipped: not host/server (remote server controls cap).");
                    return;
                }

                if (desiredCap < 32) desiredCap = 32; // never lower below vanilla
                if (stat._maxMainLevel < desiredCap)
                {
                    int old = stat._maxMainLevel;
                    stat._maxMainLevel = desiredCap;
                    Log?.LogInfo($"[ValhATLYSS] Raised server level cap: {old} → {desiredCap} (host-only).");
                }
                else
                {
                    Log?.LogInfo($"[ValhATLYSS] Server level cap already {stat._maxMainLevel}.");
                }

                // Optional: sanity log of curve coverage (no edits)
                if (stat._experienceCurve != null)
                {
                    int keys = stat._experienceCurve.keys != null ? stat._experienceCurve.keys.Length : 0;
                    Log?.LogInfo($"[ValhATLYSS] ExperienceCurve keys: {keys} (cap {stat._maxMainLevel}).");
                }
            }
            catch (Exception e)
            {
                Log?.LogWarning("[ValhATLYSS] Level-cap bump failed: " + e.Message);
            }
        }

        private void TryEnableWatcher(string profilesRoot)
        {
            try
            {
                _watcher = new FileSystemWatcher(profilesRoot)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    Filter = "atl_characterProfile_*",
                    EnableRaisingEvents = true
                };

                _watcher.Changed += OnProfileChanged;
                _watcher.Created += OnProfileChanged;
                _watcher.Renamed += OnProfileRenamed;

                Log.LogInfo("[ValhATLYSS] File watcher enabled.");
            }
            catch (Exception e)
            {
                Log?.LogWarning("[ValhATLYSS] Failed to enable watcher: " + e.Message);
            }
        }

        private void OnProfileChanged(object sender, FileSystemEventArgs e)
        {
            if (!_bootstrapped) return;
            var now = DateTime.UtcNow;
            if ((now - _lastEventUtc).TotalMilliseconds < 250) return; // debounce
            _lastEventUtc = now;

            TryReconcileProfile(e.FullPath);
        }

        private void OnProfileRenamed(object sender, RenamedEventArgs e)
        {
            if (!_bootstrapped) return;
            var now = DateTime.UtcNow;
            if ((now - _lastEventUtc).TotalMilliseconds < 250) return;
            _lastEventUtc = now;

            TryReconcileProfile(e.FullPath);
        }

        private void TryReconcileProfile(string profilePath)
        {
            try
            {
                if (!File.Exists(profilePath))
                {
                    Log?.LogWarning("[ValhATLYSS] Reconcile skipped; path missing: " + profilePath);
                    return;
                }

                if (!KappaSlot.TryReadDiskStats(profilePath, out var disk, Log))
                {
                    Log?.LogWarning("[ValhATLYSS] Could not read disk stats for: " + profilePath);
                    return;
                }

                var result = HoddKista.Reconcile(profilePath, disk, Log);
                switch (result)
                {
                    case -1: Log?.LogInfo("[ValhATLYSS] Disk was restored from vault (anti-regression)."); break;
                    case +1: Log?.LogInfo("[ValhATLYSS] Vault was updated from disk (progress persisted)."); break;
                    default: Log?.LogInfo("[ValhATLYSS] No reconcile change necessary."); break;
                }
            }
            catch (Exception ex)
            {
                Log?.LogWarning("[ValhATLYSS] Reconcile failed: " + ex.Message);
            }
        }

        internal static string GetProfilesRoot()
        {
            try
            {
                var root = Path.Combine(Paths.GameRootPath, "ATLYSS_Data", "profileCollections");
                return Directory.Exists(root) ? root : null;
            }
            catch { return null; }
        }

        internal static bool TryGetMostRecentProfile(out string fullPath)
        {
            fullPath = null;
            try
            {
                var root = GetProfilesRoot();
                if (root == null) return false;
                var files = Directory.GetFiles(root, "atl_characterProfile_*", SearchOption.TopDirectoryOnly);
                DateTime last = DateTime.MinValue;
                foreach (var f in files)
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > last) { last = t; fullPath = f; }
                }
                return fullPath != null;
            }
            catch { return false; }
        }
    }
}