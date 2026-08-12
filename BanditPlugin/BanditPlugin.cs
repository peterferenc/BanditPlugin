using Rocket.Core.Plugins;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin
{
    public class BanditPlugin : RocketPlugin<BanditConfiguration>
    {
        public static BanditPlugin Instance { get; private set; }

        protected override void Load()
        {
            Instance = this;
            Logger.Log("[Bandit] Loaded. Use /bandit to spawn a stationary bot that turns to face the nearest player.");
        }

        protected override void Unload()
        {
            Instance = null;
        }
    }
}
