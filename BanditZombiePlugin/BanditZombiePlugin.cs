using Rocket.Core.Plugins;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditZombiePlugin
{
    public class BanditZombiePlugin : RocketPlugin<BanditZombieConfiguration>
    {
        public static BanditZombiePlugin Instance { get; private set; }

        protected override void Load()
        {
            Instance = this;
            Logger.Log("[BanditZombie] Loaded. Use /bandit to spawn a stationary bot that turns to face the nearest player.");
        }

        protected override void Unload()
        {
            Instance = null;
        }
    }
}
