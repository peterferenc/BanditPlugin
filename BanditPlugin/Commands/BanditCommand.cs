using System;
using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Core;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// The plumbing every part of /bandit shares: answering the caller, the sentences they all
    /// have to say, and reading the few argument shapes that recur.
    ///
    /// <see cref="Reply"/> exists because UnturnedChat.Say sends a console caller an unprefixed
    /// line, and a server console carrying half a dozen plugins wants to know which one is talking.
    /// /bandit is allowed from the console, so everything it says goes through here.
    /// </summary>
    public static class BanditCommand
    {
        /// <summary>
        /// What every order says when there is nothing to act on. Worded as an instruction rather
        /// than an error because it is nearly always the first command somebody tries.
        /// </summary>
        public const string NothingSpawned = "Nothing to command - spawn something with /bandit spawn first.";

        /// <summary>Said instead when the last spawned bandit is lying there dead.</summary>
        public const string BanditIsDead = "That bandit is dead.";

        /// <summary>The two permissions /bandit is split between.</summary>
        public const string SpawnPermission = "bandit.spawn";
        public const string TeamPermission = "bandit.team";

        /// <summary>
        /// Answers the caller wherever they are: in chat if they are a player, on the server
        /// console - named, so it can be told apart from every other plugin - if they are not.
        /// </summary>
        public static void Reply(IRocketPlayer caller, string message, Color color)
        {
            if (caller is UnturnedPlayer)
            {
                UnturnedChat.Say(caller, message, color);
            }
            else
            {
                Rocket.Core.Logging.Logger.Log($"[Bandit] {message}");
            }
        }

        /// <summary>
        /// Whether the caller holds a permission. Rocket only checks a command's permissions as a
        /// whole, and /bandit is one command, so the split between spawning and teams is made here.
        /// The console and admins hold everything.
        /// </summary>
        public static bool HasPermission(IRocketPlayer caller, string permission)
        {
            return caller.IsAdmin || R.Permissions.HasPermission(caller, permission);
        }

        /// <summary>Refuses, and says why, when the caller lacks a permission.</summary>
        public static bool RequirePermission(IRocketPlayer caller, string permission)
        {
            if (HasPermission(caller, permission))
            {
                return true;
            }

            Reply(caller, $"You need the {permission} permission for that.", Color.red);
            return false;
        }

        /// <summary>
        /// The caller as a player, or null with an explanation if they are the console. For the
        /// commands that read where somebody is standing or looking.
        /// </summary>
        public static UnturnedPlayer RequirePlayer(IRocketPlayer caller, string what)
        {
            if (caller is UnturnedPlayer player)
            {
                return player;
            }

            Reply(caller, $"{what} reads where you are standing or looking, so it has to be run in-game.", Color.red);
            return null;
        }

        /// <summary>
        /// Whether a bandit is dead, and so cannot be given an order. Guards the life component
        /// because a bandit is briefly on the map before one is attached.
        /// </summary>
        public static bool IsDead(BanditBotController bandit)
        {
            return bandit.Self.life != null && bandit.Self.life.isDead;
        }

        /// <summary>
        /// Reads the "on"/"off" argument every switch shares. Anything else is a usage error rather
        /// than a default, so a typo cannot silently mean "off" - and there is deliberately no
        /// toggle, because "toggle" means nothing to a squad that is half one way and half the other.
        /// </summary>
        public static bool TryParseOnOff(string argument, out bool on)
        {
            switch (argument?.ToLowerInvariant())
            {
                case "on":
                case "true":
                case "1":
                    on = true;
                    return true;
                case "off":
                case "false":
                case "0":
                    on = false;
                    return true;
                default:
                    on = false;
                    return false;
            }
        }

        /// <summary>Bandits as their numbers and kits - "#3 rifleman, #4 mg" - so a reply names what
        /// an order can be given to.</summary>
        public static string DescribeMembers(IEnumerable<BanditBotController> bandits)
        {
            List<string> names = new List<string>();
            foreach (BanditBotController bandit in bandits)
            {
                names.Add($"#{bandit.Id} {bandit.KitName}" + (IsDead(bandit) ? " (dead)" : string.Empty));
            }

            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "nobody";
        }

        /// <summary>Whether a word is the one given, ignoring case.</summary>
        public static bool Is(string word, string expected)
        {
            return word != null && word.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Everything from <paramref name="start"/> on.</summary>
        public static string[] Rest(string[] args, int start)
        {
            if (args == null || start >= args.Length)
            {
                return new string[0];
            }

            string[] rest = new string[args.Length - start];
            Array.Copy(args, start, rest, 0, rest.Length);
            return rest;
        }
    }
}
