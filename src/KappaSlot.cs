/**
 * ValhATLYSS :: KappaSlot.cs
 * -----------------------------------------------------------------------------
 * Purpose:
 *   Read/write ATLYSS profile files (atl_characterProfile_*).
 *   - Parse slot index from path
 *   - Load disk stats (nick/class/level/exp/attributes)
 *   - Write updated numeric stats back to file
 * -----------------------------------------------------------------------------
 */

using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace ValhATLYSS
{
    internal static class KappaSlot
    {
        private static readonly Regex SlotRegex =
            new Regex(@"atl_characterProfile_(\d+)$", RegexOptions.Compiled);

        internal struct DiskStats
        {
            public string Nick;
            public string ClassId;
            public int Level;
            public int Exp;
            public int Str, Dex, Mind, Vit;
        }

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

        internal static bool TryReadDiskStats(string profilePath, out DiskStats stats, ManualLogSource log)
        {
            stats = default;
            try
            {
                if (!File.Exists(profilePath)) return false;

                string nick = "", cls = "";
                int level = 0, exp = 0, str = 0, dex = 0, mind = 0, vit = 0;

                var lines = File.ReadAllLines(profilePath);
                foreach (var raw in lines)
                {
                    var L = raw?.Trim();
                    if (string.IsNullOrEmpty(L) || L.StartsWith("#")) continue;

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

                var lines = File.ReadAllLines(profilePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    var raw = lines[i];
                    if (string.IsNullOrEmpty(raw)) continue;
                    var L = raw.TrimStart();

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
