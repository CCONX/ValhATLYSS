/**
 * ValhATLYSS :: Ristir.cs
 * ------------------------------------------------------------------------------------
 * Purpose:
 *   Logging helpers and general utilities around EXP and level progression.
 *   May include future expansion for curves, debug printing, or balance prototyping.
 *
 * Notes:
 *   - Keep this file lean; it primarily exists to keep Plugin.cs focused.
 * ------------------------------------------------------------------------------------
 */

using System;
using BepInEx.Logging;

namespace ValhATLYSS
{
    internal static class Ristir
    {
        /// <summary>
        /// Print a diagnostic line with a standard prefix for easy grepping in logs.
        /// </summary>
        internal static void Print(ManualLogSource log, string message)
        {
            try
            {
                log?.LogInfo("[ValhATLYSS] " + message);
            }
            catch { /* logging should never throw */ }
        }

        /// <summary>
        /// Convenience clamp to ensure level never exceeds desired bounds.
        /// </summary>
        internal static int ClampLevel(int level, int min = 1, int max = 64)
        {
            if (level < min) return min;
            if (level > max) return max;
            return level;
        }

        /// <summary>
        /// Simple EXP comparator to decide whether a player progressed or regressed.
        /// Returns +1 if (L1,E1) > (L0,E0), -1 if less, 0 if equal.
        /// </summary>
        internal static int CompareProgress(int level0, int exp0, int level1, int exp1)
        {
            if (level1 > level0) return +1;
            if (level1 < level0) return -1;
            if (exp1 > exp0) return +1;
            if (exp1 < exp0) return -1;
            return 0;
        }
    }
}
