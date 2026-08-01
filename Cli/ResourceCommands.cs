using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using GameRes;

namespace GARbro.Cli
{
    internal static class ResourceCommands
    {
        static readonly string[] s_commands = {
            "capabilities", "formats.list", "probe", "archive.list",
            "archive.plan", "archive.extract", "archive.schemes",
            "archive.scheme-info", "archive.scheme-check",
            "script.extract", "image.info", "image.convert", "image.convert-batch",
            "hxv4.schemes", "hxv4.hash", "hxv4.generate", "hxv4.generate-archive",
            "hxv4.clean", "hxv4.find-missing-voices",
            "hxv4.restore-structure", "hxv4.rename",
            "hxv4.krkrdump", "hxv4.krkrdump-import",
        };

        public static ExitCode Capabilities (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions();
            command.RequirePositionalCount (0);
            string assembly_dir = Path.GetDirectoryName (
                Assembly.GetExecutingAssembly().Location);
            var krkrdump_runner = new HxV4KrkrDumpRunner();
            var krkrdump_x86 = krkrdump_runner.ResolveToolDirectory (
                null, "x86");
            var krkrdump_x64 = krkrdump_runner.ResolveToolDirectory (
                null, "x64");
            var data = new Dictionary<string, object> {
                { "protocolVersions", new[] { MachineOutput.SchemaVersion } },
                { "outputFormats", new[] { "json", "jsonl", "text" } },
                { "commands", s_commands },
                { "nonInteractive", true },
                { "formatCounts", new Dictionary<string, object> {
                    { "archive", runtime.Catalog.ArcFormats.Count() },
                    { "image", runtime.Catalog.ImageFormats.Count() },
                    { "audio", runtime.Catalog.AudioFormats.Count() },
                    { "script", runtime.Catalog.ScriptFormats.Count() },
                } },
                { "safety", new Dictionary<string, object> {
                    { "pathContainment", true },
                    { "atomicWrites", true },
                    { "actualByteCounting", true },
                    { "dryRun", true },
                    { "duplicatePolicies", new[] { "error", "suffix-index" } },
                    { "resumeModes", new[] { "verify-size", "verify-hash" } },
                    { "imageBatchResumeModes", new[] { "verify-header", "verify-decode" } },
                    { "extractionManifestSchema", ExtractionManifestState.SchemaVersion },
                    { "automaticFiniteBudget", true },
                    { "summaryOnly", true },
                    { "explicitXp3SchemeOptions", true },
                    { "defaultOverwrite", "never" },
                    { "defaultMaxFiles", ExtractionPolicy.DefaultMaxFiles },
                    { "defaultMaxTotalBytes", ExtractionPolicy.DefaultMaxTotalBytes },
                    { "defaultMaxEntryBytes", ExtractionPolicy.DefaultMaxEntryBytes },
                    { "defaultMaxDepth", ExtractionPolicy.DefaultMaxDepth },
                } },
                { "optionalComponents", new[] {
                    new Dictionary<string, object> {
                        { "name", "ArcExtra" },
                        { "available", File.Exists (Path.Combine (assembly_dir, "ArcExtra.dll")) },
                        { "path", Path.Combine (assembly_dir, "ArcExtra.dll") },
                    },
                    new Dictionary<string, object> {
                        { "name", "KrkrDump-x86" },
                        { "available", !string.IsNullOrEmpty (krkrdump_x86) },
                        { "path", krkrdump_x86 ?? Path.Combine (
                            assembly_dir, "Tools", "KrkrDump", "x86") },
                    },
                    new Dictionary<string, object> {
                        { "name", "KrkrDump-x64" },
                        { "available", !string.IsNullOrEmpty (krkrdump_x64) },
                        { "path", krkrdump_x64 ?? Path.Combine (
                            assembly_dir, "Tools", "KrkrDump", "x64") },
                    },
                } },
            };
            output.Complete (command.CommandName, "success", data);
            return ExitCode.Success;
        }

