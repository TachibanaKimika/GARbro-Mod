using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameRes;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SchemeTool
{
    internal sealed class CommandLineException : Exception
    {
        public CommandLineException (string message) : base (message)
        {
        }
    }

    internal static class SchemeDatabaseCommands
    {
        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
        };

        public static int Run (string[] args)
        {
            if (null == args || args.Length < 2 || !string.Equals (args[0], "database", StringComparison.Ordinal))
                throw new CommandLineException (Usage);
            var options = ParseOptions (args, 2);
            switch (args[1])
            {
            case "inspect":
                return Inspect (options);
            case "analyze":
                return Analyze (options, false);
            case "merge":
                return Analyze (options, true);
            case "create-test-fixtures":
                return CreateTestFixtures (options);
            default:
                throw new CommandLineException ("Unknown database action.\n" + Usage);
            }
        }

        static int Inspect (IDictionary<string, string> options)
        {
            RequireTrustedInputs (options);
            EnsureOnly (options, "input", "report", "trusted-inputs", "overwrite");
            string inputPath = RequireValue (options, "input");
            string reportPath = RequireValue (options, "report");
            EnsurePathsAreDistinct ("Inspection report must not overwrite its input.",
                reportPath, inputPath);
            bool overwrite = HasFlag (options, "overwrite");
            var snapshot = SchemeDatabaseFile.Read (inputPath, "input");
            var hasher = new SemanticHasher();
            var report = new SchemeDatabaseInspection {
                Input = snapshot.Metadata,
                SemanticHash = hasher.Hash (snapshot.Database),
                Schemes = snapshot.Database.SchemeMap.OrderBy (x => x.Key, StringComparer.Ordinal)
                    .Select (x => new SchemeInventoryEntry {
                        Key = x.Key,
                        ValueType = null == x.Value ? null : x.Value.GetType().FullName,
                        SemanticHash = hasher.Hash (x.Value),
                    }).ToList(),
            };
            WriteJsonAtomic (reportPath, report, overwrite);
            WriteResult (new {
                status = "success",
                operation = "database.inspect",
                reportSha256 = ComputeFileHash (reportPath),
                version = report.Input.Version,
                schemeCount = report.Input.SchemeCount,
                gameMapCount = report.Input.GameMapCount,
                semanticHash = report.SemanticHash,
            });
            return 0;
        }

        static int Analyze (IDictionary<string, string> options, bool writeOutput)
        {
            RequireTrustedInputs (options);
            if (writeOutput)
                EnsureOnly (options, "base", "ours", "theirs", "output", "report", "trusted-inputs", "overwrite");
            else
                EnsureOnly (options, "base", "ours", "theirs", "report", "trusted-inputs", "overwrite");
            string basePath = RequireValue (options, "base");
            string oursPath = RequireValue (options, "ours");
            string theirsPath = RequireValue (options, "theirs");
            string reportPath = RequireValue (options, "report");
            bool overwrite = HasFlag (options, "overwrite");
            EnsureDistinctInputs (basePath, oursPath, theirsPath);
            EnsurePathsAreDistinct ("Merge report must not overwrite an input.",
                reportPath, basePath, oursPath, theirsPath);
            string outputPath = null;
            if (writeOutput)
            {
                outputPath = RequireValue (options, "output");
                EnsurePathsAreDistinct ("Output must not overwrite an input or report.",
                    outputPath, basePath, oursPath, theirsPath, reportPath);
            }

            var baseSnapshot = SchemeDatabaseFile.Read (basePath, "base");
            var oursSnapshot = SchemeDatabaseFile.Read (oursPath, "ours");
            var theirsSnapshot = SchemeDatabaseFile.Read (theirsPath, "theirs");
            var outcome = new SchemeDatabaseMerger().Merge (baseSnapshot, oursSnapshot, theirsSnapshot);
            WriteJsonAtomic (reportPath, outcome.Report, overwrite);
            string reportHash = ComputeFileHash (reportPath);
            if (outcome.Report.Conflicts.Count != 0)
            {
                WriteResult (new {
                    status = "conflict",
                    operation = writeOutput ? "database.merge" : "database.analyze",
                    reportSha256 = reportHash,
                    conflicts = outcome.Report.Conflicts.Count,
                    outputWritten = false,
                });
                return 3;
            }

            string outputHash = null;
            if (writeOutput)
            {
                SchemeDatabaseFile.Write (outputPath, outcome.Database, overwrite);
                outputHash = ComputeFileHash (outputPath);
            }
            WriteResult (new {
                status = "clean",
                operation = writeOutput ? "database.merge" : "database.analyze",
                reportSha256 = reportHash,
                conflicts = 0,
                changes = outcome.Report.Changes.Count,
                resultVersion = outcome.Report.Result.Version,
                resultSemanticHash = outcome.Report.Result.SemanticHash,
                outputWritten = writeOutput,
                outputSha256 = outputHash,
            });
            return 0;
        }

        static int CreateTestFixtures (IDictionary<string, string> options)
        {
            EnsureOnly (options, "directory", "overwrite");
            string directory = Path.GetFullPath (RequireValue (options, "directory"));
            bool overwrite = HasFlag (options, "overwrite");
            if (!Directory.Exists (directory))
                Directory.CreateDirectory (directory);

            var baseScheme = new SchemeMergeTestScheme {
                Label = "fixture",
                Values = new Dictionary<string, int> {
                    { "left", 1 }, { "right", 1 }, { "same", 1 },
                },
            };
            var oursScheme = new SchemeMergeTestScheme {
                Label = "fixture",
                Values = new Dictionary<string, int> {
                    { "left", 2 }, { "right", 1 }, { "same", 1 }, { "oursOnly", 5 },
                },
            };
            var theirsScheme = new SchemeMergeTestScheme {
                Label = "fixture",
                Values = new Dictionary<string, int> {
                    { "left", 1 }, { "right", 2 }, { "same", 1 }, { "theirsOnly", 6 },
                },
            };
            var conflictScheme = new SchemeMergeTestScheme {
                Label = "fixture",
                Values = new Dictionary<string, int> {
                    { "left", 3 }, { "right", 2 }, { "same", 1 }, { "theirsOnly", 6 },
                },
            };

            var files = new Dictionary<string, SchemeDataBase> {
                { "base.dat", CreateFixtureDatabase (10, baseScheme, "base.exe", "Base") },
                { "ours.dat", CreateFixtureDatabase (11, oursScheme, "ours.exe", "Ours") },
                { "theirs.dat", CreateFixtureDatabase (12, theirsScheme, "theirs.exe", "Theirs") },
                { "theirs-conflict.dat", CreateFixtureDatabase (12, conflictScheme, "theirs.exe", "Theirs") },
            };
            foreach (var item in files)
                SchemeDatabaseFile.Write (Path.Combine (directory, item.Key), item.Value, overwrite);
            WriteResult (new {
                status = "success",
                operation = "database.create-test-fixtures",
                directory = directory,
                files = files.Keys.OrderBy (x => x, StringComparer.Ordinal).ToArray(),
            });
            return 0;
        }

        static SchemeDataBase CreateFixtureDatabase (int version, SchemeMergeTestScheme scheme,
                                                       string extraExecutable, string title)
        {
            var gameMap = new Dictionary<string, string> { { "base.exe", "Base" } };
            if (!gameMap.ContainsKey (extraExecutable))
                gameMap.Add (extraExecutable, title);
            return new SchemeDataBase {
                Version = version,
                SchemeMap = new Dictionary<string, ResourceScheme> { { "TEST", scheme } },
                GameMap = gameMap,
            };
        }

        static IDictionary<string, string> ParseOptions (string[] args, int start)
        {
            var options = new Dictionary<string, string> (StringComparer.Ordinal);
            for (int i = start; i < args.Length; ++i)
            {
                string argument = args[i];
                if (!argument.StartsWith ("--", StringComparison.Ordinal) || argument.Length == 2)
                    throw new CommandLineException ("Invalid option: " + argument);
                string name = argument.Substring (2);
                if (options.ContainsKey (name))
                    throw new CommandLineException ("Duplicate option: --" + name);
                if (name == "trusted-inputs" || name == "overwrite")
                {
                    options.Add (name, "true");
                    continue;
                }
                if (++i >= args.Length || args[i].StartsWith ("--", StringComparison.Ordinal))
                    throw new CommandLineException ("Missing value for --" + name);
                options.Add (name, args[i]);
            }
            return options;
        }

        static void RequireTrustedInputs (IDictionary<string, string> options)
        {
            if (!HasFlag (options, "trusted-inputs"))
                throw new CommandLineException ("BinaryFormatter inputs are unsafe. Pass --trusted-inputs only for reviewed repository artifacts.");
        }

        static void EnsureOnly (IDictionary<string, string> options, params string[] names)
        {
            var allowed = new HashSet<string> (names, StringComparer.Ordinal);
            foreach (string name in options.Keys)
            {
                if (!allowed.Contains (name))
                    throw new CommandLineException ("Unknown option: --" + name);
            }
        }

        static string RequireValue (IDictionary<string, string> options, string name)
        {
            string value;
            if (!options.TryGetValue (name, out value) || string.IsNullOrWhiteSpace (value))
                throw new CommandLineException ("Missing required option --" + name);
            return value;
        }

        static bool HasFlag (IDictionary<string, string> options, string name)
        {
            return options.ContainsKey (name);
        }

        static void EnsureDistinctInputs (params string[] paths)
        {
            var seen = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths.Select (Path.GetFullPath))
            {
                if (!seen.Add (path))
                    throw new CommandLineException ("Base, ours, and theirs must be distinct files.");
            }
        }

        static void EnsurePathsAreDistinct (string message, string output, params string[] inputs)
        {
            string fullOutput = Path.GetFullPath (output);
            foreach (string input in inputs)
            {
                if (string.Equals (fullOutput, Path.GetFullPath (input), StringComparison.OrdinalIgnoreCase))
                    throw new CommandLineException (message);
            }
        }

        static void WriteJsonAtomic (string path, object value, bool overwrite)
        {
            string fullPath = Path.GetFullPath (path);
            string directory = Path.GetDirectoryName (fullPath);
            if (string.IsNullOrEmpty (directory) || !Directory.Exists (directory))
                throw new SchemeDatabaseException ("Report directory does not exist.");
            if (0 != (File.GetAttributes (directory) & FileAttributes.ReparsePoint))
                throw new SchemeDatabaseException ("Report directory is a reparse point.");
            if (File.Exists (fullPath)
                && 0 != (File.GetAttributes (fullPath) & FileAttributes.ReparsePoint))
            {
                throw new SchemeDatabaseException ("Report path is a reparse point.");
            }
            if (File.Exists (fullPath) && !overwrite)
                throw new SchemeDatabaseException ("Report already exists; pass --overwrite deliberately.");
            string temporary = Path.Combine (directory, "." + Path.GetFileName (fullPath)
                + "." + Guid.NewGuid().ToString ("N") + ".tmp");
            try
            {
                string json = JsonConvert.SerializeObject (value, JsonSettings) + "\n";
                File.WriteAllText (temporary, json, new UTF8Encoding (false));
                if (File.Exists (fullPath))
                {
                    string backup = temporary + ".bak";
                    File.Replace (temporary, fullPath, backup, true);
                    File.Delete (backup);
                }
                else
                {
                    File.Move (temporary, fullPath);
                }
            }
            finally
            {
                if (File.Exists (temporary))
                    File.Delete (temporary);
            }
        }

        static string ComputeFileHash (string path)
        {
            using (var input = File.OpenRead (path))
            using (var sha = SHA256.Create())
                return SchemeDatabaseFile.ToHex (sha.ComputeHash (input));
        }

        static void WriteResult (object value)
        {
            Console.WriteLine (JsonConvert.SerializeObject (value, new JsonSerializerSettings {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore,
            }));
        }

        const string Usage =
            "Usage:\n"
          + "  Onachi-GARbro.SchemeTool.exe database inspect --input FILE --report FILE --trusted-inputs [--overwrite]\n"
          + "  Onachi-GARbro.SchemeTool.exe database analyze --base FILE --ours FILE --theirs FILE --report FILE --trusted-inputs [--overwrite]\n"
          + "  Onachi-GARbro.SchemeTool.exe database merge --base FILE --ours FILE --theirs FILE --output FILE --report FILE --trusted-inputs [--overwrite]\n"
          + "  Onachi-GARbro.SchemeTool.exe database create-test-fixtures --directory DIR [--overwrite]";
    }

    [Serializable]
    internal sealed class SchemeMergeTestScheme : ResourceScheme
    {
        public string Label;
        public Dictionary<string, int> Values;
    }
}
