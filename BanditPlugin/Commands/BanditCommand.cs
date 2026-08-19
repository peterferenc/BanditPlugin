using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// The plumbing every /bandit command repeated: answering the caller, and the two or three
    /// sentences they all had to say.
    ///
    /// <see cref="Reply"/> was copied verbatim into seven command classes and inlined into an
    /// eighth. It exists because UnturnedChat.Say sends a console caller an unprefixed line, and a
    /// server console carrying half a dozen plugins wants to know which one is talking - so every
    /// command that <see cref="AllowedCaller.Both"/> allows from the console grew its own copy.
    /// Commands that are <see cref="AllowedCaller.Console"/>-less deliberately do not use it: a
    /// player-only command can only ever be answered in chat, where the prefix would be noise.
    /// </summary>
    public static class BanditCommand
    {
        /// <summary>
        /// What every command says when there is nothing to act on. Worded as an instruction
        /// rather than an error because it is nearly always the first command somebody tries.
        /// </summary>
        public const string NoBandit = "No bandit to command - spawn one with /bandit first.";

        /// <summary>Said instead when the last spawned bandit is lying there dead.</summary>
        public const string BanditIsDead = "That bandit is dead.";

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
        /// Whether a bandit is dead, and so cannot be given an order. Guards the life component
        /// because a bandit is briefly on the map before one is attached.
        /// </summary>
        public static bool IsDead(BanditBotController bandit)
        {
            return bandit.Self.life != null && bandit.Self.life.isDead;
        }

        /// <summary>
        /// Reads the "start"/"stop" argument that the stance and patrol commands share. Anything
        /// else is a usage error rather than a default, so a typo cannot silently mean "stop".
        /// </summary>
        public static bool TryParseStartStop(string argument, out bool start)
        {
            switch (argument.ToLowerInvariant())
            {
                case "start":
                    start = true;
                    return true;
                case "stop":
                    start = false;
                    return true;
                default:
                    start = false;
                    return false;
            }
        }
    }
}
