using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace BuildAI.Core
{
    public static class BuildInfo
    {
        public const string Version = "7.4";
        public const string Iteration = "7.4 release";
        public const string BuildDateUtc = "2026-09-26";
        public static string Banner => "BuildAI " + Iteration + " | Version " + Version + " | Build " + BuildDateUtc + " UTC";

        public static string LoadedAssemblyIdentity(Assembly assembly, string revitVersion)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            var path = assembly.Location;
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return "PLUGIN BUILD ID" + Environment.NewLine +
                       "Version=" + (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                           ?? assembly.GetName().Version?.ToString() ?? "<unknown>") + Environment.NewLine +
                       "AssemblyPath=" + path + Environment.NewLine +
                       "FileVersion=" + FileVersionInfo.GetVersionInfo(path).FileVersion + Environment.NewLine +
                       "AssemblyVersion=" + assembly.GetName().Version + Environment.NewLine +
                       "Sha256=" + BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() + Environment.NewLine +
                       "RevitVersion=" + (revitVersion ?? "<unknown>");
        }
    }
}


