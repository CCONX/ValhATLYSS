/**
 * ValhATLYSS :: Ristir.cs
 * -----------------------------------------------------------------------------
 * Purpose:
 *   General utilities (logging, simple math helpers) kept separate from the
 *   watcher/reconcile logic to keep those files focused.
 *
 * This build:
 *   - No Player references; no live applies.
 * -----------------------------------------------------------------------------
 */

using System;
using BepInEx.Logging;

namespace ValhATLYSS
{
    internal static class Ristir
    {
        internal static void Print(ManualLogSource log, string message)
        {
            try { log?.LogInfo("[ValhATLYSS] " + message); } catch { /* never throw */ }
        }

        internal static int ClampLevel(int level, int min = 1, int max = 64)
        {
            if (level < min) return min;
            if (level > max) return max;
            return level;
        }

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
