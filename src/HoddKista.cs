/**
 * ValhATLYSS :: HoddKista.cs
 * -----------------------------------------------------------------------------
 * Purpose:
 *   Local "vault" snapshot for player stats (level, EXP, attributes).
 *   Reconcile rules:
 *     - If DISK regressed vs VAULT → restore DISK from VAULT (write file).
 *     - If DISK progressed vs VAULT → update VAULT from DISK.
 *
 * This build:
 *   - NO fingerprinting. 'owner' is retained for compatibility but written blank.
 *   - NO live apply. We do not touch runtime Player objects at all.
 * -----------------------------------------------------------------------------
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
        private static readonly string VaultDir = Path.Combine(Paths.ConfigPath, "ValhATLYSS", "vault");

        internal struct VaultStats
        {
            public string owner;   // legacy key; blank on new writes
            public string nick;
            public string classId;
            public int level;
            public int exp;
            public int str, dex, mind, vit;
        }

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
        /// Compare disk vs vault and reconcile. Returns:
        ///  -1 : disk restored from vault
        ///   0 : no changes
        ///  +1 : vault updated from disk OR created
        /// </summary>
        internal static int Reconcile(string profilePath, KappaSlot.DiskStats disk, ManualLogSource log)
        {
            if (!KappaSlot.ParseSlotIndexFromPath(profilePath, out var slot))
            {
                log?.LogWarning("[ValhATLYSS] Reconcile: cannot parse slot from " + profilePath);
                return 0;
            }

            Directory.CreateDirectory(VaultDir);
            var vaultPath = GetVaultPathForSlot(slot);

            // Create vault if none exists
            if (!TryReadVault(vaultPath, out var vault, log))
            {
                var created = ToVault(disk); // owner blank
                WriteVault(vaultPath, created, log);
                log?.LogInfo("[ValhATLYSS] Vault created for slot " + slot);
                return +1;
            }

            // Anti-regression: higher level wins; if equal, higher exp wins
            bool diskBehindVault =
                (disk.Level < vault.level) ||
                (disk.Level == vault.level && disk.Exp < vault.exp);

            if (diskBehindVault)
            {
                // Restore DISK from VAULT (file write only)
                if (KappaSlot.TryWriteDiskStats(profilePath, vault.level, vault.exp, vault.str, vault.dex, vault.mind, vault.vit, log))
                {
                    log?.LogInfo("[ValhATLYSS] Restored DISK from VAULT.");
                    return -1;
                }

                log?.LogWarning("[ValhATLYSS] Failed to restore disk from vault.");
                return 0;
            }

            // If disk progressed, update vault snapshot
            bool diskAheadOfVault =
                (disk.Level > vault.level) ||
                (disk.Level == vault.level && disk.Exp > vault.exp);

            if (diskAheadOfVault)
            {
                var newer = ToVault(disk); // owner blank
                WriteVault(vaultPath, newer, log);
                log?.LogInfo("[ValhATLYSS] Updated VAULT from DISK.");
                return +1;
            }

            return 0;
        }

        private static string GetVaultPathForSlot(int slot) =>
            Path.Combine(VaultDir, $"va_charProf_{slot}.json");

        private static VaultStats ToVault(KappaSlot.DiskStats d)
        {
            VaultStats v;
            v.owner = "";                   // intentionally blank (no fingerprinting)
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
                        case "owner": owner = val; break; // legacy; ignored in logic
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

        private static void WriteVault(string path, VaultStats v, ManualLogSource log)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# ValhATLYSS vault");
                sb.AppendLine("owner=" + (v.owner ?? "")); // blank in new writes
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
    }
}
