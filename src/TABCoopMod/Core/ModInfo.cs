using System;
using System.IO;
using System.Reflection;

namespace TABCoopMod.Core
{
    /// <summary>
    /// Central version metadata shared across assembly attributes, networking handshakes,
    /// Steam lobby metadata, UI headers, and version-tagged log records.
    /// </summary>
    public static class ModInfo
    {
        // LAN-enabled build. The assembly version is kept at 0.1.5.0 on purpose: the installed
        // TheyAreBillions.exe.config pins "TABCoopMod, Version=0.1.5.0" in appDomainManagerAssembly,
        // so bumping it would require rewriting the game config (and would not load otherwise).
        // Only the advertised/lobby-handshake version string changes, so upstream v0.1.5 hosts and
        // this LAN build are explicitly rejected instead of silently mismatching protocols.
        public const string Version = "0.1.5-LAN";
        public const string Tag = "v" + Version;
        public const string AssemblyVersion = "0.1.5.0";

        public static string GetBuildTimestamp()
        {
            try
            {
                string loc = typeof(ModInfo).Assembly.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                    return File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { }
            return "unknown";
        }
    }
}
