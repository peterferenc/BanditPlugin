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
            DamageTool.damagePlayerRequested += OnDamagePlayerRequested;
            Logger.Log("[Bandit] Loaded. /bandit spawns one - it just stands until ordered. "
                + "/bandit shoot|cover|peek start|stop are the standing orders; /banditgoto sends one somewhere, "
                + "/banditpatrol sets it walking a route, /banditstatus reports what each is doing.");
        }

        protected override void Unload()
        {
            DamageTool.damagePlayerRequested -= OnDamagePlayerRequested;

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
