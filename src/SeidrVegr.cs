/**
 * ValhATLYSS :: SeidrVegr.cs
 * -----------------------------------------------------------------------------
 * Purpose:
 *   Placeholder for any future server capability checks.
 *   Current build: no networking; always returns false.
 * -----------------------------------------------------------------------------
 */

namespace ValhATLYSS
{
    internal static class SeidrVegr
    {
        internal static bool Handshake()
        {
            // Future: only ever exchange capability flags, never player stats.
            return false;
        }
    }
}
