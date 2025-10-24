/**
 * ValhATLYSS :: Plugin.cs (0.4.19-hotfix4)
 * -----------------------------------------------------------------------------
 * WHAT THIS FILE DOES
 *  1) Host-only level-cap enforcement:
 *     - Continuously sets GameManager._current._statLogics._maxMainLevel to 64 (or higher if DesiredCap > 64)
 *       when you are the host (solo or listen server).
 *     - Runs every 1s so any late reset (by the base game or other mods) is corrected immediately.
 *
 *  2) Anti-regression on disk saves (no runtime stat injection):
 *     - Watches the ATLYSS profile folder for atl_characterProfile_* saves (ignores "*_bak").
 *     - Debounces bursts and reads the file once; also has a polling safety net every few seconds.
 *     - When a save is detected and we can parse Level/Exp, we call HoddKista.Reconcile(...) to:
 *         • update the local "vault" if the save is better, or
 *         • restore the save from the vault if the save regressed.
 *
 *  3) Strictly no reflection or fingerprinting.
 *
 * SAFE BEHAVIOR
 *  - On remote servers, we never try to raise the cap (server remains authoritative).
 *  - We only touch disk files; we don’t modify live Player stats.
 *  - We ignore backup files "*_bak", only touching the active profile file.
 * -----------------------------------------------------------------------------
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using Mirror; // for NetworkServer.active (host-only check)

namespace ValhATLYSS
{
    [BepInPlugin("cconx.ValhATLYSS", "ValhATLYSS", "0.4.19")]
    public sealed class Plugin : BaseUnityPlugin
    {
        // ---------------------------------------------------------------------
        // TUNABLES
        // ---------------------------------------------------------------------

        /// <summary>Desired server-side cap while hosting. Never below 32.</summary>
        private const int DesiredCap = 64;

        /// <summary>Debounce window for watcher (milliseconds of quiet before we read the file).</summary>
        private const int ReconcileDebounceMs = 2000;

        /// <summary>Polling fallback interval (seconds) if OS file events are missed.</summary>
        private const float PollIntervalSeconds = 5f;

        /// <summary>Set true during validation to get detailed logs; set false when stable.</summary>
        private const bool VerboseWatcherLogs = true;

        // ---------------------------------------------------------------------
        // LOGGING / STATE
        // ---------------------------------------------------------------------

        internal static ManualLogSource Log;

        private FileSystemWatcher _watcher;
        private string _profilesRoot;
        private bool _bootstrapped;

        // Per-profile coalescing + last processed snapshot
        private sealed class Seen
        {
            public DateTime LastWriteUtc;    // last processed write time
            public long LastSize;        // last processed file size
            public int ContentHash;     // quick text hash to detect same-content rewrites

            public int LastLevel;       // last parsed Level we accepted
            public long LastExp;         // last parsed Exp we accepted

            public DateTime PendingUntilUtc; // debounce target for this file
        }

        private readonly Dictionary<string, Seen> _seen =
            new(StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------------
        // UNITY LIFECYCLE
        // ---------------------------------------------------------------------

        private void Awake()
        {
            Log = this.Logger;
            Log.LogInfo("[ValhATLYSS] Awake");

            // Locate the profile root (…/ATLYSS_Data/profileCollections)
            _profilesRoot = GetProfilesRoot();
            if (string.IsNullOrEmpty(_profilesRoot))
            {
                Log.LogWarning("[ValhATLYSS] Could not locate profileCollections path. Mod will idle.");
            }
            else
            {
                Log.LogInfo("[ValhATLYSS] Profiles root: " + _profilesRoot);
                TryEnableWatcher(_profilesRoot);

                // Optional discovery, useful to verify we’re looking at the right files
                try
                {
                    var files = Directory.GetFiles(_profilesRoot, "atl_characterProfile_*", SearchOption.TopDirectoryOnly);
                    Log?.LogInfo($"[ValhATLYSS] Profiles discovered: {files.Length}");
                    if (VerboseWatcherLogs)
                        foreach (var f in files) Log?.LogInfo($"[ValhATLYSS] Profile: {f}");
                }
                catch (Exception ex)
                {
                    Log?.LogDebug($"[ValhATLYSS] Profile discovery failed: {ex.Message}");
                }
            }

            StartCoroutine(Bootstrap());
        }

        /// <summary>Delayed start: prepare vault, start cap enforcer, start poller, and initial reconcile.</summary>
        private IEnumerator Bootstrap()
        {
            yield return new WaitForSeconds(2.0f);
            _bootstrapped = true;

            // Ensure vault exists (plain-text format) under profileCollections/ValhATLYSS/vault
            HoddKista.EnsureReady(Log);

            // NEW: persistent cap enforcement (host-only)
            StartCoroutine(EnforceServerCapForever());

            // Poller safety net (same reconcile logic as watcher)
            StartCoroutine(ProfilePoller());

            // One-shot reconcile of the most recent non-bak profile (nice on boot)
            if (TryGetMostRecentProfile(out var path))
            {
                Log.LogInfo("[ValhATLYSS] Initial reconcile of most recent profile: " + path);
                TryReconcileProfile(path);
            }
            else
            {
                Log.LogInfo("[ValhATLYSS] No recent non-bak profile found to reconcile at bootstrap.");
            }
        }

        // ---------------------------------------------------------------------
        // HOST-ONLY CAP ENFORCER (continuous)
        // ---------------------------------------------------------------------
        // Why continuous? In some setups the cap gets reset late by base code or other mods.
        // This loop checks once per second; if the value isn’t what we want, it fixes it.
        // On remote servers (not host), we do nothing.

        private IEnumerator EnforceServerCapForever()
        {
            int lastAnnounced = -1; // avoid log spam for “OK” messages
            int desired = DesiredCap < 32 ? 32 : DesiredCap;

            // Give the scene a beat to spin up.
            yield return new WaitForSeconds(1.0f);

            while (true)
            {
                try
                {
                    // Host check (Mirror host or authoritative main player)
                    bool isHost = false;
                    try { isHost = NetworkServer.active; } catch { }
                    if (!isHost && Player._mainPlayer != null) isHost = Player._mainPlayer.isServer;

                    // StatLogics is where the cap lives
                    var stat = GameManager._current != null ? GameManager._current._statLogics : null;

                    if (isHost && stat != null)
                    {
                        if (stat._maxMainLevel != desired)
                        {
                            int before = stat._maxMainLevel;
                            stat._maxMainLevel = desired;
                            Logger?.LogInfo($"[ValhATLYSS] Server level cap set: {before} → {stat._maxMainLevel} (enforce)");
                            lastAnnounced = stat._maxMainLevel;
                        }
                        else if (lastAnnounced != desired)
                        {
                            Logger?.LogInfo($"[ValhATLYSS] Server level cap OK: {stat._maxMainLevel} (>= {desired})");
                            lastAnnounced = desired;
                        }
                    }
                    // Not host or not ready? loop quietly and try again.
                }
                catch (Exception e)
                {
                    Logger?.LogDebug("[ValhATLYSS] Cap enforcer tick failed: " + e.Message);
                }

                yield return new WaitForSeconds(1.0f);
            }
        }

        // ---------------------------------------------------------------------
        // FILE WATCHER (debounced) + helpers
        // ---------------------------------------------------------------------

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

        private void OnProfileChanged(object sender, FileSystemEventArgs e) => QueueProfileCheck(e.FullPath);
        private void OnProfileRenamed(object sender, RenamedEventArgs e) => QueueProfileCheck(e.FullPath);

        private static bool IsBackupPath(string path) =>
            path.EndsWith("_bak", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).EndsWith("_bak", StringComparison.OrdinalIgnoreCase);

        /// <summary>Coalesce FS bursts for a path and schedule a single read when quiet.</summary>
        private void QueueProfileCheck(string path)
        {
            if (!_bootstrapped || string.IsNullOrEmpty(path))
                return;

            if (IsBackupPath(path))
            {
                if (VerboseWatcherLogs)
                    Log?.LogInfo($"[ValhATLYSS] Watcher: ignoring backup file {Path.GetFileName(path)}");
                return;
            }

            if (!_seen.TryGetValue(path, out var seen))
                _seen[path] = seen = new Seen();

            seen.PendingUntilUtc = DateTime.UtcNow.AddMilliseconds(ReconcileDebounceMs);

            if (VerboseWatcherLogs)
                Log?.LogInfo($"[ValhATLYSS] Watcher: queued {Path.GetFileName(path)} until {seen.PendingUntilUtc:HH:mm:ss.fff}Z");

            StartCoroutine(CoalesceAndProcess(path));
        }

        /// <summary>
        /// After the quiet window, read the file once. If we can parse Level/Exp, always
        /// run reconcile (update vault if better, restore disk if regressed).
        /// </summary>
        private IEnumerator CoalesceAndProcess(string path)
        {
            while (_seen.TryGetValue(path, out var s) && DateTime.UtcNow < s.PendingUntilUtc)
                yield return null;

            if (VerboseWatcherLogs)
                Log?.LogInfo($"[ValhATLYSS] Watcher: processing {Path.GetFileName(path)} after quiet window");

            const int maxTries = 10;
            for (int i = 0; i < maxTries; i++)
            {
                if (TryReadAllText(path, out var text, out var info))
                {
                    if (!_seen.TryGetValue(path, out var last))
                        _seen[path] = last = new Seen();

                    int hash = StableHash(text);

                    // Skip if bytes are identical to the last processed snapshot
                    if (info.LastWriteTimeUtc == last.LastWriteUtc &&
                        info.Length == last.LastSize &&
                        hash == last.ContentHash)
                    {
                        if (VerboseWatcherLogs)
                            Log?.LogInfo($"[ValhATLYSS] Watcher: no FS/hash change for {Path.GetFileName(path)}");
                        yield break;
                    }

                    // Parse Level/Exp from the actual file (not from 'text' only), tolerant to formats
                    if (!KappaSlot.TryReadProfileStats(path, out var disk, Log))
                    {
                        if (VerboseWatcherLogs)
                            Log?.LogInfo($"[ValhATLYSS] Watcher: could not read Level/Exp from {Path.GetFileName(path)}");

                        last.LastWriteUtc = info.LastWriteTimeUtc;
                        last.LastSize = info.Length;
                        last.ContentHash = hash;
                        yield break;
                    }

                    if (VerboseWatcherLogs)
                        Log?.LogInfo($"[ValhATLYSS] Watcher: parsed Level={disk.Level} Exp={disk.Exp} (prev L={last.LastLevel} E={last.LastExp})");

                    // Always reconcile when parsed. The vault decides whether to restore or update.
                    var result = HoddKista.Reconcile(path, disk, Log);
                    if (result == -1) Log?.LogInfo("[ValhATLYSS] Anti-regression: disk restored from vault.");
                    else if (result == +1) Log?.LogInfo("[ValhATLYSS] Anti-regression: vault updated from disk.");
                    else if (VerboseWatcherLogs) Log?.LogInfo("[ValhATLYSS] Anti-regression: no change needed.");

                    // Update snapshot to the now-seen content/state
                    last.LastWriteUtc = info.LastWriteTimeUtc;
                    last.LastSize = info.Length;
                    last.ContentHash = hash;
                    last.LastLevel = disk.Level;
                    last.LastExp = disk.Exp;

                    yield break;
                }

                // File still being written—retry shortly.
                yield return new WaitForSeconds(0.1f);
            }

            Log?.LogDebug($"[ValhATLYSS] Skipped profile check (file busy): {path}");
        }

        // ---------------------------------------------------------------------
        // POLLER SAFETY NET (same reconcile; ignores *_bak)
        // ---------------------------------------------------------------------

        private IEnumerator ProfilePoller()
        {
            yield return new WaitForSeconds(3f);

            while (true)
            {
                if (!string.IsNullOrEmpty(_profilesRoot))
                {
                    try
                    {
                        if (TryGetMostRecentProfile(out var path) && File.Exists(path))
                        {
                            if (TryReadAllText(path, out var text, out var info))
                            {
                                if (!_seen.TryGetValue(path, out var last))
                                    _seen[path] = last = new Seen();

                                int hash = StableHash(text);

                                // Only proceed when file changed (by FS metadata or hash)
                                if (info.LastWriteTimeUtc != last.LastWriteUtc ||
                                    info.Length != last.LastSize ||
                                    hash != last.ContentHash)
                                {
                                    if (KappaSlot.TryReadProfileStats(path, out var disk, Log))
                                    {
                                        if (VerboseWatcherLogs)
                                            Log?.LogInfo($"[ValhATLYSS] Poller: parsed Level={disk.Level} Exp={disk.Exp} (prev L={last.LastLevel} E={last.LastExp}) from {Path.GetFileName(path)}");

                                        var result = HoddKista.Reconcile(path, disk, Log);
                                        if (result == -1) Log?.LogInfo("[ValhATLYSS] Anti-regression: disk restored from vault. (poller)");
                                        else if (result == +1) Log?.LogInfo("[ValhATLYSS] Anti-regression: vault updated from disk. (poller)");
                                        else if (VerboseWatcherLogs) Log?.LogInfo("[ValhATLYSS] Anti-regression: no change needed. (poller)");

                                        last.LastLevel = disk.Level;
                                        last.LastExp = disk.Exp;
                                    }
                                    else if (VerboseWatcherLogs)
                                    {
                                        Log?.LogInfo($"[ValhATLYSS] Poller: could not read Level/Exp in {Path.GetFileName(path)}");
                                    }

                                    last.LastWriteUtc = info.LastWriteTimeUtc;
                                    last.LastSize = info.Length;
                                    last.ContentHash = hash;
                                }
                                else if (VerboseWatcherLogs)
                                {
                                    Log?.LogDebug($"[ValhATLYSS] Poller: no FS/hash change for {Path.GetFileName(path)}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log?.LogDebug($"[ValhATLYSS] Poller error: {ex.Message}");
                    }
                }

                yield return new WaitForSeconds(PollIntervalSeconds);
            }
        }

        // ---------------------------------------------------------------------
        // FILE HELPERS
        // ---------------------------------------------------------------------

        private static bool TryReadAllText(string path, out string text, out FileInfo info)
        {
            text = null; info = null;
            try
            {
                info = new FileInfo(path);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
                return true;
            }
            catch { return false; }
        }

        /// <summary>In-process, tiny hash for content-change detection (not persisted).</summary>
        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                if (!string.IsNullOrEmpty(s))
                    for (int i = 0; i < s.Length; i++)
                        h = (h * 31) + s[i];
                return h;
            }
        }

        // ---------------------------------------------------------------------
        // ONE-SHOT RECONCILE USED AT BOOT
        // ---------------------------------------------------------------------

        private void TryReconcileProfile(string profilePath)
        {
            try
            {
                if (string.IsNullOrEmpty(profilePath) || IsBackupPath(profilePath))
                {
                    Log?.LogInfo("[ValhATLYSS] Reconcile skipped: path null or backup file.");
                    return;
                }

                if (!File.Exists(profilePath))
                {
                    Log?.LogWarning("[ValhATLYSS] Reconcile skipped; path missing: " + profilePath);
                    return;
                }

                if (!KappaSlot.TryReadProfileStats(profilePath, out var disk, Log))
                {
                    Log?.LogWarning("[ValhATLYSS] Could not read disk stats for: " + profilePath);
                    return;
                }

                var result = HoddKista.Reconcile(profilePath, disk, Log);
                if (result == -1) Log?.LogInfo("[ValhATLYSS] Anti-regression: disk restored from vault.");
                else if (result == +1) Log?.LogInfo("[ValhATLYSS] Anti-regression: vault updated from disk.");
                else Log?.LogInfo("[ValhATLYSS] No reconcile change necessary.");
            }
            catch (Exception ex)
            {
                Log?.LogWarning("[ValhATLYSS] Reconcile failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------
        // PATH HELPERS
        // ---------------------------------------------------------------------

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

        /// <summary>Find most recently modified non-backup profile.</summary>
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
                    var name = Path.GetFileName(f);
                    if (name.EndsWith("_bak", StringComparison.OrdinalIgnoreCase))
                        continue; // ignore backups

                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > last) { last = t; fullPath = f; }
                }

                return fullPath != null;
            }
            catch { return false; }
        }
    }
}
