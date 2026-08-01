using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameRes;
using GameRes.Formats.KiriKiri;

namespace GARbro.Cli
{
    internal static class ArchiveSchemeCommands
    {
        const int SchemeCheckEntryLimit = 32;

        public static ExitCode Schemes (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("tag", "filter");
            command.RequirePositionalCount (0);
            var tag = command.GetSingle ("tag", "XP3");
            if (!string.Equals (tag, "XP3", StringComparison.OrdinalIgnoreCase))
            {
                throw CliException.Invalid (
                    "scheme_tag_not_supported",
                    "archive schemes currently supports only the XP3 format tag.",
                    new Dictionary<string, object> {
                        { "requestedTag", tag },
                        { "supportedTags", new[] { "XP3" } },
                    });
            }
            var filter = command.GetSingle ("filter");
            var descriptors = ArchiveSchemeOptions.EnumerateDescriptors().ToList();
            var schemes = descriptors
                .Where (x => MatchesFilter (x, filter))
                .Select (x => x.ToDictionary())
                .ToList();
            var known_names = new HashSet<string> (
                descriptors.Where (x => null != x.Scheme).Select (x => x.Name),
                StringComparer.OrdinalIgnoreCase);
            var game_map = runtime.Catalog.EnumerateGameMap()
                .Where (x => string.IsNullOrEmpty (filter)
                    || Contains (x.Key, filter) || Contains (x.Value, filter))
                .OrderBy (x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy (x => x.Value, StringComparer.OrdinalIgnoreCase)
                .Select (x => new Dictionary<string, object> {
                    { "lookupName", x.Key },
                    { "title", x.Value },
                    { "schemeKnown", known_names.Contains (x.Value) },
                })
                .ToList();

            foreach (var scheme in schemes)
            {
                if (output.IsJsonLines)
                    output.WriteEvent (command.CommandName, "scheme", "success", scheme);
                else if (output.IsText)
                    output.WriteText (FormatSchemeText (scheme));
            }
            foreach (var mapping in game_map)
            {
                if (output.IsJsonLines)
                    output.WriteEvent (command.CommandName, "game-map", "success", mapping);
                else if (output.IsText)
                {
                    output.WriteText (string.Format (
                        System.Globalization.CultureInfo.InvariantCulture,
                        "GAME-MAP {0} => {1}", mapping["lookupName"], mapping["title"]));
                }
            }
            var result = new Dictionary<string, object> {
                { "tag", "XP3" },
                { "count", schemes.Count },
                { "schemeCount", schemes.Count },
                { "gameMapCount", game_map.Count },
            };
            if (!string.IsNullOrEmpty (filter))
                result["filter"] = filter;
            if (output.IsJson)
            {
                result["schemes"] = schemes;
                result["gameMap"] = game_map;
            }
            output.Complete (command.CommandName, "success", result);
            return ExitCode.Success;
        }

        public static ExitCode SchemeInfo (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions();
            command.RequirePositionalCount (1);
            var requested_name = command.RequirePositional (0, "scheme name");
            var scheme = ArchiveSchemeOptions.ResolveDescriptor (requested_name);
            var data = scheme.ToDictionary();
            data["requestedName"] = requested_name;
            var game_map = runtime.Catalog.EnumerateGameMap()
                .Where (x => string.Equals (
                    x.Value, scheme.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy (x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select (x => new Dictionary<string, object> {
                    { "lookupName", x.Key },
                    { "title", x.Value },
                })
                .ToList();
            data["gameMapCount"] = game_map.Count;
            if (game_map.Count > 0)
                data["gameMap"] = game_map;
            output.Complete (command.CommandName, "success", data);
            return ExitCode.Success;
        }

        public static ExitCode SchemeCheck (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            command.RejectUnknownOptions ("scheme", "hx-names", "cx-dump-dir");
            command.RequirePositionalCount (1);
            if (!ArchiveSchemeOptions.HasOptions (command))
            {
                throw CliException.Usage (
                    "xp3_scheme_required",
                    "archive scheme-check requires --scheme or --cx-dump-dir.");
            }

            var archive_path = command.RequirePositional (0, "archive path");
            var resolution = ArchiveSchemeOptions.Resolve (runtime, command, archive_path);
            using (var archive = runtime.OpenArchive (archive_path, resolution))
            {
                var validation = ValidateContent (archive, resolution);
                var data = new Dictionary<string, object> {
                    { "archivePath", Path.GetFullPath (archive_path) },
                    { "archiveTag", archive.Tag },
                    { "indexOpened", true },
                    { "entryCount", archive.Dir.Count },
                    { "schemeResolution", resolution.ToDictionary() },
                    { "contentValidation", validation.ToDictionary() },
                };
                if (validation.IsMismatch)
                {
                    throw CliException.Invalid (
                        "xp3_scheme_check_failed",
                        "The selected XP3 scheme opened the index, but sampled entry contents did not match their expected formats.",
                        data);
                }
                output.Complete (command.CommandName, "success", data);
            }
            return ExitCode.Success;
        }

        static bool MatchesFilter (ArchiveSchemeDescriptor scheme, string filter)
        {
            if (string.IsNullOrEmpty (filter))
                return true;
            return Contains (scheme.Name, filter)
                || Contains (scheme.DisplayName, filter)
                || Contains (scheme.AlgorithmType, filter)
                || Contains (scheme.Family, filter)
                || Contains (scheme.Source, filter);
        }

        static bool Contains (string value, string filter)
        {
            return !string.IsNullOrEmpty (value)
                && value.IndexOf (filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string FormatSchemeText (IDictionary<string, object> scheme)
        {
            return string.Format (
                System.Globalization.CultureInfo.InvariantCulture,
                "{0,-36} {1,-16} {2}",
                scheme["name"], scheme["family"], scheme["displayName"]);
        }

        static SchemeContentValidation ValidateContent (
            ArcFile archive, ArchiveSchemeResolution resolution)
        {
            var result = new SchemeContentValidation();
            bool requires_hx_index = null != resolution
                && null != resolution.EffectiveScheme
                && "hx-v4" == resolution.EffectiveScheme.Family;
            foreach (var entry in archive.Dir.Take (SchemeCheckEntryLimit))
            {
                ++result.SampledEntries;
                var expected = ExpectedContentKind (entry.Name);
                if (null != expected)
                    ++result.ExpectedEntriesSampled;
                var xp3_entry = entry as Xp3Entry;
                if (requires_hx_index && null != xp3_entry
                    && xp3_entry.IsEncrypted && null == xp3_entry.Extra)
                {
                    result.Failures.Add (new Dictionary<string, object> {
                        { "entry", entry.Name },
                        { "expected", "resolved_hx_v4_index_entry" },
                        { "reason", "hx_index_unresolved" },
                    });
                    continue;
                }
                byte[] header;
                try
                {
                    using (var input = archive.OpenEntry (entry))
                        header = ReadPrefix (input, 16);
                }
                catch (Exception exception)
                {
                    if (null != expected)
                    {
                        result.Failures.Add (new Dictionary<string, object> {
                            { "entry", entry.Name },
                            { "expected", expected },
                            { "reason", "read_failed" },
                            { "exceptionType", exception.GetType().FullName },
                        });
                    }
                    continue;
                }

                var detected = DetectContentKind (header);
                if (null != expected)
                {
                    if (ContentKindsMatch (expected, detected))
                    {
                        ++result.MatchedEntries;
                        result.Matches.Add (new Dictionary<string, object> {
                            { "entry", entry.Name },
                            { "expected", expected },
                            { "detected", detected },
                        });
                    }
                    else
                    {
                        result.Failures.Add (new Dictionary<string, object> {
                            { "entry", entry.Name },
                            { "expected", expected },
                            { "detected", detected ?? "unknown" },
                            { "reason", "magic_mismatch" },
                        });
                    }
                }
                else if (null != detected)
                {
                    ++result.MatchedEntries;
                    result.Matches.Add (new Dictionary<string, object> {
                        { "entry", entry.Name },
                        { "detected", detected },
                    });
                }
            }

            if (result.Failures.Count > 0 && result.MatchedEntries > 0)
                result.Status = "mixed";
            else if (result.Failures.Count > 0)
                result.Status = "mismatch";
            else if (result.MatchedEntries > 0)
                result.Status = "matched";
            else if (result.ExpectedEntriesSampled > 0)
                result.Status = "mismatch";
            else
                result.Status = "inconclusive";
            return result;
        }

        static byte[] ReadPrefix (Stream input, int maximum)
        {
            var buffer = new byte[maximum];
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = input.Read (buffer, offset, buffer.Length - offset);
                if (0 == read)
                    break;
                offset += read;
            }
            if (offset != buffer.Length)
                Array.Resize (ref buffer, offset);
            return buffer;
        }

        static string ExpectedContentKind (string name)
        {
            switch (Path.GetExtension (name).ToLowerInvariant())
            {
            case ".png": return "png";
            case ".jpg":
            case ".jpeg": return "jpeg";
            case ".bmp": return "bmp";
            case ".gif": return "gif";
            case ".ogg": return "ogg";
            case ".wav": return "wav";
            case ".webp": return "webp";
            case ".psb": return "psb";
            case ".tlg": return "tlg";
            default: return null;
            }
        }

        static string DetectContentKind (byte[] value)
        {
            if (StartsWith (value, new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
                return "png";
            if (StartsWith (value, new byte[] { 0xFF, 0xD8, 0xFF }))
                return "jpeg";
            if (StartsWithAscii (value, "BM"))
                return "bmp";
            if (StartsWithAscii (value, "GIF8"))
                return "gif";
            if (StartsWithAscii (value, "OggS"))
                return "ogg";
            if (StartsWithAscii (value, "PSB\0"))
                return "psb";
            if (StartsWithAscii (value, "TLG"))
                return "tlg";
            if (StartsWithAscii (value, "RIFF") && AsciiEqual (value, 8, "WAVE"))
                return "wav";
            if (StartsWithAscii (value, "RIFF") && AsciiEqual (value, 8, "WEBP"))
                return "webp";
            if (StartsWith (value, new byte[] { 0x30, 0x26, 0xB2, 0x75 }))
                return "asf";
            return null;
        }

        static bool ContentKindsMatch (string expected, string detected)
        {
            return string.Equals (expected, detected, StringComparison.Ordinal);
        }

        static bool StartsWithAscii (byte[] value, string prefix)
        {
            return AsciiEqual (value, 0, prefix);
        }

        static bool AsciiEqual (byte[] value, int offset, string text)
        {
            if (null == value || value.Length < offset + text.Length)
                return false;
            for (int i = 0; i < text.Length; ++i)
            {
                if (value[offset+i] != (byte)text[i])
                    return false;
            }
            return true;
        }

        static bool StartsWith (byte[] value, byte[] prefix)
        {
            if (null == value || value.Length < prefix.Length)
                return false;
            for (int i = 0; i < prefix.Length; ++i)
            {
                if (value[i] != prefix[i])
                    return false;
            }
            return true;
        }

        sealed class SchemeContentValidation
        {
            public string Status;
            public int SampledEntries;
            public int ExpectedEntriesSampled;
            public int MatchedEntries;
            public readonly List<Dictionary<string, object>> Matches =
                new List<Dictionary<string, object>>();
            public readonly List<Dictionary<string, object>> Failures =
                new List<Dictionary<string, object>>();

            public bool IsMismatch
            {
                get { return "mismatch" == Status || "mixed" == Status; }
            }

            public Dictionary<string, object> ToDictionary ()
            {
                var result = new Dictionary<string, object> {
                    { "status", Status },
                    { "reasonCode", ReasonCode() },
                    { "sampleLimit", SchemeCheckEntryLimit },
                    { "sampledEntries", SampledEntries },
                    { "expectedEntriesSampled", ExpectedEntriesSampled },
                    { "matchedEntries", MatchedEntries },
                    { "mismatchCount", Failures.Count },
                };
                if (Matches.Count > 0)
                    result["matches"] = Matches.Take (8).ToList();
                if (Failures.Count > 0)
                    result["failures"] = Failures.Take (8).ToList();
                return result;
            }

            string ReasonCode ()
            {
                switch (Status)
                {
                case "matched": return "sample_magic_matched";
                case "mixed": return "sample_magic_mixed";
                case "mismatch": return "sample_magic_mismatch";
                default: return "no_recognizable_sample";
                }
            }
        }
    }
}
