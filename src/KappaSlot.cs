using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace ValhATLYSS
{
    /// <summary>
    /// KappaSlot is responsible for reading/writing the *minimal* stats we care about
    /// from the game's on-disk character profile so that we can (a) back them up locally
    /// and (b) restore them if the game regresses them (e.g., when joining vanilla servers).
    ///
    /// Design goals:
    ///  - Zero reflection/fingerprinting.
    ///  - Pure file I/O. We never poke live game memory here.
    ///  - Be robust to profile format changes: match multiple likely key names and shapes.
    /// </summary>
    internal static class KappaSlot
    {
        /// <summary>
        /// The minimal stats we track for anti-regression.
        /// </summary>
        internal struct DiskStats
        {
            public int Level;          // parsed level (>= 1). 0 means "unknown"
            public long Exp;           // parsed exp (>= 0). -1 means "unknown"

            // For safe in-place restoration we also remember *how* we matched the file.
            // If we successfully matched a Level and/or Exp token, we store the exact
            // regex used so we can do a targeted replacement later.
            public string LevelPatternUsed;
            public string ExpPatternUsed;

            public bool HasLevel => Level > 0 && !string.IsNullOrEmpty(LevelPatternUsed);
            public bool HasExp => Exp >= 0 && !string.IsNullOrEmpty(ExpPatternUsed);
        }

        /// <summary>
        /// Try to parse Level/Exp from a profile text blob.
        /// Supports JSON, JSON-like, and key=value styles, case-insensitive.
        /// We purposefully *don’t* assume exact property names; we try a shortlist of
        /// plausible aliases observed across versions/mod builds.
        /// </summary>
        private static bool TryParseStatsFromText(string text, out DiskStats stats, ManualLogSource log)
        {
            stats = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Common aliases the game (or different builds) could use.
            // We search in order and take the first match we find for each field.
            string[] levelKeys = { "level", "mainLevel", "playerLevel", "lvl" };
            string[] expKeys = { "exp", "experience", "mainExp", "xp", "totalExp" };

            // Generic JSON-style:  "level": 37   (with optional quotes/spaces)
            // Generic kv-style:    level=37
            // Also tolerate trailing commas.
            string MakeNumberPattern(string key) =>
                $"(?i)(?:[\"']?{Regex.Escape(key)}[\"']?\\s*[:=]\\s*)(?<num>-?\\d+)";

            // Try levels
            foreach (var k in levelKeys)
            {
                var pat = MakeNumberPattern(k);
                var m = Regex.Match(text, pat, RegexOptions.CultureInvariant);
                if (m.Success && int.TryParse(m.Groups["num"].Value, out var lvl) && lvl > 0)
                {
                    stats.Level = lvl;
                    stats.LevelPatternUsed = pat;
                    break;
                }
            }

            // Try exp
            foreach (var k in expKeys)
            {
                var pat = MakeNumberPattern(k);
                var m = Regex.Match(text, pat, RegexOptions.CultureInvariant);
                if (m.Success && long.TryParse(m.Groups["num"].Value, out var xp) && xp >= 0)
                {
                    stats.Exp = xp;
                    stats.ExpPatternUsed = pat;
                    break;
                }
            }

            // If neither field matched, parsing failed.
            var ok = stats.HasLevel || stats.HasExp;
            if (!ok)
                log?.LogInfo("[ValhATLYSS] Parser: no Level/Exp tokens matched. Profile format likely changed.");

            return ok;
        }

        /// <summary>
        /// Reads the profile file and extracts stats. Returns false if we can’t locate either Level or Exp.
        /// </summary>
        internal static bool TryReadProfileStats(string path, out DiskStats stats, ManualLogSource log)
        {
            stats = default;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                log?.LogWarning("[ValhATLYSS] Parser: file not found: " + path);
                return false;
            }

            string text;
            try
            {
                // Open with shared read; the game may still have the file open.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                    text = sr.ReadToEnd();
            }
            catch (Exception e)
            {
                log?.LogWarning("[ValhATLYSS] Parser: read failed: " + e.Message);
                return false;
            }

            if (!TryParseStatsFromText(text, out stats, log))
                return false;

            // Sanity floor: some files may carry level 0 or negative numbers while creating a new character.
            if (stats.Level <= 0) stats.Level = 0;
            if (stats.Exp < 0) stats.Exp = -1;

            return stats.HasLevel || stats.HasExp;
        }

        /// <summary>
        /// Given a profile file and the *target* stats we want to ensure on disk,
        /// tries to update the file *in place* by replacing only the matched tokens.
        /// We refuse to write if we didn’t parse/remember how to safely patch the text.
        /// </summary>
        internal static bool TryWriteProfileStats(string path, DiskStats target, ManualLogSource log)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            string text;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                    text = sr.ReadToEnd();
            }
            catch (Exception e)
            {
                log?.LogWarning("[ValhATLYSS] Writer: read failed: " + e.Message);
                return false;
            }

            bool changed = false;

            // Replace Level token if we know how we matched it originally.
            if (target.HasLevel)
            {
                var pat = new Regex(target.LevelPatternUsed, RegexOptions.CultureInvariant);
                var m = pat.Match(text);
                if (m.Success)
                {
                    // Keep the key and delimiter; replace just the number group.
                    var replaced = pat.Replace(text, match =>
                    {
                        var prefix = match.Value.Substring(0, match.Value.LastIndexOf(match.Groups["num"].Value, StringComparison.Ordinal));
                        return prefix + target.Level.ToString();
                    }, 1);
                    if (!ReferenceEquals(replaced, text))
                    {
                        text = replaced;
                        changed = true;
                    }
                }
                else
                {
                    log?.LogInfo("[ValhATLYSS] Writer: Level pattern no longer matches; skip safe write.");
                }
            }

            // Replace Exp token if we know how we matched it originally.
            if (target.HasExp)
            {
                var pat = new Regex(target.ExpPatternUsed, RegexOptions.CultureInvariant);
                var m = pat.Match(text);
                if (m.Success)
                {
                    var replaced = pat.Replace(text, match =>
                    {
                        var prefix = match.Value.Substring(0, match.Value.LastIndexOf(match.Groups["num"].Value, StringComparison.Ordinal));
                        return prefix + target.Exp.ToString();
                    }, 1);
                    if (!ReferenceEquals(replaced, text))
                    {
                        text = replaced;
                        changed = true;
                    }
                }
                else
                {
                    log?.LogInfo("[ValhATLYSS] Writer: Exp pattern no longer matches; skip safe write.");
                }
            }

            if (!changed) return false;

            try
            {
                // Write back atomically: temp file then replace to minimize corruption risk.
                var tmp = path + ".valh_tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                File.Copy(tmp, path, true);
                File.Delete(tmp);
                return true;
            }
            catch (Exception e)
            {
                log?.LogWarning("[ValhATLYSS] Writer: write failed: " + e.Message);
                return false;
            }
        }
    }
}
