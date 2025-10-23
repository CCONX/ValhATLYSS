/**
 * ValhATLYSS :: Plugin.cs
 * -----------------------------------------------------------------------------
 * Purpose:
 *   BepInEx entry point. Sets up:
 *     - Logging
 *     - Discovery of the ATLYSS profileCollections folder
 *     - FileSystemWatcher for atl_characterProfile_* changes
 *     - Reconcile pipeline (disk <-> vault) on change
 *
 * Notes:
 *   - Entirely local; no networking.
 *   - This build performs NO live in-memory stat application. It restores by
 *     writing the profile file only. The game will pick up those values normally.
 * -----------------------------------------------------------------------------
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace ValhATLYSS
{
    [BepInPlugin("cconx.ValhATLYSS", "ValhATLYSS", "0.4.16")]
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

        /// <summary>Deferred boot to avoid early race conditions during game init.</summary>
        private IEnumerator Bootstrap()
        {
            yield return new WaitForSeconds(2.0f);
            _bootstrapped = true;

            HoddKista.EnsureReady(Log);

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

        /// <summary>Set up the watcher to listen for profile file modifications.</summary>
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
            if ((now - _lastEventUtc).TotalMilliseconds < 250) return; // debounce
            _lastEventUtc = now;

            TryReconcileProfile(e.FullPath);
        }

        /// <summary>
        /// Read disk stats, compare vs. vault, and reconcile in whichever direction is needed.
        /// </summary>
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

        /// <summary>ATLYSS profileCollections path under the game root.</summary>
        internal static string GetProfilesRoot()
        {
            try
            {
                var root = Path.Combine(Paths.GameRootPath, "ATLYSS_Data", "profileCollections");
                return Directory.Exists(root) ? root : null;
            }
            catch { return null; }
        }

        /// <summary>Find the most recently modified atl_characterProfile_* file.</summary>
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
                    if (t > last)
                    {
                        last = t;
                        fullPath = f;
                    }
                }
                return fullPath != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Utility: extend a curve using exponential growth of deltas. Not used by reconcile,
        /// but kept as a helper for balancing or future features.
        /// </summary>
        internal static AnimationCurve GrowCurve(AnimationCurve curve, float growth, float delta, Keyframe? last = null)
        {
            try
            {
                if (curve == null) curve = new AnimationCurve();
                var list = new List<Keyframe>(curve.keys);
                if (list.Count == 0 && last.HasValue == false)
                {
                    list.Add(new Keyframe(1, 0));
                    last = new Keyframe(1, 0);
                }

                if (!last.HasValue)
                    last = list[^1];

                float run = last.Value.value;
                float d = delta > 0f ? delta : 100f;

                for (int lvl = (int)last.Value.time + 1; lvl <= 64; lvl++)
                {
                    d *= growth;
                    run += d;
                    list.Add(new Keyframe(lvl, run));
                }

                return new AnimationCurve(list.ToArray());
            }
            catch { return curve; }
        }
    }
}
