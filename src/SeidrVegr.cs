/**
 * ValhATLYSS :: SeidrVegr.cs
 * ------------------------------------------------------------------------------------
 * Purpose:
 *   Placeholder for handshake or capability negotiation with a server (future use).
 *   Current build: returns false / no-ops. The mod operates entirely locally.
 *
 * Notes:
 *   - Any future networking should send capability flags only, never player stats.
 *   - Keep this file isolated so Plugin/HoddKista remain un-networked.
 * ------------------------------------------------------------------------------------
 */

namespace ValhATLYSS
{
    internal static class SeidrVegr
    {
        /// <summary>
        /// Returns whether a server-side mod is present and compatible.
        /// Current build: stubbed to false (no actual networking).
        /// </summary>
        internal static bool Handshake()
        {
            // Future idea:
            // - Send "ValhATLYSS" + protocol version + feature flags.
            // - Receive server rules (max level, exp policy) without any player identifiers.
            // For now, always false to indicate "solo local mode".
            return false;
        }
    }
}
