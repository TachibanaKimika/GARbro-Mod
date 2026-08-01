using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GameRes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GARbro.Cli
{
    internal static class ImageBatchCommands
    {
        const long MinimumPlanHeadroom = 1024L * 1024;
        const long PerImageEstimateHeadroom = 64L * 1024;

        public static ExitCode ConvertBatch (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions (
                "source-root", "destination", "format", "manifest",
                "recursive", "detect-by-signature", "include", "resume",
                "overwrite", "dry-run", "budget", "summary-only",
                "max-files", "max-total-bytes", "max-entry-bytes", "max-depth");
            command.RequirePositionalCount (0);

            string source_root_value = command.GetSingle ("source-root");
            if (string.IsNullOrWhiteSpace (source_root_value))
                throw CliException.Usage (
                    "missing_source_root",
                    "image convert-batch requires --source-root DIR.");
            string destination_value = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination_value))
                throw CliException.Usage (
                    "missing_destination",
                    "image convert-batch requires --destination DIR.");
            string format_name = command.GetSingle ("format");
            if (string.IsNullOrWhiteSpace (format_name))
                throw CliException.Usage (
                    "missing_format",
                    "image convert-batch requires --format TAG_OR_EXTENSION.");

            string source_root = NormalizeDirectory (
                runtime.RequireDirectory (source_root_value));
            RejectSourceRootReparsePoints (source_root);
            string destination = NormalizeDirectoryPath (destination_value);
            RejectDestinationInsideSource (source_root, destination);
            if (File.Exists (destination))
            {
                throw CliException.Invalid (
                    "invalid_destination",
                    "Batch destination is an existing file.",
                    new Dictionary<string, object> { { "path", destination } });
            }

            ImageFormat target = FindWritableImageFormat (
                runtime.Catalog, format_name);
            if (null == target)
                throw CliException.Invalid (
                    "output_format_not_found",
                    "No writable image format matches: " + format_name);
            string target_extension = target.Extensions.FirstOrDefault (
                x => !string.IsNullOrWhiteSpace (x));
            if (string.IsNullOrWhiteSpace (target_extension))
                throw new InvalidOperationException (
                    "Writable image format has no filename extension: " + target.Tag);
            target_extension = target_extension.TrimStart ('.');

            string resume = ParseResumeMode (command.GetSingle ("resume"));
            bool recursive = command.HasFlag ("recursive");
            bool detect_by_signature = command.HasFlag ("detect-by-signature");
            bool summary_only = command.HasFlag ("summary-only");
            string manifest_value = command.GetSingle ("manifest");
            string manifest = string.IsNullOrWhiteSpace (manifest_value)
                ? null : runtime.RequireFile (manifest_value);
            IList<string> includes = command.GetMany ("include");
            ValidateIncludes (includes);

            var known_extensions = new HashSet<string> (
                runtime.Catalog.ImageFormats.SelectMany (x => x.Extensions)
                    .Where (x => !string.IsNullOrWhiteSpace (x))
                    .Select (x => x.TrimStart ('.')),
                StringComparer.OrdinalIgnoreCase);
            SourceScan scan = null == manifest
                ? EnumerateSources (
                    source_root, recursive, detect_by_signature,
                    known_extensions, includes)
                : ReadManifest (source_root, manifest, includes);

            var items = new List<BatchItem>();
            int signature_ignored = 0;
            foreach (SourceReference source in scan.Sources)
            {
                CancellationState.ThrowIfRequested();
                bool known_extension = known_extensions.Contains (
                    Path.GetExtension (source.FullPath).TrimStart ('.'));
                bool signature_only = null == manifest
                    && detect_by_signature && !known_extension;
                BatchItem item = InspectSource (
                    runtime, source, signature_only);
                if (null == item)
                {
                    ++signature_ignored;
                    continue;
                }
                items.Add (item);
            }

            items = items.OrderBy (
                    x => PortablePath (x.Source.RelativePath),
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy (x => PortablePath (x.Source.RelativePath),
                         StringComparer.Ordinal)
                .ToList();
            if (0 == items.Count)
            {
                throw CliException.Invalid (
                    "no_images_selected",
                    "The image batch did not select any recognizable image candidates.",
                    new Dictionary<string, object> {
                        { "sourceRoot", source_root },
                        { "manifest", manifest },
                        { "recursive", recursive },
                        { "detectBySignature", detect_by_signature },
                        { "includes", includes.ToArray() },
                        { "scanned", scan.ScannedFileCount },
                        { "signatureCandidatesIgnored", signature_ignored },
                    });
            }

            var preliminary_resolver = new OutputPathResolver (destination, 1024);
            long estimated_total = 0;
            long maximum_estimate = 0;
            int maximum_depth = 0;
            foreach (BatchItem item in items)
            {
                string output_name = Path.ChangeExtension (
                    item.Source.RelativePath, target_extension);
                item.Output = preliminary_resolver.NormalizeAndValidate (output_name);
                preliminary_resolver.Reserve (item.Output, output_name);
                maximum_depth = Math.Max (maximum_depth, item.Output.Depth);
                if (null == item.Error)
                {
                    item.EstimatedOutputBytes = EstimateOutputBytes (item);
                    estimated_total = AddFinite (
                        estimated_total, item.EstimatedOutputBytes);
                    maximum_estimate = Math.Max (
                        maximum_estimate, item.EstimatedOutputBytes);
                }
            }

            ExtractionLimits automatic_limits = CreateAutomaticLimits (
                items.Count, estimated_total, maximum_estimate, maximum_depth);
            ExtractionPolicy policy = ExtractionPolicy.FromCommand (
                command, automatic_limits);

            var resolver = new OutputPathResolver (destination, policy.MaxDepth);
            foreach (BatchItem item in items)
            {
                string output_name = Path.ChangeExtension (
                    item.Source.RelativePath, target_extension);
                item.Output = resolver.NormalizeAndValidate (output_name);
                resolver.Reserve (item.Output, output_name);
            }

            EnsureOutputsAreNotInputs (
                source_root, manifest, items);
            EnsureFileCount (items.Count, policy.MaxFiles);
            PreflightDestinations (runtime, items, target, resume, policy);
            EnsurePlannedBudget (items, policy);

            var budget = new ExtractionBudget (policy);
            int recognized = items.Count (x => null == x.Error);
            int planned = 0;
            int written = 0;
            int repaired = 0;
            int verified = 0;
            int skipped = 0;
            int failed = 0;
            long bytes_written = 0;

            foreach (BatchItem item in items)
            {
                CancellationState.ThrowIfRequested();
                if (null != item.Error)
                {
                    item.Status = "failed";
                    ++failed;
                    WriteItem (command, output, item, target, summary_only);
                    continue;
                }
                if (item.Verified)
                {
                    item.Status = "verified_existing";
                    ++verified;
                    WriteItem (command, output, item, target, summary_only);
                    continue;
                }
                if (item.Skip)
                {
                    item.Status = "skipped";
                    ++skipped;
                    WriteItem (command, output, item, target, summary_only);
                    continue;
                }
                if (policy.DryRun)
                {
                    item.Status = item.Repair ? "planned_repair" : "planned";
                    ++planned;
                    WriteItem (command, output, item, target, summary_only);
                    continue;
                }

                try
                {
                    ImageData image;
                    using (var input = BinaryStream.FromFile (item.Source.FullPath))
                    {
                        input.Position = 0;
                        image = item.SourceFormat.Read (input, item.Metadata);
                    }
                    if (null == image)
                        throw new InvalidFormatException (
                            "The source image decoder returned no image data.");
                    long count = SafeFileWriter.WriteToFile (
                        item.Output.FullPath, policy.Overwrite, budget,
                        stream => target.Write (stream, image));
                    item.BytesWritten = count;
                    bytes_written = AddFinite (bytes_written, count);
                    item.Status = item.Repair ? "repaired" : "written";
                    ++written;
                    if (item.Repair)
                        ++repaired;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    item.Status = "failed";
                    item.Error = ErrorData (exception, "image_conversion_failed");
                    ++failed;
                }
                WriteItem (command, output, item, target, summary_only);
            }

            bool partial = failed > 0 || skipped > 0;
            string status = partial ? "partial_success" : "success";
            var limits = policy.ToDictionary();
            if ("auto".Equals (command.GetSingle ("budget"),
                               StringComparison.OrdinalIgnoreCase))
            {
                limits["budgetSource"] = "imageBatchPlan";
            }
            var summary = new Dictionary<string, object> {
                { "sourceRoot", source_root },
                { "destination", resolver.Root },
                { "targetTag", target.Tag },
                { "targetExtension", target_extension },
                { "manifest", manifest },
                { "recursive", recursive },
                { "detectBySignature", detect_by_signature },
                { "resume", resume },
                { "dryRun", policy.DryRun },
                { "summaryOnly", summary_only },
                { "scanned", scan.ScannedFileCount },
                { "reparsePointsSkipped", scan.ReparsePointsSkipped },
                { "signatureCandidatesIgnored", signature_ignored },
                { "selected", items.Count },
                { "recognized", recognized },
                { "planned", planned },
                { "written", written },
                { "repaired", repaired },
                { "verifiedExisting", verified },
                { "skipped", skipped },
                { "failed", failed },
                { "bytesWritten", bytes_written },
                { "observedBytes", budget.ObservedBytes },
                { "estimatedOutputBytes", estimated_total },
                { "limits", limits },
            };
            output.Complete (command.CommandName, status, summary);
            return partial ? ExitCode.PartialSuccess : ExitCode.Success;
        }

        static SourceScan EnumerateSources (
            string source_root, bool recursive, bool detect_by_signature,
            ISet<string> known_extensions, IList<string> includes)
        {
            var result = new SourceScan();
            var pending = new Stack<string>();
            pending.Push (source_root);
            while (pending.Count > 0)
            {
                CancellationState.ThrowIfRequested();
                string directory = pending.Pop();
                foreach (string path in Directory.GetFileSystemEntries (directory))
                {
                    CancellationState.ThrowIfRequested();
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes (path);
                    }
                    catch (FileNotFoundException)
                    {
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        continue;
                    }
                    if (0 != (attributes & FileAttributes.ReparsePoint))
                    {
                        ++result.ReparsePointsSkipped;
                        continue;
                    }
                    if (0 != (attributes & FileAttributes.Directory))
                    {
                        if (recursive)
                            pending.Push (path);
                        continue;
                    }

                    ++result.ScannedFileCount;
                    string relative = GetRelativePath (source_root, path);
                    if (!GlobMatcher.IsAnyMatch (PortablePath (relative), includes))
                        continue;
                    string extension = Path.GetExtension (path).TrimStart ('.');
                    if (!detect_by_signature && !known_extensions.Contains (extension))
                        continue;
                    result.Sources.Add (new SourceReference {
                        FullPath = Path.GetFullPath (path),
                        RelativePath = relative,
                    });
                }
                if (!recursive)
                    break;
            }
            SortSources (result.Sources);
            return result;
        }

        static SourceScan ReadManifest (
            string source_root, string manifest, IList<string> includes)
        {
            var result = new SourceScan();
            var paths = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
            var encoding = new UTF8Encoding (false, true);
            try
            {
                using (var file = new FileStream (
                    manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader (file, encoding, false))
                {
                    string line;
                    int line_number = 0;
                    while (null != (line = reader.ReadLine()))
                    {
                        CancellationState.ThrowIfRequested();
                        ++line_number;
                        if (1 == line_number && line.Length > 0
                            && '\uFEFF' == line[0])
                        {
                            line = line.Substring (1);
                        }
                        string value = ParseManifestLine (line, line_number);
                        if (null == value)
                            continue;
                        bool json_record = line.TrimStart().StartsWith (
                            "{", StringComparison.Ordinal);
                        string full_path = ResolveManifestPath (
                            source_root, value, json_record, line_number);
                        EnsureManifestSourceSafe (
                            source_root, full_path, value, line_number);
                        string relative = GetRelativePath (source_root, full_path);
                        if (!GlobMatcher.IsAnyMatch (PortablePath (relative), includes))
                            continue;
                        if (!paths.Add (full_path))
                        {
                            throw CliException.Invalid (
                                "duplicate_manifest_path",
                                "The image manifest contains the same source path more than once.",
                                new Dictionary<string, object> {
                                    { "line", line_number },
                                    { "path", value },
                                });
                        }
                        result.Sources.Add (new SourceReference {
                            FullPath = full_path,
                            RelativePath = relative,
                        });
                        ++result.ScannedFileCount;
                    }
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new CliException (
                    ExitCode.InvalidInput, "invalid_input",
                    "invalid_manifest_encoding",
                    "The image manifest is not valid UTF-8.",
                    new Dictionary<string, object> { { "path", manifest } },
                    exception);
            }
            SortSources (result.Sources);
            return result;
        }

        static string ParseManifestLine (string line, int line_number)
        {
            string trimmed = (line ?? string.Empty).Trim();
            if (0 == trimmed.Length)
                return null;
            if (!trimmed.StartsWith ("{", StringComparison.Ordinal))
                return trimmed;
            try
            {
                JObject record = JObject.Parse (trimmed);
                JProperty property = record.Properties().FirstOrDefault (
                    x => "path".Equals (x.Name, StringComparison.OrdinalIgnoreCase))
                    ?? record.Properties().FirstOrDefault (
                    x => "sourcePath".Equals (
                        x.Name, StringComparison.OrdinalIgnoreCase));
                if (null == property || JTokenType.String != property.Value.Type
                    || string.IsNullOrWhiteSpace ((string)property.Value))
                {
                    throw CliException.Invalid (
                        "invalid_image_manifest_record",
                        "A JSONL image manifest record requires a string path or sourcePath.",
                        new Dictionary<string, object> { { "line", line_number } });
                }
                return ((string)property.Value).Trim();
            }
            catch (CliException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new CliException (
                    ExitCode.InvalidInput, "invalid_input",
                    "invalid_image_manifest_record",
                    "The image manifest contains invalid JSONL.",
                    new Dictionary<string, object> { { "line", line_number } },
                    exception);
            }
        }

        static string ResolveManifestPath (
            string source_root, string value, bool json_record, int line_number)
        {
            try
            {
                if (!json_record && Path.IsPathRooted (value))
                {
                    throw CliException.Invalid (
                        "invalid_manifest_path",
                        "Plain-text image manifest paths must be relative to --source-root.",
                        new Dictionary<string, object> {
                            { "line", line_number },
                            { "path", value },
                            { "reason", "rooted_plain_text_path" },
                        });
                }
                string full_path = Path.IsPathRooted (value)
                    ? Path.GetFullPath (value)
                    : Path.GetFullPath (Path.Combine (source_root, value));
                if (!IsWithin (source_root, full_path))
                {
                    throw CliException.Invalid (
                        "manifest_path_outside_source_root",
                        "An image manifest path resolves outside --source-root.",
                        new Dictionary<string, object> {
                            { "line", line_number },
                            { "path", value },
                            { "sourceRoot", source_root },
                        });
                }
                return full_path;
            }
            catch (CliException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException
                    || exception is NotSupportedException
                    || exception is PathTooLongException)
                {
                    throw new CliException (
                        ExitCode.InvalidInput, "invalid_input",
                        "invalid_manifest_path",
                        "The image manifest contains an invalid path.",
                        new Dictionary<string, object> {
                            { "line", line_number },
                            { "path", value },
                        }, exception);
                }
                throw;
            }
        }

        static void EnsureManifestSourceSafe (
            string source_root, string full_path, string value, int line_number)
        {
            if (!File.Exists (full_path))
            {
                throw CliException.Invalid (
                    "manifest_source_not_found",
                    "An image manifest source file does not exist.",
                    new Dictionary<string, object> {
                        { "line", line_number },
                        { "path", value },
                    });
            }
            string relative = GetRelativePath (source_root, full_path);
            string current = source_root;
            foreach (string segment in relative.Split (
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine (current, segment);
                if (0 != (File.GetAttributes (current) & FileAttributes.ReparsePoint))
                {
                    throw CliException.Invalid (
                        "manifest_reparse_point",
                        "An image manifest path traverses a reparse point.",
                        new Dictionary<string, object> {
                            { "line", line_number },
                            { "path", value },
                            { "component", current },
                        });
                }
            }
        }

        static BatchItem InspectSource (
            RuntimeContext runtime, SourceReference source,
            bool signature_only)
        {
            var item = new BatchItem {
                Source = source,
                SourceBytes = new FileInfo (source.FullPath).Length,
            };
            try
            {
                runtime.BeginRecognition();
                bool registered_signature = false;
                using (var input = BinaryStream.FromFile (source.FullPath))
                {
                    if (input.Length < 4)
                    {
                        if (signature_only)
                            return null;
                    }
                    else
                    {
                        registered_signature = runtime.Catalog
                            .LookupSignature<ImageFormat> (input.Signature).Any();
                        Tuple<ImageFormat, ImageMetaData> detected =
                            ImageFormat.FindFormat (input);
                        if (null != detected)
                        {
                            item.SourceFormat = detected.Item1;
                            item.Metadata = detected.Item2;
                            return item;
                        }
                    }
                }
                if (signature_only)
                {
                    if (!registered_signature)
                        return null;
                    item.Error = new Dictionary<string, object> {
                        { "code", "format_not_recognized" },
                        { "message", "An image signature candidate could not be recognized." },
                    };
                    return item;
                }
                try
                {
                    runtime.ThrowRecognitionFailure (source.FullPath);
                }
                catch (CliException exception)
                {
                    item.Error = ErrorData (exception, "format_not_recognized");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                item.Error = ErrorData (exception, "image_recognition_failed");
            }
            return item;
        }

        static void PreflightDestinations (
            RuntimeContext runtime, IEnumerable<BatchItem> items,
            ImageFormat target, string resume, ExtractionPolicy policy)
        {
            foreach (BatchItem item in items)
            {
                CancellationState.ThrowIfRequested();
                if (Directory.Exists (item.Output.FullPath))
                {
                    throw CliException.Conflict (
                        "destination_is_directory",
                        "An image output path is an existing directory.",
                        new Dictionary<string, object> {
                            { "sourcePath", item.Source.FullPath },
                            { "destination", item.Output.FullPath },
                        });
                }
                if (!File.Exists (item.Output.FullPath))
                    continue;

                item.DestinationExisted = true;
                if (null != resume)
                {
                    VerificationResult verification = VerifyOutput (
                        runtime, item.Output.FullPath, target, resume);
                    item.ResumeVerification = verification.Reason;
                    if (verification.Valid)
                    {
                        item.Verified = true;
                        continue;
                    }
                    if (OverwriteMode.Replace == policy.Overwrite)
                    {
                        item.Repair = true;
                        continue;
                    }
                    throw CliException.Conflict (
                        "resume_verification_failed",
                        "An existing image output failed resume verification; use --overwrite replace to repair it.",
                        new Dictionary<string, object> {
                            { "sourcePath", item.Source.FullPath },
                            { "destination", item.Output.FullPath },
                            { "resume", resume },
                            { "reason", verification.Reason },
                            { "message", verification.Message },
                        });
                }

                switch (policy.Overwrite)
                {
                case OverwriteMode.Never:
                    throw CliException.Conflict (
                        "destination_exists",
                        "Destination file already exists: " + item.Output.FullPath,
                        new Dictionary<string, object> {
                            { "path", item.Output.FullPath },
                            { "sourcePath", item.Source.FullPath },
                        });
                case OverwriteMode.Skip:
                    item.Skip = true;
                    break;
                case OverwriteMode.Replace:
                    break;
                }
            }
        }

        static void EnsureOutputsAreNotInputs (
            string source_root, string manifest, IEnumerable<BatchItem> items)
        {
            foreach (BatchItem item in items)
            {
                string input_kind = null;
                string input_path = null;
                if (IsWithin (source_root, item.Output.FullPath))
                {
                    input_kind = "sourceRoot";
                    input_path = source_root;
                }
                else if (!string.IsNullOrEmpty (manifest)
                         && string.Equals (
                             manifest, item.Output.FullPath,
                             StringComparison.OrdinalIgnoreCase))
                {
                    input_kind = "sourceManifest";
                    input_path = manifest;
                }

                if (null == input_kind)
                    continue;
                throw CliException.Conflict (
                    "output_input_collision",
                    "A planned image output conflicts with a batch input.",
                    new Dictionary<string, object> {
                        { "inputKind", input_kind },
                        { "inputPath", input_path },
                        { "sourcePath", item.Source.FullPath },
                        { "outputPath", item.Output.FullPath },
                    });
            }
        }

        static VerificationResult VerifyOutput (
            RuntimeContext runtime, string path, ImageFormat target, string mode)
        {
            try
            {
                runtime.BeginRecognition();
                using (var input = BinaryStream.FromFile (path))
                {
                    Tuple<ImageFormat, ImageMetaData> detected =
                        ImageFormat.FindFormat (input);
                    if (null == detected)
                        return VerificationResult.Failure ("header_not_recognized",
                            "The existing output header was not recognized as an image.");
                    if (!IsRequestedTargetFormat (
                            detected.Item1, target, input))
                        return VerificationResult.Failure ("target_format_mismatch",
                            "The existing output is not encoded in the requested target format.");
                    if ("verify-decode" == mode)
                    {
                        input.Position = 0;
                        ImageData image = detected.Item1.Read (input, detected.Item2);
                        if (null == image || 0 == image.Width || 0 == image.Height)
                            return VerificationResult.Failure ("decode_returned_no_image",
                                "The existing output did not decode to a non-empty image.");
                    }
                }
                return VerificationResult.Success();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return VerificationResult.Failure (
                    "verification_exception", exception.Message);
            }
        }

        static bool IsRequestedTargetFormat (
            ImageFormat detected, ImageFormat target, IBinaryStream input)
        {
            if (detected.GetType() == target.GetType())
                return true;

            // WebP readers and writers are deliberately separate implementations:
            // WEBP recognizes both bitstream variants, while WEBP/80 and
            // WEBP/LOSSLESS are write-only encoder presets.
            if (!string.Equals (detected.Tag, "WEBP",
                                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string variant = DetectWebPVariant (input);
            if (string.Equals (target.Tag, "WEBP/80",
                               StringComparison.OrdinalIgnoreCase))
            {
                return "lossy" == variant;
            }
            if (string.Equals (target.Tag, "WEBP/LOSSLESS",
                               StringComparison.OrdinalIgnoreCase))
            {
                return "lossless" == variant;
            }
            return string.Equals (target.Tag, "WEBP",
                                  StringComparison.OrdinalIgnoreCase);
        }

        static string DetectWebPVariant (IBinaryStream input)
        {
            if (null == input || !input.CanSeek || input.Length < 20)
                return null;
            long previous = input.Position;
            try
            {
                input.Position = 0;
                byte[] header = input.ReadBytes (12);
                if (!AsciiEquals (header, 0, "RIFF")
                    || !AsciiEquals (header, 8, "WEBP"))
                {
                    return null;
                }

                long offset = 12;
                while (offset <= input.Length - 8)
                {
                    input.Position = offset;
                    byte[] chunk = input.ReadBytes (4);
                    uint size = input.ReadUInt32();
                    if (AsciiEquals (chunk, 0, "VP8L"))
                        return "lossless";
                    if (AsciiEquals (chunk, 0, "VP8 "))
                        return "lossy";

                    long padded_size = (long)size + (size & 1U);
                    if (padded_size > input.Length - offset - 8)
                        return null;
                    offset += 8 + padded_size;
                }
                return null;
            }
            finally
            {
                input.Position = previous;
            }
        }

        static bool AsciiEquals (byte[] value, int offset, string expected)
        {
            if (null == value || value.Length < offset + expected.Length)
                return false;
            for (int i = 0; i < expected.Length; ++i)
            {
                if (value[offset+i] != (byte)expected[i])
                    return false;
            }
            return true;
        }

        static void EnsureFileCount (int count, long limit)
        {
            if (count > limit)
            {
                throw CliException.Invalid (
                    "file_count_limit_exceeded",
                    "Selected image count exceeds --max-files.",
                    new Dictionary<string, object> {
                        { "observed", count },
                        { "limit", limit },
                    });
            }
        }

        static void EnsurePlannedBudget (
            IEnumerable<BatchItem> items, ExtractionPolicy policy)
        {
            long total = 0;
            foreach (BatchItem item in items.Where (
                x => null == x.Error && !x.Verified && !x.Skip))
            {
                if (item.EstimatedOutputBytes > policy.MaxEntryBytes)
                {
                    throw CliException.Invalid (
                        "entry_size_limit_exceeded",
                        "An estimated image output exceeds --max-entry-bytes.",
                        new Dictionary<string, object> {
                            { "sourcePath", item.Source.FullPath },
                            { "estimatedOutputBytes", item.EstimatedOutputBytes },
                            { "limit", policy.MaxEntryBytes },
                        });
                }
                if (item.EstimatedOutputBytes > policy.MaxTotalBytes - total)
                {
                    throw CliException.Invalid (
                        "total_size_limit_exceeded",
                        "Estimated image outputs exceed --max-total-bytes.",
                        new Dictionary<string, object> {
                            { "observed", AddFinite (total, item.EstimatedOutputBytes) },
                            { "limit", policy.MaxTotalBytes },
                        });
                }
                total += item.EstimatedOutputBytes;
            }
        }

        static ExtractionLimits CreateAutomaticLimits (
            int count, long total, long maximum_entry, int maximum_depth)
        {
            return new ExtractionLimits {
                MaxFiles = Math.Max (1, count),
                MaxTotalBytes = AddFinite (
                    total, Math.Max (MinimumPlanHeadroom, CeilingTwoPercent (total))),
                MaxEntryBytes = AddFinite (
                    maximum_entry, Math.Max (
                        MinimumPlanHeadroom, CeilingTwoPercent (maximum_entry))),
                MaxDepth = Math.Max (1, maximum_depth),
            };
        }

        static long EstimateOutputBytes (BatchItem item)
        {
            long pixels = SaturatingMultiply (
                item.Metadata.Width, item.Metadata.Height);
            long bytes_per_pixel = Math.Max (
                4, (item.Metadata.BPP + 7L) / 8L);
            long decoded = SaturatingMultiply (pixels, bytes_per_pixel);
            return AddFinite (
                Math.Max (item.SourceBytes, decoded), PerImageEstimateHeadroom);
        }

        static long SaturatingMultiply (long left, long right)
        {
            if (left <= 0 || right <= 0)
                return 0;
            return left > long.MaxValue / right
                ? long.MaxValue : left * right;
        }

        static long AddFinite (long value, long addition)
        {
            if (value < 0 || addition < 0)
                return long.MaxValue;
            return value > long.MaxValue - addition
                ? long.MaxValue : value + addition;
        }

        static long CeilingTwoPercent (long value)
        {
            if (value <= 0)
                return 0;
            return value / 50 + (0 == value % 50 ? 0 : 1);
        }

        static void WriteItem (
            ParsedCommand command, MachineOutput output, BatchItem item,
            ImageFormat target, bool summary_only)
        {
            var data = new Dictionary<string, object> {
                { "relativePath", PortablePath (item.Source.RelativePath) },
                { "sourcePath", item.Source.FullPath },
                { "destination", item.Output.FullPath },
                { "sourceTag", null != item.SourceFormat ? item.SourceFormat.Tag : null },
                { "targetTag", target.Tag },
                { "status", item.Status },
                { "bytesWritten", item.BytesWritten },
                { "estimatedOutputBytes", item.EstimatedOutputBytes },
            };
            if (null != item.Metadata)
            {
                data["width"] = item.Metadata.Width;
                data["height"] = item.Metadata.Height;
                data["bpp"] = item.Metadata.BPP;
            }
            if (item.DestinationExisted)
                data["destinationExisted"] = true;
            if (!string.IsNullOrEmpty (item.ResumeVerification))
                data["resumeVerification"] = item.ResumeVerification;
            if (null != item.Error)
                data["error"] = item.Error;

            if (output.IsJsonLines && !summary_only)
            {
                output.WriteEvent (
                    command.CommandName, "image", EventStatus (item.Status), data);
            }
            else if (output.IsText && !summary_only)
            {
                output.WriteText (string.Format (
                    CultureInfo.InvariantCulture, "{0} {1} => {2}",
                    item.Status.ToUpperInvariant(),
                    PortablePath (item.Source.RelativePath), item.Output.FullPath));
            }
        }

        static string EventStatus (string item_status)
        {
            switch (item_status)
            {
            case "failed": return "failed";
            case "skipped": return "skipped";
            case "planned":
            case "planned_repair": return "planned";
            default: return "success";
            }
        }

        static Dictionary<string, object> ErrorData (
            Exception exception, string fallback_code)
        {
            var cli = exception as CliException;
            if (null != cli)
            {
                var result = new Dictionary<string, object> {
                    { "code", cli.Code },
                    { "status", cli.Status },
                    { "message", cli.Message },
                };
                if (null != cli.Details)
                    result["details"] = cli.Details;
                return result;
            }
            string code = exception is UnauthorizedAccessException
                ? "access_denied"
                : exception is IOException ? "io_failure" : fallback_code;
            return new Dictionary<string, object> {
                { "code", code },
                { "message", exception.Message },
                { "exceptionType", exception.GetType().FullName },
            };
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

        static string ParseResumeMode (string value)
        {
            if (string.IsNullOrWhiteSpace (value))
                return null;
            string normalized = value.ToLowerInvariant();
            if ("verify-header" != normalized && "verify-decode" != normalized)
            {
                throw CliException.Usage (
                    "invalid_resume_mode",
                    "--resume must be one of: verify-header, verify-decode.");
            }
            return normalized;
        }

        static void ValidateIncludes (IEnumerable<string> includes)
        {
            if (includes.Any (string.IsNullOrWhiteSpace))
                throw CliException.Usage (
                    "invalid_include_pattern",
                    "--include requires a non-empty glob pattern.");
        }

        static void SortSources (List<SourceReference> sources)
        {
            sources.Sort ((left, right) => {
                int comparison = StringComparer.OrdinalIgnoreCase.Compare (
                    PortablePath (left.RelativePath),
                    PortablePath (right.RelativePath));
                if (0 != comparison)
                    return comparison;
                return StringComparer.Ordinal.Compare (
                    PortablePath (left.RelativePath),
                    PortablePath (right.RelativePath));
            });
        }

        static string NormalizeDirectory (string path)
        {
            string full_path = Path.GetFullPath (path);
            string volume_root = Path.GetPathRoot (full_path);
            return string.Equals (full_path, volume_root,
                                  StringComparison.OrdinalIgnoreCase)
                ? full_path
                : full_path.TrimEnd (
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static string NormalizeDirectoryPath (string path)
        {
            try
            {
                return NormalizeDirectory (path);
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException
                    || exception is NotSupportedException
                    || exception is PathTooLongException)
                {
                    throw new CliException (
                        ExitCode.InvalidInput, "invalid_input",
                        "invalid_destination",
                        "Batch destination path is invalid: " + path,
                        null, exception);
                }
                throw;
            }
        }

        static void RejectDestinationInsideSource (
            string source_root, string destination)
        {
            if (IsWithin (source_root, destination))
            {
                throw CliException.Invalid (
                    "destination_inside_source_root",
                    "Batch destination must not be equal to or below --source-root.",
                    new Dictionary<string, object> {
                        { "sourceRoot", source_root },
                        { "destination", destination },
                    });
            }
        }

        static void RejectSourceRootReparsePoints (string source_root)
        {
            string current = source_root;
            while (!string.IsNullOrEmpty (current))
            {
                if (Directory.Exists (current)
                    && 0 != (File.GetAttributes (current)
                             & FileAttributes.ReparsePoint))
                {
                    throw CliException.Invalid (
                        "source_root_reparse_point",
                        "--source-root must not traverse a reparse point.",
                        new Dictionary<string, object> {
                            { "sourceRoot", source_root },
                            { "component", current },
                        });
                }
                string parent = Path.GetDirectoryName (current);
                if (string.Equals (parent, current,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                current = parent;
            }
        }

        static bool IsWithin (string root, string path)
        {
            if (string.Equals (root, path, StringComparison.OrdinalIgnoreCase))
                return true;
            string prefix = root.EndsWith (
                Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root : root + Path.DirectorySeparatorChar;
            return path.StartsWith (prefix, StringComparison.OrdinalIgnoreCase);
        }

        static string GetRelativePath (string root, string path)
        {
            string full_path = Path.GetFullPath (path);
            if (!IsWithin (root, full_path)
                || string.Equals (root, full_path,
                                  StringComparison.OrdinalIgnoreCase))
            {
                throw CliException.Invalid (
                    "source_path_outside_root",
                    "Image source path is not a file below --source-root.",
                    new Dictionary<string, object> {
                        { "sourceRoot", root },
                        { "path", full_path },
                    });
            }
            string prefix = root.EndsWith (
                Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root : root + Path.DirectorySeparatorChar;
            return full_path.Substring (prefix.Length);
        }

        static string PortablePath (string path)
        {
            return (path ?? string.Empty).Replace ('\\', '/');
        }

        sealed class SourceScan
        {
            public readonly List<SourceReference> Sources =
                new List<SourceReference>();
            public int ScannedFileCount;
            public int ReparsePointsSkipped;
        }

        sealed class SourceReference
        {
            public string FullPath;
            public string RelativePath;
        }

        sealed class BatchItem
        {
            public SourceReference Source;
            public ImageFormat SourceFormat;
            public ImageMetaData Metadata;
            public ResolvedOutputPath Output;
            public long SourceBytes;
            public long EstimatedOutputBytes;
            public long BytesWritten;
            public bool DestinationExisted;
            public bool Verified;
            public bool Skip;
            public bool Repair;
            public string ResumeVerification;
            public string Status;
            public Dictionary<string, object> Error;
        }

        sealed class VerificationResult
        {
            public bool Valid;
            public string Reason;
            public string Message;

            public static VerificationResult Success ()
            {
                return new VerificationResult {
                    Valid = true,
                    Reason = "verified",
                    Message = "Existing output passed resume verification.",
                };
            }

            public static VerificationResult Failure (
                string reason, string message)
            {
                return new VerificationResult {
                    Valid = false,
                    Reason = reason,
                    Message = message,
                };
            }
        }
    }
}