        public static ExitCode FormatsList (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("kind");
            command.RequirePositionalCount (0);
            string kind = command.GetSingle ("kind", "all").ToLowerInvariant();
            if (!new[] { "all", "archive", "image", "audio", "script" }.Contains (kind))
                throw CliException.Usage ("invalid_format_kind",
                    "--kind must be one of: all, archive, image, audio, script.");

            var formats = runtime.Catalog.Formats
                .Where (x => "all" == kind || kind == x.Type)
                .OrderBy (x => x.Type, StringComparer.Ordinal)
                .ThenBy (x => x.Tag, StringComparer.Ordinal)
                .Select (FormatData)
                .ToList();
            if (output.IsJsonLines)
            {
                foreach (var format in formats)
                    output.WriteEvent (command.CommandName, "result", "success", format);
            }
            else if (output.IsText)
            {
                foreach (var format in formats)
                    output.WriteText (string.Format (
                        CultureInfo.InvariantCulture, "{0,-8} {1,-24} {2}",
                        format["kind"], format["tag"], format["description"]));
            }

            var result = new Dictionary<string, object> {
                { "kind", kind },
                { "count", formats.Count },
            };
            if (output.IsJson)
                result["formats"] = formats;
            output.Complete (command.CommandName, "success", result);
            return ExitCode.Success;
        }

        public static ExitCode Probe (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("scheme", "hx-names", "cx-dump-dir");
            command.RequirePositionalCount (1);
            string path = runtime.RequireFile (
                command.RequirePositional (0, "input path"));
            var candidates = new List<Dictionary<string, object>>();

            runtime.BeginRecognition();
            if (new FileInfo (path).Length < 4)
            {
                runtime.ThrowRecognitionFailure (path);
                return ExitCode.Unrecognized;
            }
            ArchiveSchemeResolution scheme_resolution =
                ArchiveSchemeOptions.Resolve (runtime, command, path);
            using (var archive = null != scheme_resolution
                ? runtime.OpenArchive (path, scheme_resolution)
                : ArcFile.TryOpen (path))
            {
                if (null != archive)
                {
                    scheme_resolution = ArchiveSchemeOptions.FinalizeAfterOpen (
                        archive, scheme_resolution, path);
                    var data = DetectionData (
                        path, "archive", archive.Tag, archive.Description);
                    data["entryCount"] = archive.Dir.Count;
                    if (null != scheme_resolution)
                        data["schemeResolution"] = scheme_resolution.ToDictionary();
                    CompleteDetection (command, output, data, candidates);
                    return ExitCode.Success;
                }
            }

            using (var input = BinaryStream.FromFile (path))
            {
                var image = ImageFormat.FindFormat (input);
                if (null != image)
                {
                    var data = DetectionData (
                        path, "image", image.Item1.Tag, image.Item1.Description);
                    AddImageMetadata (data, image.Item2);
                    CompleteDetection (command, output, data, candidates);
                    return ExitCode.Success;
                }
            }

            AudioFormat audio_format = FindAudioFormat (runtime, path);
            if (null != audio_format)
            {
                var data = DetectionData (
                    path, "audio", audio_format.Tag, audio_format.Description);
                CompleteDetection (command, output, data, candidates);
                return ExitCode.Success;
            }

            using (var input = BinaryStream.FromFile (path))
            {
                var script = ScriptFormat.FindFormat (input);
                if (null != script)
                {
                    var data = DetectionData (
                        path, "script", script.Tag, script.Description);
                    AddScriptModes (data, script);
                    CompleteDetection (command, output, data, candidates);
                    return ExitCode.Success;
                }
            }
            runtime.ThrowRecognitionFailure (path);
            return ExitCode.Unrecognized;
        }

        public static ExitCode ImageInfo (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions();
            command.RequirePositionalCount (1);
            string path = runtime.RequireFile (
                command.RequirePositional (0, "image path"));
            runtime.BeginRecognition();
            if (new FileInfo (path).Length < 4)
            {
                runtime.ThrowRecognitionFailure (path);
                return ExitCode.Unrecognized;
            }
            using (var input = BinaryStream.FromFile (path))
            {
                var result = ImageFormat.FindFormat (input);
                if (null == result)
                {
                    runtime.ThrowRecognitionFailure (path);
                    return ExitCode.Unrecognized;
                }
                var data = DetectionData (
                    path, "image", result.Item1.Tag, result.Item1.Description);
                AddImageMetadata (data, result.Item2);
                output.Complete (command.CommandName, "success", data);
            }
            return ExitCode.Success;
        }

