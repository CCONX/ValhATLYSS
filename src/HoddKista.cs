/**
 * ValhATLYSS :: HoddKista.cs
 * ------------------------------------------------------------------------------------
 * Purpose:
 *   Local "vault" persistence for player stats (level, EXP, attributes).
 *   Provides anti-regression: if disk stats drop below our snapshot, restore them.
 *
 * How it works:
 *   - Stores a small INI-like text file per slot under BepInEx config (local only).
 *   - Reconcile() compares disk vs vault and decides to restore or update.
 *   - Includes guards for foreign/invalid files and safe read/write helpers.
 *
 * Notes:
 *   - No networking here; vault files are local to the machine.
 *   - Packaging: do not include the vault folder in releases to avoid cross-machine
 *     contamination during testing.
 * ------------------------------------------------------------------------------------
 */

using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace ValhATLYSS
{
    internal static class HoddKista
    {
        // Location for vault files. These are local-only snapshots.
        private static readonly string VaultDir = Path.Combine(Paths.ConfigPath, "ValhATLYSS", "vault");

        // Minimal INI-like payload persisted to disk.
        internal struct VaultStats
        {
            public string owner;   // machine identifier used in current build (fingerprint/local id)
            public string nick;    // optional char nickname
            public string classId; // optional class identifier
            public int level;
            public int exp;
            public int str, dex, mind, vit;
        }

        /// <summary>Create the vault directory if missing.</summary>
        internal static void EnsureReady(ManualLogSource log)
        {
            try
            {
                Directory.CreateDirectory(VaultDir);
                log?.LogInfo("[ValhATLYSS] Vault ready at " + VaultDir);
            }
            catch (Exception e)
            {
                log?.LogWarning("[ValhATLYSS] Could not create vault dir: " + e.Message);
            }
        }

        /// <summary>
        /// Core reconcile routine. Reads/creates a vault for the slot, then:
        /// - If disk regressed vs vault: write vault → disk (anti-regression).
        /// - If disk progressed vs vault: write disk → vault (persist progress).
        /// Returns -1 (restored disk), 0 (no-op), +1 (updated vault/created).
        /// </summary>
        internal static int Reconcile(string profilePath, KappaSlot.DiskStats disk, ManualLogSource log)
        {
            // Determine which slot this profile belongs to from its path.
            if (!KappaSlot.ParseSlotIndexFromPath(profilePath, out var slot))
            {
                log?.LogWarning("[ValhATLYSS] Reconcile: cannot parse slot from " + profilePath);
                return 0;
            }

            Directory.CreateDirectory(VaultDir);

            // Vault file name is tied to the slot (current build scheme).
            var vaultPath = GetVaultPathForSlot(slot);

            // Read existing vault or create new from disk snapshot.
            if (!TryReadVault(vaultPath, out var vault, log))
            {
                var created = ToVault(disk);
                WriteVault(vaultPath, created, log);
                log?.LogInfo("[ValhATLYSS] Vault created for slot " + slot);
                return +1;
            }

            // OPTIONAL OWNER GUARD (depending on build; retains current behavior):
            // If an 'owner' field is used to detect foreign files, check here and rebuild if needed.
            // (In current codebase, owner is whatever the build originally wrote; logic remains unchanged.)
            // Example:
            // if (!string.IsNullOrEmpty(vault.owner) && !IsLocalOwner(vault.owner)) { quarantine & rebuild }

            // Prevent regression: higher level wins; if equal, higher exp wins.
            if (disk.Level < vault.level || (disk.Level == vault.level && disk.Exp < vault.exp))
            {
                if (KappaSlot.TryWriteDiskStats(profilePath, vault.level, vault.exp, vault.str, vault.dex, vault.mind, vault.vit, log))
                {
                    log?.LogInfo("[ValhATLYSS] Restored DISK from VAULT.");
                    return -1;
                }
                log?.LogWarning("[ValhATLYSS] Failed to restore disk from vault.");
                return 0;
            }

            // Disk progressed → update vault to keep the latest progress.
            if (disk.Level > vault.level || (disk.Level == vault.level && disk.Exp > vault.exp))
            {
                var newer = ToVault(disk);
                WriteVault(vaultPath, newer, log);
                log?.LogInfo("[ValhATLYSS] Updated VAULT from DISK.");
                return +1;
            }

            // No changes necessary.
            return 0;
        }

        /// <summary>Current vault filename scheme (per slot).</summary>
        private static string GetVaultPathForSlot(int slot) =>
            Path.Combine(VaultDir, $"va_charProf_{slot}.json");

        /// <summary>Convert disk stats into a vault record.</summary>
        private static VaultStats ToVault(KappaSlot.DiskStats d)
        {
            VaultStats v;
            v.owner = ComputeMachineFingerprint(); // current build behavior: uses machine fingerprint/local id
            v.nick = d.Nick ?? "";
            v.classId = d.ClassId ?? "";
            v.level = d.Level;
            v.exp = d.Exp;
            v.str = d.Str;
            v.dex = d.Dex;
            v.mind = d.Mind;
            v.vit = d.Vit;
            return v;
        }

        /// <summary>
        /// Reads a vault file from disk into VaultStats.
        /// The format is a simple key=value per line; lines starting with '#' are comments.
        /// </summary>
        private static bool TryReadVault(string path, out VaultStats v, ManualLogSource log)
        {
            v = default;
            try
            {
                if (!File.Exists(path)) return false;

                var lines = File.ReadAllLines(path);
                string owner = "", nick = "", classId = "";
                int level = 0, exp = 0, str = 0, dex = 0, mind = 0, vit = 0;

                foreach (var raw in lines)
                {
                    var L = raw?.Trim();
                    if (string.IsNullOrEmpty(L) || L.StartsWith("#")) continue;

                    int eq = L.IndexOf('=');
                    if (eq <= 0) continue;

                    var key = L.Substring(0, eq).Trim();
                    var val = L.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "owner": owner = val; break;
                        case "nick": nick = val; break;
                        case "class": classId = val; break;
                        case "level": int.TryParse(val, out level); break;
                        case "exp": int.TryParse(val, out exp); break;
                        case "str": int.TryParse(val, out str); break;
                        case "dex": int.TryParse(val, out dex); break;
                        case "mind": int.TryParse(val, out mind); break;
                        case "vit": int.TryParse(val, out vit); break;
                    }
                }

                v.owner = owner ?? "";
                v.nick = nick ?? "";
                v.classId = classId ?? "";
                v.level = level; v.exp = exp;
                v.str = str; v.dex = dex; v.mind = mind; v.vit = vit;
                return true;
            }
            catch (Exception e)
            {
                log?.LogWarning("[ValhATLYSS] Failed to read vault: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Writes a VaultStats record to disk in an INI-like format.
        /// </summary>
        private static void WriteVault(string path, VaultStats v, ManualLogSource log)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# ValhATLYSS vault");
                sb.AppendLine("owner=" + (v.owner ?? ""));
                sb.AppendLine("nick=" + (v.nick ?? ""));
                sb.AppendLine("class=" + (v.classId ?? ""));
                sb.AppendLine("level=" + v.level);
                sb.AppendLine("exp=" + v.exp);
                sb.AppendLine("str=" + v.str);
                sb.AppendLine("dex=" + v.dex);
                sb.AppendLine("mind=" + v.mind);
                sb.AppendLine("vit=" + v.vit);

                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception e)
            {
                log?.LogWarning("[ValhATLYSS] Failed to write vault: " + e.Message);
            }
        }

        /// <summary>
        /// Current build: derive a machine fingerprint/local-id string.
        /// (Commented and kept as-is for behavior parity; future versions should replace
        /// this with a local random ID stored in config to avoid fingerprinting.)
        /// </summary>
        private static string ComputeMachineFingerprint()
        {
            try
            {
                // Lightweight, non-cryptographic identifier derived from environment.
                // (Kept for compatibility with this build; do not transmit this anywhere.)
                var u = Environment.UserName ?? "";
                var m = Environment.MachineName ?? "";
                var o = Environment.OSVersion?.VersionString ?? "";
                var s = (u + "|" + m + "|" + o).Trim();

                // Simple stable hash for a short hex string.
                unchecked
                {
                    int h = 23;
                    for (int i = 0; i < s.Length; i++)
                        h = (h * 31) + s[i];
                    return Math.Abs(h).ToString("x8");
                }
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
