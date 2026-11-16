using System;
using System.IO;

namespace Wolf
{
    // Where the tests look for the real files. Nothing machine-specific is kept in the source:
    // every path comes from an environment variable, and when a variable is missing the
    // corresponding test prints "skip" instead of failing.
    //
    //   WOLF_TEST_MODS     - folder with the mods being distributed
    //   WOLF_TEST_CONFIG   - serverconfig.xml of the real server
    //   WOLF_TEST_MODSYNC  - config.json of a ModSync installation (exclude_patterns live there)
    //   WOLF_TEST_ZAC      - AntiCheatConfig.xml; defaults to <mods>\Zipp0o's-AntiCheat\AntiCheatConfig.xml
    internal static class TestPaths
    {
        public static string ModsDir
        {
            get { return Env("WOLF_TEST_MODS", ""); }
        }

        public static string ConfigFile
        {
            get { return Env("WOLF_TEST_CONFIG", ""); }
        }

        public static string ModSyncConfig
        {
            get { return Env("WOLF_TEST_MODSYNC", ""); }
        }

        public static string AntiCheatConfig
        {
            get
            {
                string fallback = string.IsNullOrEmpty(ModsDir)
                    ? ""
                    : Path.Combine(ModsDir, "Zipp0o's-AntiCheat", "AntiCheatConfig.xml");
                return Env("WOLF_TEST_ZAC", fallback);
            }
        }

        private static string Env(string variable, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }
}