        public static ExitCode ImageConvert (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions (
                "format", "destination", "overwrite", "dry-run",
                "max-files", "max-total-bytes", "max-entry-bytes", "max-depth");
            command.RequirePositionalCount (1);
            string path = runtime.RequireFile (
                command.RequirePositional (0, "image path"));
            string format_name = command.GetSingle ("format");
            if (string.IsNullOrWhiteSpace (format_name))
                throw CliException.Usage ("missing_format",
                    "image convert requires --format TAG_OR_EXTENSION.");
            string destination = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination))
                throw CliException.Usage ("missing_destination",
                    "image convert requires --destination DIR.");

            ImageFormat target = FindWritableImageFormat (
                runtime.Catalog, format_name);
            if (null == target)
                throw CliException.Invalid (
                    "output_format_not_found",
                    "No writable image format matches: " + format_name);
            string extension = target.Extensions.FirstOrDefault();
            if (string.IsNullOrEmpty (extension))
                throw new InvalidOperationException (
                    "Writable image format has no filename extension: " + target.Tag);

            ExtractionPolicy policy = ExtractionPolicy.FromCommand (command);
            var resolver = new OutputPathResolver (destination, policy.MaxDepth);
            string output_name = Path.ChangeExtension (
                Path.GetFileName (path), extension);
            string output_path = resolver.Resolve (output_name);
            EnsureNoConflict (output_path, policy.Overwrite);

            runtime.BeginRecognition();
            Tuple<ImageFormat, ImageMetaData> source;
            using (var input = BinaryStream.FromFile (path))
                source = ImageFormat.FindFormat (input);
            if (null == source)
            {
                runtime.ThrowRecognitionFailure (path);
                return ExitCode.Unrecognized;
            }

            var data = new Dictionary<string, object> {
                { "sourcePath", path },
                { "sourceTag", source.Item1.Tag },
                { "targetTag", target.Tag },
                { "destination", output_path },
                { "dryRun", policy.DryRun },
            };
            AddImageMetadata (data, source.Item2);
            if (policy.DryRun)
            {
                data["status"] = "planned";
                output.Complete (command.CommandName, "success", data);
                return ExitCode.Success;
            }
            if (OverwriteMode.Skip == policy.Overwrite
                && File.Exists (output_path))
            {
                data["status"] = "skipped";
                data["bytesWritten"] = 0;
                output.Complete (
                    command.CommandName, "partial_success", data);
                return ExitCode.PartialSuccess;
            }

