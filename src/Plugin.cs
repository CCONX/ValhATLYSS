/**
 * ValhATLYSS :: Plugin.cs
 * ------------------------------------------------------------------------------------
 * Purpose:
 *   Main BepInEx plugin entry point. Responsible for:
 *     - Initializing logging and bootstrapping.
 *     - Discovering the ATLYSS profileCollections folder.
 *     - Watching the character profile files for changes.
 *     - Driving the reconcile pipeline when profiles change.
 *
 * Key ideas:
 *   - Uses a FileSystemWatcher to detect local file changes (no networking).
 *   - On changes, reads profile stats (via KappaSlot), compares with the local
 *     vault snapshot (via HoddKista), and applies "prevent regression" rules.
 *   - Provides utilities for reading curves and leveling logic (Ristir/SeidrVegr).
 *
 * Notes:
 *   - All operations are local to the user's machine.
 *   - Avoid packaging any config/vault folders with releases.
 * ------------------------------------------------------------------------------------
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

        /// <summary>
        /// Awake is called once when the plugin is loaded.
        /// Sets up logging, resolves the ATLYSS profiles folder, configures a FileSystemWatcher,
        /// and starts a delayed bootstrap to avoid early-race conditions while the game loads.
        /// </summary>
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

        /// <summary>
        /// Deferred bootstrap to let the game finish initializing filesystem/state.
        /// </summary>
        private IEnumerator Bootstrap()
        {
            // Small delay to allow the game to fully set up the profile files/directories.
            yield return new WaitForSeconds(2.0f);
            _bootstrapped = true;

            // Ensure the vault directory exists and log its location.
            HoddKista.EnsureReady(Log);

            // On first boot, try to reconcile the most recent profile if one exists.
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
        /// Set up the file watcher to listen to changes on atl_characterProfile_* files.
        /// </summary>
        private void TryEnableWatcher(string profilesRoot)
        {
            try
            {
                _watcher = new FileSystemWatcher(profilesRoot);
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                _watcher.IncludeSubdirectories = false;
                _watcher.Filter = "atl_characterProfile_*";

                _watcher.Changed += OnProfileChanged;
                _watcher.Created += OnProfileChanged;
                _watcher.Renamed += OnProfileRenamed;
                _watcher.EnableRaisingEvents = true;

                Log.LogInfo("[ValhATLYSS] File watcher enabled.");
            }
            catch (Exception e)
            {
                Log?.LogWarning("[ValhATLYSS] Failed to enable watcher: " + e.Message);
            }
        }

        /// <summary>
        /// Handler for file Changed/Created events. Debounced to avoid double-firing while the game writes.
        /// </summary>
        private void OnProfileChanged(object sender, FileSystemEventArgs e)
        {
            if (!_bootstrapped) return;
            var now = DateTime.UtcNow;
            if ((now - _lastEventUtc).TotalMilliseconds < 250) return; // debounce
            _lastEventUtc = now;

            // Reconcile the specific profile that triggered the change.
            TryReconcileProfile(e.FullPath);
        }

        /// <summary>
        /// Handler for file rename events (e.g., temp writes -> final).
        /// </summary>
        private void OnProfileRenamed(object sender, RenamedEventArgs e)
        {
            if (!_bootstrapped) return;
            var now = DateTime.UtcNow;
            if ((now - _lastEventUtc).TotalMilliseconds < 250) return; // debounce
            _lastEventUtc = now;

            TryReconcileProfile(e.FullPath);
        }

        /// <summary>
        /// Performs the reconcile flow for one profile path:
        /// 1) Read disk stats (KappaSlot).
        /// 2) Compare vs vault snapshot (HoddKista).
        /// 3) Apply anti-regression or update vault accordingly.
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
        /// Helper to create a progression curve from an initial delta and exponential growth.
        /// Used for EXP curves or other balancing tasks (utility).
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

                // Extend to level 64 as a default target cap.
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
