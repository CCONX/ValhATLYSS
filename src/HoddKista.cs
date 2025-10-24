/**
 * ValhATLYSS :: HoddKista.cs (vault, plain-text)
 * -----------------------------------------------------------------------------
 * ROLE
 *  - Local "vault" of last-known-good stats per profile filename
 *    (e.g., atl_characterProfile_4 → vault/atl_characterProfile_4.json).
 *
 * FORMAT (plain-text; no JSON dependency):
 *   level=<int>   // >= 1, or 0 when unknown
 *   exp=<long>    // >= 0, or -1 when unknown
 *
 * REASONS
 *  - Avoid UnityEngine.JsonUtility / System.Text.Json assembly differences.
 *  - Keep it trivial and robust.
 *
 * SAFETY
 *  - No reflection. No fingerprinting. Pure file I/O.
 *  - Vault keyed by *profile filename* (prevents cross-host bleed).
 * -----------------------------------------------------------------------------
 */

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace ValhATLYSS
{
    internal static class HoddKista
    {
        // Root like: <GameRoot>/ATLYSS_Data/profileCollections/ValhATLYSS/vault
        private static string _root;

        /// <summary>Prepare the vault directory under the game's profileCollections.</summary>
        internal static void EnsureReady(ManualLogSource log)
        {
            try
            {
                var baseDir = Plugin.GetProfilesRoot();
                if (string.IsNullOrEmpty(baseDir)) return;

                _root = Path.Combine(baseDir, "ValhATLYSS", "vault");
                Directory.CreateDirectory(_root);
                log?.LogInfo("[ValhATLYSS] Vault ready at: " + _root);
            }
            catch (Exception e)
            {
                log?.LogWarning("[ValhATLYSS] Vault setup failed: " + e.Message);
            }
        }

        private static string VaultPathForProfile(string profileFullPath)
        {
            var key = Path.GetFileName(profileFullPath) ?? "unknown_profile";
            // Keep the ".json" suffix for backwards-compat filenames, content is plain-text.
            return Path.Combine(_root ?? "", key + ".json");
        }

        // ----------------------------- Plain-text I/O -----------------------------

        private struct VaultModel
        {
            public int Level; // >=1 (0 = unknown)
            public long Exp;   // >=0 (-1 = unknown)
        }

        private static readonly Regex RxLevel = new Regex(@"(?im)^\s*level\s*=\s*(?<n>-?\d+)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex RxExp = new Regex(@"(?im)^\s*exp\s*=\s*(?<n>-?\d+)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static bool LoadVault(string vp, out VaultModel model)
        {
            model = new VaultModel { Level = 0, Exp = -1 };

            try
            {
                if (!File.Exists(vp)) return false;

                string text;
                using (var fs = new FileStream(vp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                    text = sr.ReadToEnd();

                var mL = RxLevel.Match(text);
                var mE = RxExp.Match(text);

                if (mL.Success && int.TryParse(mL.Groups["n"].Value, out var lvl)) model.Level = Math.Max(0, lvl);
                if (mE.Success && long.TryParse(mE.Groups["n"].Value, out var xp)) model.Exp = xp < 0 ? -1 : xp;

                return mL.Success || mE.Success;
            }
            catch
            {
                return false;
            }
        }

        private static void SaveVault(string vp, VaultModel model)
        {
            try
            {
                var sb = new StringBuilder(64);
                sb.Append("level=").Append(model.Level).Append('\n');
                sb.Append("exp=").Append(model.Exp).Append('\n');

                // Atomic-ish write: temp then replace.
                var tmp = vp + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                File.Copy(tmp, vp, true);
                File.Delete(tmp);
            }
            catch
            {
                // best-effort; ignore errors
            }
        }

        // ----------------------------- Reconcile core -----------------------------

        /// <summary>
        /// Compare DISK (parsed by KappaSlot) and vault; decide action:
        ///   - vault better → write back to DISK (safe token replace) and refresh vault
        ///   - disk better  → update vault
        /// Returns:
        ///   -1 → restored DISK from vault
        ///   +1 → updated vault from disk
        ///    0 → nothing changed / could not act safely
        /// </summary>
        internal static int Reconcile(string profilePath, KappaSlot.DiskStats disk, ManualLogSource log)
        {
            if (string.IsNullOrEmpty(_root)) return 0;

            var vp = VaultPathForProfile(profilePath);
            var haveVault = LoadVault(vp, out var vault);

            bool diskHasUseful = disk.HasLevel || disk.HasExp;
            bool vaultHasUseful = (vault.Level > 0) || (vault.Exp >= 0);

            if (!diskHasUseful && !vaultHasUseful)
            {
                log?.LogInfo("[ValhATLYSS] Reconcile: nothing to do (no readable stats).");
                return 0;
            }

            int diskLevel = disk.HasLevel ? disk.Level : 0;
            long diskExp = disk.HasExp ? disk.Exp : -1;
            int vaultLevel = vault.Level;
            long vaultExp = vault.Exp;

            // Decide if vault strictly “better” (higher level, or same level with higher XP)
            bool vaultBetter =
                (vaultLevel > diskLevel) ||
                (vaultLevel == diskLevel && vaultExp >= 0 && diskExp >= 0 && vaultExp > diskExp);

            if (vaultBetter)
            {
                // Only write if we know how to safely patch tokens we matched during read.
                if (disk.HasLevel || disk.HasExp)
                {
                    var toWrite = new KappaSlot.DiskStats
                    {
                        Level = (vaultLevel > 0) ? vaultLevel : diskLevel,
                        Exp = (vaultExp >= 0) ? vaultExp : diskExp,
                        LevelPatternUsed = disk.LevelPatternUsed,
                        ExpPatternUsed = disk.ExpPatternUsed
                    };

                    if (KappaSlot.TryWriteProfileStats(profilePath, toWrite, log))
                    {
                        log?.LogInfo("[ValhATLYSS] Disk was restored from vault (anti-regression).");

                        // Refresh vault to best values (keeps them monotonic)
                        SaveVault(vp, new VaultModel
                        {
                            Level = Math.Max(vaultLevel, toWrite.Level),
                            Exp = Math.Max(vaultExp, toWrite.Exp)
                        });
                        return -1;
                    }

                    log?.LogInfo("[ValhATLYSS] Vault better, but safe patch failed (pattern not found).");
                    return 0;
                }

                log?.LogInfo("[ValhATLYSS] Vault better but disk tokens not recognized; skip write.");
                return 0;
            }
            else
            {
                // Disk same or better → update vault
                var newVault = new VaultModel
                {
                    Level = Math.Max(vaultLevel, diskLevel),
                    Exp = Math.Max(vaultExp, diskExp)
                };
                SaveVault(vp, newVault);
                return +1;
            }
        }
    }
}