            ImageData image;
            using (var input = BinaryStream.FromFile (path))
            {
                input.Position = 0;
                image = source.Item1.Read (input, source.Item2);
            }
            var budget = new ExtractionBudget (policy);
            long written = SafeFileWriter.WriteToFile (
                output_path, policy.Overwrite, budget,
                stream => target.Write (stream, image));
            data["status"] = "written";
            data["bytesWritten"] = written;
            output.Complete (command.CommandName, "success", data);
            return ExitCode.Success;
        }

        public static ExitCode ScriptExtract (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions (
                "entry", "mode", "destination", "overwrite", "dry-run",
                "max-files", "max-total-bytes", "max-entry-bytes", "max-depth");
            command.RequirePositionalCount (1);
            string source_path = runtime.RequireFile (
                command.RequirePositional (0, "script or archive path"));
            string destination = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination))
                throw CliException.Usage ("missing_destination",
                    "script extract requires --destination DIR.");
            string mode = NormalizeScriptMode (command.GetSingle ("mode"));
            ExtractionPolicy policy = ExtractionPolicy.FromCommand (command);
            var resolver = new OutputPathResolver (destination, policy.MaxDepth);

            string entry_name = command.GetSingle ("entry");
            ArcFile archive = null;
            IBinaryStream input = null;
            string logical_name = Path.GetFileName (source_path);
            try
            {
                runtime.BeginRecognition();
                if (!string.IsNullOrWhiteSpace (entry_name))
                {
                    archive = runtime.OpenArchive (source_path);
                    Entry entry = FindExactEntry (archive, entry_name);
                    logical_name = entry.Name;
                    input = archive.OpenBinaryEntry (entry);
                }
                else
                {
                    input = BinaryStream.FromFile (source_path);
                }

                if (input.Length < 4)
                {
                    runtime.ThrowRecognitionFailure (logical_name);
                    return ExitCode.Unrecognized;
                }
                ScriptFormat format = ScriptFormat.FindFormat (input);
                if (null == format)
                {
                    runtime.ThrowRecognitionFailure (logical_name);
                    return ExitCode.Unrecognized;
                }
                var configurable = format as IConfigurableScriptFormat;
                if (null != configurable
                    && !configurable.TextModes.Any (
                        x => string.Equals (x, mode,
                                           StringComparison.OrdinalIgnoreCase)))
                {
                    throw CliException.Invalid (
                        "script_mode_not_supported",
                        "The selected script handler does not support mode: " + mode,
                        new Dictionary<string, object> {
                            { "formatTag", format.Tag },
                            { "requestedMode", mode },
                            { "availableModes", configurable.TextModes.ToArray() },
                        });
                }
                if (null == configurable && ScriptTextMode.Filtered != mode)
                {
                    throw CliException.Invalid (
                        "script_mode_not_supported",
                        "The selected script handler only supports filtered output.",
                        new Dictionary<string, object> {
                            { "formatTag", format.Tag },
                            { "requestedMode", mode },
                            { "availableModes", new[] { ScriptTextMode.Filtered } },
                        });
                }

                string output_name = GetScriptOutputName (logical_name, mode);
                string output_path = resolver.Resolve (output_name);
                EnsureNoConflict (output_path, policy.Overwrite);
                var data = new Dictionary<string, object> {
                    { "sourcePath", source_path },
                    { "entry", entry_name },
                    { "formatTag", format.Tag },
                    { "mode", mode },
                    { "destination", output_path },
                    { "dryRun", policy.DryRun },
                };
                if (policy.DryRun)
                {
                    data["status"] = "planned";
                    output.Complete (command.CommandName, "success", data);
                    return ExitCode.Success;
                }
                if (OverwriteMode.Skip == policy.Overwrite
                    && File.Exists (output_path))
                {
                    data["status"] = "skipped";
                    data["bytesWritten"] = 0;
                    output.Complete (
                        command.CommandName, "partial_success", data);
                    return ExitCode.PartialSuccess;
                }

                input.Position = 0;
                Stream converted = null != configurable
                    ? configurable.ConvertFrom (input, mode)
                    : format.ConvertFrom (input);
                long written;
                var budget = new ExtractionBudget (policy);
                using (converted)
                {
                    written = SafeFileWriter.CopyToFile (
                        converted, output_path, policy.Overwrite, budget);
                }
                data["status"] = "written";
                data["bytesWritten"] = written;
                output.Complete (command.CommandName, "success", data);
                return ExitCode.Success;
            }
            catch (OperationCanceledException)
            {
                runtime.TranslateParameterCancellation (logical_name);
                throw;
            }
            finally
            {
                if (null != input)
                    input.Dispose();
                if (null != archive)
                    archive.Dispose();
            }
        }

        static Entry FindExactEntry (ArcFile archive, string name)
        {
            var exact = archive.Dir.Where (
                x => string.Equals (x.Name, name, StringComparison.Ordinal)).ToList();
            if (1 == exact.Count)
                return exact[0];
            var insensitive = archive.Dir.Where (
                x => string.Equals (x.Name, name,
                                   StringComparison.OrdinalIgnoreCase)).ToList();
            if (1 == insensitive.Count)
                return insensitive[0];
            if (insensitive.Count > 1)
                throw CliException.Invalid (
                    "ambiguous_entry", "Archive entry name is ambiguous: " + name);
            throw CliException.Invalid (
                "entry_not_found", "Archive entry was not found: " + name);
        }

        static string NormalizeScriptMode (string mode)
        {
            if (string.IsNullOrWhiteSpace (mode))
                throw CliException.Usage ("missing_script_mode",
                    "script extract requires --mode filtered|raw|dump|jsonl.");
            mode = mode.ToLowerInvariant();
            if (ScriptTextMode.Filtered != mode && ScriptTextMode.Raw != mode
                && ScriptTextMode.Dump != mode
                && ScriptTextMode.JsonLines != mode)
            {
                throw CliException.Usage (
                    "invalid_script_mode",
                    "--mode must be one of: filtered, raw, dump, jsonl.");
            }
            return mode;
        }

        static string GetScriptOutputName (string name, string mode)
        {
            string extension = ScriptTextMode.JsonLines == mode ? "jsonl"
                : ScriptTextMode.Raw == mode ? "raw.txt"
                : ScriptTextMode.Dump == mode ? "dump.txt" : "txt";
            string normalized = name.Replace ('\\', '/');
            return Path.ChangeExtension (normalized, extension);
        }

        static ImageFormat FindWritableImageFormat (
            FormatCatalog catalog, string name)
        {
            string normalized = name.TrimStart ('.');
            return catalog.ImageFormats.FirstOrDefault (
                       x => x.CanWrite
                           && string.Equals (x.Tag, normalized,
                                             StringComparison.OrdinalIgnoreCase))
                ?? catalog.LookupExtension<ImageFormat> (normalized)
                          .FirstOrDefault (x => x.CanWrite);
        }

        static AudioFormat FindAudioFormat (RuntimeContext runtime, string path)
        {
            uint signature = 0;
            using (var input = File.OpenRead (path))
            {
                if (input.Length >= 4)
                    signature = FormatCatalog.ReadSignature (input);
            }
            var candidates = runtime.Catalog.LookupFileName (path)
                .OfType<AudioFormat>()
                .Concat (runtime.Catalog.LookupSignature<AudioFormat> (signature))
                .Distinct()
                .ToList();
            foreach (var format in candidates)
            {
                runtime.BeginRecognition();
                try
                {
                    using (var input = BinaryStream.FromFile (path))
                    using (var sound = format.TryOpen (input))
                    {
                        if (null != sound)
                            return format;
                    }
                }
                catch (OperationCanceledException)
                {
                    runtime.TranslateParameterCancellation (path);
                    throw;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        static Dictionary<string, object> DetectionData (
            string path, string kind, string tag, string description)
        {
            var info = new FileInfo (path);
            uint signature = 0;
            using (var input = info.OpenRead())
            {
                if (input.Length >= 4)
                    signature = FormatCatalog.ReadSignature (input);
            }
            return new Dictionary<string, object> {
                { "path", path },
                { "kind", kind },
                { "tag", tag },
                { "description", description },
                { "size", info.Length },
                { "extension", info.Extension.TrimStart ('.') },
                { "signature", string.Format (
                    CultureInfo.InvariantCulture, "0x{0:X8}", signature) },
            };
        }

        static void CompleteDetection (
            ParsedCommand command, MachineOutput output,
            Dictionary<string, object> data,
            IList<Dictionary<string, object>> candidates)
        {
            if (candidates.Count > 0)
                data["candidates"] = candidates;
            if (output.IsJsonLines)
                output.WriteEvent (command.CommandName, "result", "success", data);
            else if (output.IsText)
            {
                output.WriteText (string.Format (
                    CultureInfo.InvariantCulture, "{0} [{1}] {2}",
                    data["path"], data["tag"], data["kind"]));
                output.Complete (command.CommandName, "success", null);
                return;
            }
            output.Complete (command.CommandName, "success", data);
        }

        static void AddImageMetadata (
            IDictionary<string, object> data, ImageMetaData metadata)
        {
            data["width"] = metadata.Width;
            data["height"] = metadata.Height;
            data["bitsPerPixel"] = metadata.BPP;
            data["offsetX"] = metadata.OffsetX;
            data["offsetY"] = metadata.OffsetY;
        }

        static void AddScriptModes (
            IDictionary<string, object> data, ScriptFormat script)
        {
            var configurable = script as IConfigurableScriptFormat;
            data["textModes"] = null != configurable
                ? configurable.TextModes.ToArray()
                : new[] { ScriptTextMode.Filtered };
            if (null != configurable)
                data["defaultTextMode"] = configurable.DefaultTextMode;
        }

        static Dictionary<string, object> FormatData (IResource format)
        {
            var data = new Dictionary<string, object> {
                { "kind", format.Type },
                { "tag", format.Tag },
                { "description", format.Description },
                { "extensions", format.Extensions.Where (
                    x => !string.IsNullOrEmpty (x)).Distinct (
                        StringComparer.OrdinalIgnoreCase).ToArray() },
                { "signatures", format.Signatures.Distinct().Select (
                    x => string.Format (
                        CultureInfo.InvariantCulture, "0x{0:X8}", x)).ToArray() },
                { "canWrite", format.CanWrite },
                { "assembly", format.GetType().Assembly.GetName().Name },
            };
            var script = format as ScriptFormat;
            if (null != script)
                AddScriptModes (data, script);
            return data;
        }

        static void EnsureNoConflict (string path, OverwriteMode overwrite)
        {
            if (File.Exists (path) && OverwriteMode.Never == overwrite)
            {
                throw CliException.Conflict (
                    "destination_exists",
                    "Destination file already exists: " + path,
                    new Dictionary<string, object> {
                        { "path", path },
                    });
            }
        }
    }
}
