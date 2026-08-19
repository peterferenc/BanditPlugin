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
    /// The route a convoy drives on this map, stored in
    /// Rocket/Plugins/BanditPlugin/Waypoints/&lt;map&gt;.convoy.txt.
    ///
    /// Deliberately not the same list <see cref="BanditWaypointStore"/> keeps. A patrol route is a
    /// handful of points inside a town that men walk between and loiter at; a convoy route is a
    /// handful of points across the map that a column of vehicles drives through once. Sharing them
    /// would mean every patrol suddenly walking the length of the island the moment somebody set up
    /// a convoy, and every convoy touring the inside of a warehouse.
    ///
    /// Same plain-text format as the patrol file, and for the same reason: a waypoint is three
    /// numbers, and being able to open the file and move one beats any amount of in-game editing.
    /// </summary>
    public static class BanditConvoyRoute
    {
        private static readonly List<Vector3> Points = new List<Vector3>();
        private static string _loadedMap;

        public static string CurrentMap => Level.info != null ? Level.info.name : null;

        /// <summary>This map's convoy route, in the order it is driven.</summary>
        public static IReadOnlyList<Vector3> Current
        {
            get
            {
                EnsureLoaded();
                return Points;
            }
        }

        /// <summary>
        /// A convoy needs somewhere to start and somewhere to end, so two is the floor. The command
        /// says so rather than spawning a column that immediately reports having arrived.
        /// </summary>
        public static bool HasRoute
        {
            get
            {
                EnsureLoaded();
                return Points.Count >= 2;
            }
        }

        public static int Add(Vector3 point)
        {
            EnsureLoaded();
            Points.Add(point);
            Save();
            return Points.Count;
        }

        /// <summary>
        /// Removes by position in the list, one-based, because that is the number the list command
        /// printed next to it. Removing by proximity - which is what the patrol command does - is
        /// wrong for a route whose points are kilometres apart and usually nowhere near the person
        /// editing it.
        /// </summary>
        public static bool RemoveAt(int oneBasedIndex)
        {
            EnsureLoaded();

            if (oneBasedIndex < 1 || oneBasedIndex > Points.Count)
            {
                return false;
            }

            Points.RemoveAt(oneBasedIndex - 1);
            Save();
            return true;
        }

        public static int Clear()
        {
            EnsureLoaded();
            int count = Points.Count;
            Points.Clear();
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
            Points.Clear();

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
                        Points.Add(new Vector3(x, y, z));
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"[Bandit] Could not read the convoy route from {path}: {e.Message}");
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

                List<string> lines = new List<string>(Points.Count + 1)
                {
                    "# Bandit convoy route - one 'x y z' per line, driven in order."
                };
                foreach (Vector3 point in Points)
                {
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} {2:0.##}",
                        point.x, point.y, point.z));
                }

                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception e)
            {
                Logger.LogError($"[Bandit] Could not write the convoy route to {path}: {e.Message}");
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

            return Path.Combine(Path.Combine(BanditPlugin.Instance.Directory, "Waypoints"), safeName + ".convoy.txt");
        }
    }
}
