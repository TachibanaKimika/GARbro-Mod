using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameRes;
using GameRes.Formats.KiriKiri;

namespace GARbro.Cli
{
    internal static class HxV4Commands
    {
        public static ExitCode Schemes (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions();
            command.RequirePositionalCount (0);
            var schemes = AvailableHxSchemes();
            output.Complete (
                command.CommandName, "success",
                new Dictionary<string, object> {
                    { "count", schemes.Length },
                    { "schemes", schemes },
                });
            return ExitCode.Success;
        }

        public static ExitCode Hash (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("kind");
            command.RequirePositionalCount (1);
            var value = command.RequirePositional (0, "name or path");
            var kind = command.GetSingle ("kind", "file").ToLowerInvariant();
            string hash;
            if ("file" == kind)
                hash = HxV4Tools.GetFileNameHash (value);
            else if ("path" == kind)
                hash = HxV4Tools.GetPathHash (value);
            else
                throw CliException.Usage (
                    "invalid_hxv4_hash_kind", "--kind must be file or path.");

            var data = new Dictionary<string, object> {
                { "kind", kind },
                { "value", value },
                { "hash", hash },
            };
            output.Complete (command.CommandName, "success", data);
            return ExitCode.Success;
        }

        public static ExitCode Generate (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions (
                "destination", "source-dir", "source-file", "krkrdump-dir",
                "seed", "max-files", "include-garbro-common");
            command.RequirePositionalCount (0);
            var destination = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination))
                throw CliException.Usage (
                    "missing_destination", "hxv4 generate requires --destination FILE.");

            var options = new HxV4SourceOptions {
                MaxFiles = checked ((int)command.GetInt64 (
                    "max-files", 100000, 1, int.MaxValue)),
                IncludeGarbroCommonCandidates =
                    command.HasFlag ("include-garbro-common"),
                CancellationRequested = () => CancellationState.IsRequested,
            };
            AddMany (options.SourceDirectories, command.GetMany ("source-dir"));
            AddMany (options.SourceFiles, command.GetMany ("source-file"));
            AddMany (options.KrkrDumpDirectories, command.GetMany ("krkrdump-dir"));
            AddMany (options.SeedNamesFiles, command.GetMany ("seed"));

