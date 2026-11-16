using System;
using System.IO;

namespace Wolf
{
    // Where the tests look for the real files. The suite runs on the server box, so the paths
    // are the ones this machine uses; overrides keep the tests usable on another machine.
    internal static class TestPaths
    {
        public static string ModsDir
        {
            get { return Override("WOLF_TEST_MODS", @"C:\Server 1\Mods"); }
        }

        public static string ConfigFile
        {
            get { return Override("WOLF_TEST_CONFIG", @"C:\Server 1\serverconfig.xml"); }
        }

        public static string ModSyncConfig
        {
            get { return Override("WOLF_TEST_MODSYNC", @"C:\ProgramData\ModSyncServer\config.json"); }
        }

        public static string AntiCheatConfig
        {
            get { return Override("WOLF_TEST_ZAC", Path.Combine(ModsDir, "Zipp0o's-AntiCheat", "AntiCheatConfig.xml")); }
        }

        private static string Override(string variable, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }
}
