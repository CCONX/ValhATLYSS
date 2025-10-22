/**
 * ValhATLYSS :: KappaSlot.cs
 * ------------------------------------------------------------------------------------
 * Purpose:
 *   File I/O for ATLYSS character profile files (atl_characterProfile_*).
 *   - Parse slot index from file path/name.
 *   - Read current disk stats (level, EXP, attributes).
 *   - Write updated stats back to disk in the game’s expected format.
 *
 * Notes:
 *   - The exact format of the profile file is assumed from current builds.
 *   - All reads/writes are local and synchronous.
 *   - TryReadDiskStats and TryWriteDiskStats are used by Plugin/HoddKista.
 * ------------------------------------------------------------------------------------
 */

using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace ValhATLYSS
{
    internal static class KappaSlot
    {
        // Regex to extract slot index from typical profile filenames:
        // e.g., atl_characterProfile_3, atl_characterProfile_12, etc.
        private static readonly Regex SlotRegex =
            new Regex(@"atl_characterProfile_(\d+)$", RegexOptions.Compiled);

        /// <summary>
        /// Container for stats read from disk.
        /// </summary>
        internal struct DiskStats
        {
            public string Nick;     // optional nickname
            public string ClassId;  // optional class id
            public int Level;
            public int Exp;
            public int Str, Dex, Mind, Vit;
        }

        /// <summary>
        /// Given a profile path, attempt to parse the slot index from its filename.
        /// Returns true and outputs 'slot' if recognized.
        /// </summary>
        internal static bool ParseSlotIndexFromPath(string profilePath, out int slot)
        {
            slot = 0;
            try
            {
                var name = Path.GetFileName(profilePath);
                var m = SlotRegex.Match(name);
                if (m.Success && int.TryParse(m.Groups[1].Value, out slot))
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Read the disk stats for the given profile path.
        /// This implementation mirrors the current file format expectations.
        /// </summary>
        internal static bool TryReadDiskStats(string profilePath, out DiskStats stats, ManualLogSource log)
        {
            stats = default;
            try
            {
                if (!File.Exists(profilePath)) return false;

                // Basic parser for key/value-like content with known keys.
                string nick = "", cls = "";
                int level = 0, exp = 0, str = 0, dex = 0, mind = 0, vit = 0;

                var lines = File.ReadAllLines(profilePath);
                foreach (var raw in lines)
                {
                    var L = raw?.Trim();
                    if (string.IsNullOrEmpty(L) || L.StartsWith("#")) continue;

                    // naive split; adjust if real format differs
                    var eq = L.IndexOf('=');
                    if (eq <= 0) continue;

                    var key = L.Substring(0, eq).Trim();
                    var val = L.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "nick": nick = val; break;
                        case "class": cls = val; break;

                        case "level": int.TryParse(val, out level); break;
                        case "exp": int.TryParse(val, out exp); break;
                        case "str": int.TryParse(val, out str); break;
                        case "dex": int.TryParse(val, out dex); break;
                        case "mind": int.TryParse(val, out mind); break;
                        case "vit": int.TryParse(val, out vit); break;
                    }
                }

                stats = new DiskStats
                {
                    Nick = nick ?? "",
                    ClassId = cls ?? "",
                    Level = level,
                    Exp = exp,
                    Str = str,
                    Dex = dex,
                    Mind = mind,
                    Vit = vit
                };

                return true;
            }
            catch (Exception e)
            {
                log?.LogWarning("[ValhATLYSS] TryReadDiskStats failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Write updated stats back to the profile file.
        /// Only called when the anti-regression rule decides to restore disk from vault.
        /// </summary>
        internal static bool TryWriteDiskStats(
            string profilePath, int level, int exp,
            int str, int dex, int mind, int vit,
            ManualLogSource log)
        {
            try
            {
                if (!File.Exists(profilePath))
                {
                    log?.LogWarning("[ValhATLYSS] WriteDiskStats skipped; profile missing: " + profilePath);
                    return false;
                }

                // Load, mutate, and write back preserving other fields if any.
                var lines = File.ReadAllLines(profilePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    var raw = lines[i];
                    if (string.IsNullOrEmpty(raw)) continue;
                    var L = raw.TrimStart();

                    // Update only known numeric keys; leave others untouched.
                    if (L.StartsWith("level=")) lines[i] = "level=" + level;
                    else if (L.StartsWith("exp=")) lines[i] = "exp=" + exp;
                    else if (L.StartsWith("str=")) lines[i] = "str=" + str;
                    else if (L.StartsWith("dex=")) lines[i] = "dex=" + dex;
                    else if (L.StartsWith("mind=")) lines[i] = "mind=" + mind;
                    else if (L.StartsWith("vit=")) lines[i] = "vit=" + vit;
                }

                File.WriteAllLines(profilePath, lines);
                return true;
            }
            catch (Exception e)
            {
                log?.LogWarning("[ValhATLYSS] TryWriteDiskStats failed: " + e.Message);
                return false;
            }
        }
    }
}
