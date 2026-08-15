using BanditPlugin.FakePlayer;
using Rocket.Core.Plugins;
using SDG.Unturned;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin
{
    public class BanditPlugin : RocketPlugin<BanditConfiguration>
    {
        public static BanditPlugin Instance { get; private set; }

        protected override void Load()
        {
            Instance = this;
            BackfillEmptyCollections();
            DamageTool.damagePlayerRequested += OnDamagePlayerRequested;
            Logger.Log("[Bandit] Loaded. /squadspawn puts a whole squad down fighting; "
                + "/bandit spawns one - it just stands until ordered. "
                + "/bandit shoot|cover|peek start|stop are the standing orders; /banditgoto sends one somewhere, "
                + "/banditpatrol sets it walking a route, /banditprone lies one down, "
                + "/banditstatus reports what each is doing.");
        }

        /// <summary>
        /// Fills in the list settings for a configuration file written before they existed.
        ///
        /// These cannot be seeded from their field initializers the way every other setting is.
        /// XmlSerializer adds to a collection it finds already populated instead of replacing it,
        /// so a seeded list ends up holding the defaults *and* the file's contents - and since
        /// Rocket's XMLFileAsset.Load() saves straight after deserializing, that doubled list is
        /// written back to disk and doubles again on the next start. Doing it here, once the file
        /// has been read, is the only place an empty list unambiguously means "the file had none".
        /// </summary>
        private void BackfillEmptyCollections()
        {
            BanditConfiguration config = Configuration.Instance;
            bool changed = false;

            if (config.Kits == null || config.Kits.Count == 0)
            {
                config.Kits = BanditKit.BuildDefaults();
                changed = true;
            }

            if (config.SquadComposition == null || config.SquadComposition.Count == 0)
            {
                config.SquadComposition = BanditConfiguration.DefaultSquadComposition();
                changed = true;
            }

            if (changed)
            {
                Configuration.Save();
                Logger.Log("[Bandit] Wrote the default kits and squad composition into the configuration.");
            }
        }

        protected override void Unload()
        {
            DamageTool.damagePlayerRequested -= OnDamagePlayerRequested;

            // Squads are held in a static list, which a Rocket reload does not clear on its own -
            // without this the next load starts with squads full of players from the last one.
            BanditSquad.ClearAll();

            // Cover markers are paintball decals with no lifetime, so they outlive the plugin that
            // drew them. Clearing here means /rocket reload doesn't strand a field of paint on
            // everyone - and it has to happen before Instance goes null, since Clear() uses it.
            Navigation.BanditCoverDebug.Clear();

            Instance = null;
        }

        /// <summary>
        /// Tells a bot it has been hit, and roughly from where.
        ///
        /// Without this a bandit shot from behind cover it can't see past would stand there being
        /// killed: its own scan only finds players it has line of sight to. DamagePlayerParameters
        /// carries the direction the damage was travelling, so the shooter is back along it.
        ///
        /// This runs inside PlayerLife.askDamage for every player on the server, so it must stay
        /// cheap and must not throw - an exception here unwinds back through UseableGun.ballistics.
        /// </summary>
        private static void OnDamagePlayerRequested(ref DamagePlayerParameters parameters, ref bool shouldAllow)
        {
            if (!shouldAllow || parameters.player == null)
            {
                return;
            }

            BanditBotController controller = parameters.player.GetComponent<BanditBotController>();
            if (controller == null)
            {
                return;
            }

            try
            {
                controller.NotifyDamaged(parameters.direction);
            }
            catch (System.Exception e)
            {
                Logger.LogError($"[Bandit] Damage notification threw: {e}");
            }
        }
    }
}
