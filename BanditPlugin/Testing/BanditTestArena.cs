using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BanditPlugin.Navigation;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.Testing
{
    /// <summary>
    /// Where on each map the tests are run, stored per level in
    /// Rocket/Plugins/BanditPlugin/Tests/&lt;map&gt;.arena.txt as "name x y z yaw".
    ///
    /// A test looks for a point with its own name first and falls back to "arena", so one open
    /// spot serves every test until one of them needs somewhere particular - cover wants something
    /// to hide behind, goto wants room to walk. Recorded by standing on the spot, because the only
    /// way to know a place suits a test is to have looked at it; there is no reliable way to pick
    /// "open ground with a rock nearby" off a map from code.
    /// </summary>
    public static class BanditTestArena
    {
        public const string DefaultPoint = "arena";

        public struct Point
        {
            public Vector3 Position;
            public float Yaw;

            public Vector3 Forward => Quaternion.Euler(0f, Yaw, 0f) * Vector3.forward;
        }

        public static bool TryGet(string testName, out Point point, out string usedName)
        {
            Dictionary<string, Point> points = Load();

            if (testName != null && points.TryGetValue(testName.ToLowerInvariant(), out point))
            {
                usedName = testName.ToLowerInvariant();
                return true;
            }

            usedName = DefaultPoint;
            return points.TryGetValue(DefaultPoint, out point);
        }

        public static Dictionary<string, Point> Load()
        {
            Dictionary<string, Point> points = new Dictionary<string, Point>();
            string path = FilePath();
            if (path == null || !File.Exists(path))
            {
                return points;
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

                    string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4
                        || !TryParse(parts[1], out float x)
                        || !TryParse(parts[2], out float y)
                        || !TryParse(parts[3], out float z))
                    {
                        continue;
                    }

                    float yaw = parts.Length > 4 && TryParse(parts[4], out float parsedYaw) ? parsedYaw : 0f;
                    points[parts[0].ToLowerInvariant()] = new Point { Position = new Vector3(x, y, z), Yaw = yaw };
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"[BanditTest] Could not read arena points from {path}: {e.Message}");
            }

            return points;
        }

        public static bool Set(string name, Vector3 position, float yaw)
        {
            Dictionary<string, Point> points = Load();
            points[name.ToLowerInvariant()] = new Point { Position = position, Yaw = yaw };
            return Save(points);
        }

        public static bool Remove(string name)
        {
            Dictionary<string, Point> points = Load();
            return points.Remove(name.ToLowerInvariant()) && Save(points);
        }

        private static bool Save(Dictionary<string, Point> points)
        {
            string path = FilePath();
            if (path == null)
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                List<string> lines = new List<string>
                {
                    "# Bandit test arena - 'name x y z yaw' per line. A test uses the point named after it,",
                    "# or 'arena' if there is none. Record with /bandit test arena set [name]."
                };
                foreach (KeyValuePair<string, Point> entry in points)
                {
                    Vector3 p = entry.Value.Position;
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0.##} {2:0.##} {3:0.##} {4:0.#}",
                        entry.Key, p.x, p.y, p.z, entry.Value.Yaw));
                }

                File.WriteAllLines(path, lines.ToArray());
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"[BanditTest] Could not write arena points to {path}: {e.Message}");
                return false;
            }
        }

        private static bool TryParse(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>The directory test arenas and results live in.</summary>
        public static string TestsDirectory => BanditPlugin.Instance != null
            ? Path.Combine(BanditPlugin.Instance.Directory, "Tests")
            : null;

        private static string FilePath()
        {
            string map = BanditWaypointStore.CurrentMap;
            string directory = TestsDirectory;
            if (string.IsNullOrEmpty(map) || directory == null)
            {
                return null;
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                map = map.Replace(invalid, '_');
            }

            return Path.Combine(directory, map + ".arena.txt");
        }
    }
}
