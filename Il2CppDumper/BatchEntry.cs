// BatchEntry — headless entry point for DevXUnityRewrite integration.
// Reuses Program.Init + Program.Dump but:
//   * loads config from an explicit path (not AppDomain.BaseDirectory),
//   * never touches OpenFileDialog / Console.ReadKey,
//   * captures Console output for diagnostics.
// Business logic is unchanged; this is a thin façade added for the TestRunner
// cross-validation scenario (see MODULE_IL2CPP_TO_CSHARP.md §15).
using System;
using System.IO;
using System.Text.Json;

namespace Il2CppDumper
{
    public static class BatchEntry
    {
        /// <summary>
        /// Run Il2CppDumper end-to-end without any UI prompt.
        /// Returns true on success (dump.cs written), false on failure.
        /// <paramref name="il2cppPath"/> / <paramref name="metadataPath"/> must exist.
        /// <paramref name="outputDir"/> is created if missing; should end with a separator.
        /// <paramref name="configPath"/> may be null → uses &lt;baseDir&gt;/config.json next to this assembly.
        /// </summary>
        public static bool RunHeadless(
            string il2cppPath,
            string metadataPath,
            string outputDir,
            string configPath = null)
        {
            if (string.IsNullOrWhiteSpace(il2cppPath) || !File.Exists(il2cppPath))
                throw new FileNotFoundException("il2cpp binary not found: " + il2cppPath, il2cppPath);
            if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
                throw new FileNotFoundException("global-metadata not found: " + metadataPath, metadataPath);
            if (string.IsNullOrWhiteSpace(outputDir))
                throw new ArgumentException("outputDir must not be empty.", nameof(outputDir));

            outputDir = Path.GetFullPath(outputDir);
            char sep = Path.DirectorySeparatorChar;
            if (outputDir.Length == 0 || (outputDir[outputDir.Length - 1] != sep && outputDir[outputDir.Length - 1] != '/'))
                outputDir += sep;
            Directory.CreateDirectory(outputDir);

            // Load config (default: beside the Il2CppDumper assembly), but mark RequireAnyKey=false
            // regardless of file contents so the process never blocks on key input.
            if (string.IsNullOrWhiteSpace(configPath))
                configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

            Config config;
            if (File.Exists(configPath))
            {
                config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath));
            }
            else
            {
                config = new Config();
            }
            config.RequireAnyKey = false;
            Program.config = config;

            try
            {
                if (Program.Init(il2cppPath, metadataPath, out var metadata, out var il2Cpp))
                {
                    Program.Dump(metadata, il2Cpp, outputDir);
                    return true;
                }
                return false;
            }
            catch
            {
                // Init already prints to Console; rethrow so the caller sees the failure.
                throw;
            }
        }
    }
}