            var result = HxV4Tools.GenerateFromSources (
                options, Path.GetFullPath (destination));
            if (!result.Success)
                throw CliException.Invalid (
                    "hxv4_generation_failed", result.Error ?? "Hx v4 generation failed.");
            output.Complete (command.CommandName, "success", result);
            return ExitCode.Success;
        }

        public static ExitCode GenerateArchive (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("destination", "scheme", "seed");
            command.RequirePositionalCount (1);
            var archive = runtime.RequireFile (
                command.RequirePositional (0, "Hx v4 archive"));
            var destination = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination))
                throw CliException.Usage (
                    "missing_destination",
                    "hxv4 generate-archive requires --destination FILE.");
            var scheme_name = command.GetSingle ("scheme");
            if (string.IsNullOrWhiteSpace (scheme_name))
                throw CliException.Usage (
                    "missing_hxv4_scheme",
                    "hxv4 generate-archive requires --scheme NAME.");
            var crypt = FindHxScheme (scheme_name);
            if (null == crypt)
            {
                throw CliException.Invalid (
                    "hxv4_scheme_not_found",
                    "The requested Hx v4 scheme was not found.",
                    new Dictionary<string, object> {
                        { "requestedScheme", scheme_name },
                        { "availableSchemes", AvailableHxSchemes() },
                    });
            }

            var seeds = new Dictionary<string, string> (
                StringComparer.OrdinalIgnoreCase);
            foreach (var seed_file in command.GetMany ("seed"))
            {
                Dictionary<string, string> table;
                string error;
                if (!HxV4Tools.TryReadNamesFile (seed_file, out table, out error))
                    throw CliException.Invalid ("invalid_hxv4_names", error,
                        new Dictionary<string, object> { { "path", seed_file } });
                foreach (var pair in table)
                    seeds[pair.Key] = pair.Value;
            }

            var result = HxNameGenerator.Generate (
                archive, crypt, seeds, Path.GetFullPath (destination),
                progress => {
                    CancellationState.ThrowIfRequested();
                    if (output.IsJsonLines)
                    {
                        output.WriteEvent (
                            command.CommandName, "progress", "running",
                            new Dictionary<string, object> {
                                { "percentage", progress.Percentage },
                                { "message", progress.Message },
                            });
                    }
                });
            if (!result.Success)
                throw CliException.Invalid (
                    "hxv4_generation_failed", result.Error ?? "Hx v4 generation failed.");
            output.Complete (command.CommandName, "success", result);
            return ExitCode.Success;
        }

        public static ExitCode Clean (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("deobfuscated-dir", "destination");
            command.RequirePositionalCount (1);
            var names_file = runtime.RequireFile (
                command.RequirePositional (0, "HxNames file"));
            var deobfuscated_directory = command.GetSingle ("deobfuscated-dir");
            if (string.IsNullOrWhiteSpace (deobfuscated_directory))
                throw CliException.Usage (
                    "missing_deobfuscated_directory",
                    "hxv4 clean requires --deobfuscated-dir DIR.");
            var destination = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination))
                throw CliException.Usage (
                    "missing_destination", "hxv4 clean requires --destination FILE.");

            var result = HxV4Tools.GenerateCleanNamesFile (
                names_file, runtime.RequireDirectory (deobfuscated_directory),
                destination);
            output.Complete (command.CommandName, "success", result);
            return ExitCode.Success;
        }

        public static ExitCode FindMissingVoices (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("voice-dir");
            command.RequirePositionalCount (0);
            var directories = command.GetMany ("voice-dir")
                .Select (runtime.RequireDirectory)
                .ToArray();
            if (0 == directories.Length)
                throw CliException.Usage (
                    "missing_voice_directory",
                    "hxv4 find-missing-voices requires --voice-dir DIR.");

            var result = HxV4Tools.FindMissingVoices (
                directories, () => CancellationState.IsRequested);
            if (output.IsJsonLines)
            {
                foreach (var stem in result.MissingVoiceStems)
                {
                    output.WriteEvent (
                        command.CommandName, "missing_voice", "success",
                        new Dictionary<string, object> {
                            { "stem", stem },
                            { "fileName", stem + ".ogg" },
                        });
                }
                output.Complete (
                    command.CommandName, "success",
                    MissingVoiceSummary (result));
            }
            else
            {
                if (output.IsText)
                {
                    foreach (var stem in result.MissingVoiceStems)
                        output.WriteText (stem);
                    output.WriteText (string.Format (
                        "SUMMARY scanned={0} prefixes={1} candidates={2} missing={3}",
                        result.ScannedFiles, result.PrefixCount,
                        result.CandidateCount, result.MissingCount));
                    output.Complete (command.CommandName, "success", null);
                    return ExitCode.Success;
                }
                output.Complete (command.CommandName, "success", result);
            }
            return ExitCode.Success;
        }

        public static ExitCode RestoreStructure (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("recursive", "dry-run");
            command.RequirePositionalCount (1);
            var result = HxV4Tools.RestoreDirectoryStructure (
                runtime.RequireDirectory (
                    command.RequirePositional (0, "extracted directory")),
                command.HasFlag ("recursive"), command.HasFlag ("dry-run"));
            WriteFileEvents (command, output, result);
            var status = result.Failed > 0 ? "partial_success" : "success";
            output.Complete (command.CommandName, status, result);
            return result.Failed > 0 ? ExitCode.PartialSuccess : ExitCode.Success;
        }

        public static ExitCode Rename (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("names", "dry-run");
            command.RequirePositionalCount (1);
            var names_file = command.GetSingle ("names");
            if (string.IsNullOrWhiteSpace (names_file))
                throw CliException.Usage (
                    "missing_hxv4_names", "hxv4 rename requires --names FILE.");
            names_file = runtime.RequireFile (names_file);
            var result = HxV4Tools.RenameExtractedTree (
                runtime.RequireDirectory (
                    command.RequirePositional (0, "extracted directory")),
                names_file, command.HasFlag ("dry-run"));
            WriteFileEvents (command, output, result);
            var status = result.Failed > 0 ? "partial_success" : "success";
            output.Complete (command.CommandName, status, result);
            return result.Failed > 0 ? ExitCode.PartialSuccess : ExitCode.Success;
        }

        public static ExitCode KrkrDump (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions (
                "game-executable", "destination", "tool-directory",
                "no-elevate", "same-directory", "run-only");
            command.RequirePositionalCount (1);
            var archive = runtime.RequireFile (
                command.RequirePositional (0, "Hx v4 archive"));
            var game_executable = command.GetSingle ("game-executable");
            if (string.IsNullOrWhiteSpace (game_executable))
                throw CliException.Usage (
                    "missing_game_executable",
                    "hxv4 krkrdump requires --game-executable EXE.");
            game_executable = runtime.RequireFile (game_executable);
            var destination = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination))
                throw CliException.Usage (
                    "missing_destination",
                    "hxv4 krkrdump requires --destination DIR.");
            destination = Path.GetFullPath (destination);
            var dump_directory = Path.Combine (destination, ".krkrdump");
            if (Directory.Exists (dump_directory))
            {
                throw CliException.Conflict (
                    "krkrdump_destination_exists",
                    "The KrkrDump destination already contains a run result. "
                    + "Choose a new destination or use hxv4 krkrdump-import.",
                    new Dictionary<string, object> {
                        { "path", dump_directory },
                    });
            }

            var runner = new HxV4KrkrDumpRunner();
            ResourceParameterCommandResult dump_result;
            try
            {
                dump_result = runner.Run (
                    new HxV4KrkrDumpRunRequest {
                        SourceArchive = archive,
                        GameExecutable = game_executable,
                        OutputDirectory = destination,
                        ToolDirectory = command.GetSingle ("tool-directory"),
                        Elevate = !command.HasFlag ("no-elevate"),
                        CancellationRequested = () => CancellationState.IsRequested,
                    },
                    status => {
                        if (output.IsJsonLines)
                        {
                            output.WriteEvent (
                                command.CommandName, "progress", "running",
                                new Dictionary<string, object> {
                                    { "stage", status },
                                });
                        }
                    });
            }
            catch (HxV4KrkrDumpRuntimeMissingException X)
            {
                throw CliException.Invalid (
                    "krkrdump_runtime_missing", X.Message,
                    new Dictionary<string, object> {
                        { "architecture", X.Architecture },
                        { "sourceRepository", HxV4KrkrDumpRunner.SourceRepositoryUrl },
                    });
            }
            if (!dump_result.Success)
            {
                throw CliException.Invalid (
                    "krkrdump_no_output",
                    dump_result.Message ?? "KrkrDump produced no output.",
                    DumpResultData (dump_result, null));
            }

            if (command.HasFlag ("run-only"))
            {
                output.Complete (command.CommandName, "success",
                    DumpResultData (dump_result, null));
                return ExitCode.Success;
            }

            return ImportKrkrDumpResult (
                command, output, dump_result, archive,
                command.HasFlag ("same-directory"));
        }

        public static ExitCode KrkrDumpImport (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions (
                "result-dir", "game-executable", "same-directory");
            command.RequirePositionalCount (1);
            var archive = runtime.RequireFile (
                command.RequirePositional (0, "Hx v4 archive"));
            var result_directory = command.GetSingle ("result-dir");
            if (string.IsNullOrWhiteSpace (result_directory))
                throw CliException.Usage (
                    "missing_krkrdump_result_directory",
                    "hxv4 krkrdump-import requires --result-dir DIR.");
            if (!Directory.Exists (result_directory))
                throw CliException.Invalid (
                    "krkrdump_result_directory_not_found",
                    "The KrkrDump result directory was not found.",
                    new Dictionary<string, object> {
                        { "path", Path.GetFullPath (result_directory) },
                    });
            var game_executable = command.GetSingle ("game-executable");
            if (!string.IsNullOrWhiteSpace (game_executable))
                game_executable = runtime.RequireFile (game_executable);

            var dump_result = HxV4KrkrDumpRunner.CollectExistingResult (
                archive, result_directory, game_executable);
            return ImportKrkrDumpResult (
                command, output, dump_result, archive,
                command.HasFlag ("same-directory"));
        }

        static ExitCode ImportKrkrDumpResult (
            ParsedCommand command, MachineOutput output,
            ResourceParameterCommandResult dump_result, string archive,
            bool include_same_directory)
        {
            var import = KrkrDumpResultImporter.Import (
                dump_result, archive, include_same_directory,
                progress => {
                    CancellationState.ThrowIfRequested();
                    if (output.IsJsonLines)
                    {
                        output.WriteEvent (
                            command.CommandName, "progress", "running",
                            new Dictionary<string, object> {
                                { "percentage", progress.Percentage },
                                { "message", progress.Message },
                            });
                    }
                });
            if (!import.Success)
                throw CliException.Invalid (
                    "krkrdump_import_failed", import.Message,
                    DumpResultData (dump_result, import));

            output.Complete (
                command.CommandName, "success",
                DumpResultData (dump_result, import));
            return ExitCode.Success;
        }

        static HxCrypt FindHxScheme (string requested)
        {
            foreach (var pair in Xp3Opener.KnownSchemes)
            {
                if (pair.Key.Equals (requested, StringComparison.OrdinalIgnoreCase))
                    return pair.Value as HxCrypt;
            }
            return null;
        }

        static string[] AvailableHxSchemes ()
        {
            return Xp3Opener.KnownSchemes
                .Where (x => x.Value is HxCrypt)
                .Select (x => x.Key)
                .OrderBy (x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static Dictionary<string, object> DumpResultData (
            ResourceParameterCommandResult dump, KrkrDumpImportResult import)
        {
            var data = new Dictionary<string, object> {
                { "outputDirectory", dump.OutputDirectory },
                { "logFile", dump.LogFileName },
                { "metadata", dump.Metadata },
                { "imported", null != import && import.Success },
            };
            if (null != import)
            {
                data["schemeName"] = import.SchemeName;
                data["message"] = import.Message;
                string source_archive;
                dump.Metadata.TryGetValue ("SourceArchive", out source_archive);
                data["generatedNamesFile"] =
                    KrkrDumpResultImporter.GetAutomaticNamesCacheFile (
                        dump, source_archive);
            }
            return data;
        }

        static void WriteFileEvents (
            ParsedCommand command, MachineOutput output,
            HxV4FileOperationResult result)
        {
            if (output.IsJsonLines)
            {
                foreach (var item in result.Items)
                {
                    output.WriteEvent (
                        command.CommandName, "file", item.Status, item);
                }
            }
            else if (output.IsText)
            {
                foreach (var item in result.Items)
                {
                    output.WriteText (
                        string.Format ("{0} {1} => {2}",
                            item.Status.ToUpperInvariant(), item.Source,
                            item.Destination));
                }
            }
        }

        static void AddMany (IList<string> target, IEnumerable<string> values)
        {
            foreach (var value in values)
                target.Add (value);
        }

        static Dictionary<string, object> MissingVoiceSummary (
            HxV4MissingVoiceResult result)
        {
            return new Dictionary<string, object> {
                { "scannedFiles", result.ScannedFiles },
                { "prefixCount", result.PrefixCount },
                { "candidateCount", result.CandidateCount },
                { "missingCount", result.MissingCount },
            };
        }
    }
}
