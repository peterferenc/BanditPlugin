using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.Navigation
{
    /// <summary>
    /// A running commentary on what every driving bandit thinks it is doing.
    ///
    /// This exists because the interesting failures are all invisible. "The stryker stopped on an
    /// empty road" is not a report anybody can act on: the road was empty to a person standing on
    /// it, and the vehicle refused it on the strength of a boxcast against a mask that includes clip
    /// volumes nobody can see, a slope test sampled every two metres, a direction banned seven
    /// seconds ago by a stall detector, and a speed limit derived from a distance probe. Any one of
    /// those can be the answer and none of them leaves a trace. So they all leave one now.
    ///
    /// Off by default and switched on per session with "/banditnavlog on", because at a line per
    /// vehicle per half second it is a development tool rather than something to run a server with.
    /// Rate limiting is per bandit rather than global, so one vehicle cannot crowd the others out of
    /// the log at the moment they all go wrong together.
    /// </summary>
    public static class BanditNavLog
    {
        /// <summary>Whether the running commentary is on. Event lines - stalls, gives-up, refusals -
        /// are written whenever it is; they are rare and they are the ones worth having.</summary>
        public static bool Enabled { get; set; }

        /// <summary>How often each vehicle repeats its state line while nothing in particular is
        /// happening.</summary>
        private const float TraceIntervalSeconds = 0.5f;

        private static readonly Dictionary<object, float> NextTrace = new Dictionary<object, float>();

        /// <summary>One event worth reading: a stall, a recovery, a refusal, a give-up. Not rate
        /// limited - these are the lines that explain the trace around them.</summary>
        public static void Write(object source, string message)
        {
            if (!Enabled)
            {
                return;
            }

            Logger.Log($"[Nav] {Name(source)}: {message}");
        }

        /// <summary>The half-second state line. Dropped rather than queued when it comes round too
        /// soon, so turning this on costs a bounded amount however many vehicles are driving.</summary>
        public static void Trace(object source, string message)
        {
            if (!Enabled)
            {
                return;
            }

            NextTrace.TryGetValue(source, out float next);
            if (Time.time < next)
            {
                return;
            }

            NextTrace[source] = Time.time + TraceIntervalSeconds;
            Logger.Log($"[Nav] {Name(source)}: {message}");
        }

        /// <summary>Clears the rate-limit table. Called when the logging is switched off, so a long
        /// session does not leave an entry per bandit that ever drove.</summary>
        public static void Reset()
        {
            NextTrace.Clear();
        }

        /// <summary>
        /// Who is talking. The vehicle's own instance id is in there because a column routinely
        /// holds two Urals, and "Ural stalled" is not a report about either of them.
        /// </summary>
        private static string Name(object source)
        {
            BanditVehicleDriver driver = source as BanditVehicleDriver;
            if (driver == null)
            {
                return source?.ToString() ?? "?";
            }

            SDG.Unturned.InteractableVehicle vehicle = driver.Vehicle;
            return vehicle == null
                ? "unseated"
                : $"{(vehicle.asset != null ? vehicle.asset.FriendlyName : "vehicle")}#{vehicle.instanceID}";
        }

    }
}
