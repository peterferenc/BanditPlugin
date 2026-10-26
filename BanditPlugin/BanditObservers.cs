using System;
using System.Collections.Generic;
using System.IO;
using SDG.Unturned;
using Steamworks;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin
{
    /// <summary>
    /// Players bandits do not see, and the camera that looks through their eyes.
    ///
    /// An observer is someone watching bandits rather than fighting them - running the tests, or
    /// standing in the middle of a fight to see why it went wrong. Every bandit treats one as on no
    /// side at all: never a target, never a contact, never a reason for a vehicle to wake up. It is
    /// a single check in <see cref="BanditTeams.IsHostile"/>, so the target scan, turrets, event
    /// triggers and friendly-fire test all agree. Bullets aimed at somebody else still hurt.
    ///
    /// The camera is the one vanilla's /spy uses: the server asks a player's own game client to
    /// photograph its screen and send it back. It works only on real players - a bandit has no
    /// client to ask - so what it records is the observer's view of the fight.
    /// </summary>
    public static class BanditObservers
    {
        private static readonly HashSet<ulong> Observers = new HashSet<ulong>();

        /// <summary>
        /// While set, every real player is an observer - for test runs, where anyone on the server
        /// is watching, including whoever joins halfway through. Bandits are never affected:
        /// IsHostile only asks this about players who are not bandits.
        /// </summary>
        public static bool EveryoneObserves { get; set; }

        public static bool IsObserver(Player player)
        {
            if (player?.channel?.owner == null)
            {
                return false;
            }

            if (EveryoneObserves)
            {
                return true;
            }

            return Observers.Count > 0 && Observers.Contains(player.channel.owner.playerID.steamID.m_SteamID);
        }

        /// <summary>Makes a player an observer or not, and returns whether they were one before.</summary>
        public static bool Set(Player player, bool observing)
        {
            if (player?.channel?.owner == null)
            {
                return false;
            }

            ulong id = player.channel.owner.playerID.steamID.m_SteamID;
            bool was = Observers.Contains(id);
            if (observing)
            {
                Observers.Add(id);
            }
            else
            {
                Observers.Remove(id);
            }

            return was;
        }

        /// <summary>
        /// Asks a player's client for a screenshot and writes it to <paramref name="path"/> when it
        /// arrives - a 640x480 JPEG, a few hundred milliseconds later. Nothing is shown to the
        /// player. Returns false only when there is nobody to ask.
        /// </summary>
        public static bool Capture(Player player, string path)
        {
            if (player?.channel?.owner == null || string.IsNullOrEmpty(path))
            {
                return false;
            }

            player.sendScreenshot(CSteamID.Nil, (steamId, data) =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, data);
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"[Bandit] Could not save a screenshot to {path}: {e.Message}");
                }
            });

            return true;
        }
    }
}
