using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.Navigation
{
    /// <summary>
    /// Per-map patrol waypoints, stored one route per level as plain text in
    /// Rocket/Plugins/BanditPlugin/Waypoints/&lt;map&gt;.txt.
    ///
    /// Text rather than XML/JSON because the file is meant to be hand-editable next to the
    /// in-game /banditwp commands, and a waypoint is three numbers.
    ///
    /// When a map has no file, the route falls back to the map's own LocationNodes - the named
    /// places the level editor marks on official maps - so patrol does something sensible with
    /// zero setup. Those are town centres, not walkable-checked points, so a hand-recorded route
    /// is always better; this is a default, not a substitute.
    /// </summary>
    public static class BanditWaypointStore
    {
        private static readonly List<Vector3> Waypoints = new List<Vector3>();
        private static string _loadedMap;

        public static string CurrentMap => Level.info != null ? Level.info.name : null;

        /// <summary>Recorded waypoints for the current map. Empty if none have been recorded.</summary>
        public static IReadOnlyList<Vector3> Current
        {
            get
            {
                EnsureLoaded();
                return Waypoints;
            }
        }

        /// <summary>
        /// The route patrol should actually walk: the recorded waypoints, or the map's location
        /// nodes when nothing has been recorded and the config allows the fallback.
        /// </summary>
        public static List<Vector3> GetRoute(bool allowLocationNodeFallback)
        {
            EnsureLoaded();

            if (Waypoints.Count > 0)
            {
                return new List<Vector3>(Waypoints);
            }

            List<Vector3> route = new List<Vector3>();
            if (!allowLocationNodeFallback)
            {
                return route;
            }

            // Preferred source: legacy maps have their LOCATION nodes converted into devkit nodes
            // during Level.load (LevelNodes.AutoConvertLegacyNodes) and modern maps store them
            // directly, so this covers both - provided the system is constructed on a headless
            // server, which is not guaranteed for a type family named "TempNode".
            LocationDevkitNodeSystem system = LocationDevkitNodeSystem.Get();
            if (system != null)
            {
                foreach (LocationDevkitNode node in system.GetAllNodes())
                {
                    if (node != null)
                    {
                        route.Add(node.transform.position);
                    }
                }
            }

            if (route.Count > 0)
            {
                return route;
            }

            // Fall back to the legacy list. It is obsolete but still populated by LevelNodes.load
            // on the server, and it is the difference between patrol working out of the box on a
            // map and silently having nowhere to go.
#pragma warning disable CS0618
            if (LevelNodes.nodes == null)
            {
                return route;
            }

            foreach (Node node in LevelNodes.nodes)
            {
                if (node is LocationNode)
                {
                    route.Add(node.point);
                }
            }
#pragma warning restore CS0618

            return route;
        }

        public static void Add(Vector3 point)
        {
            EnsureLoaded();
            Waypoints.Add(point);
            Save();
        }

        /// <summary>Removes the recorded waypoint nearest <paramref name="point"/> within the radius.</summary>
        public static bool RemoveNearest(Vector3 point, float radius)
        {
            EnsureLoaded();

            int nearestIndex = -1;
            float nearestDistanceSq = radius * radius;

            for (int i = 0; i < Waypoints.Count; i++)
            {
                float distanceSq = (Waypoints[i] - point).sqrMagnitude;
                if (distanceSq <= nearestDistanceSq)
                {
                    nearestDistanceSq = distanceSq;
                    nearestIndex = i;
                }
            }

            if (nearestIndex < 0)
            {
                return false;
            }

            Waypoints.RemoveAt(nearestIndex);
            Save();
            return true;
        }

        public static int Clear()
        {
            EnsureLoaded();
            int count = Waypoints.Count;
            Waypoints.Clear();
            Save();
            return count;
        }

        private static void EnsureLoaded()
        {
            string map = CurrentMap;
            if (_loadedMap == map)
            {
                return;
            }

            _loadedMap = map;
            Waypoints.Clear();

            string path = GetFilePath(map);
            if (path == null || !File.Exists(path))
            {
                return;
            }

            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }

                    string[] parts = trimmed.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3)
                    {
                        continue;
                    }

                    if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                        && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        Waypoints.Add(new Vector3(x, y, z));
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"[Bandit] Could not read waypoints from {path}: {e.Message}");
            }
        }

        private static void Save()
        {
            string path = GetFilePath(_loadedMap);
            if (path == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                List<string> lines = new List<string>(Waypoints.Count + 1)
                {
                    "# Bandit patrol waypoints - one 'x y z' per line, walked in order."
                };
                foreach (Vector3 point in Waypoints)
                {
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} {2:0.##}",
                        point.x, point.y, point.z));
                }

                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception e)
            {
                Logger.LogError($"[Bandit] Could not write waypoints to {path}: {e.Message}");
            }
        }

        private static string GetFilePath(string map)
        {
            if (string.IsNullOrEmpty(map) || BanditPlugin.Instance == null)
            {
                return null;
            }

            string safeName = map;
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(invalid, '_');
            }

            return Path.Combine(Path.Combine(BanditPlugin.Instance.Directory, "Waypoints"), safeName + ".txt");
        }
    }
}
