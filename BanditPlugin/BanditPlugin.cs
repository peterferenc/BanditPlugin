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
            Logger.Log("[Bandit] Loaded. /bandit to spawn, /banditgoto to send it somewhere, /banditpatrol to set it walking a route.");
        }

        protected override void Unload()
        {
            DamageTool.damagePlayerRequested -= OnDamagePlayerRequested;
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
