using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GARbro.Cli
{
    internal enum OverwriteMode
    {
        Never,
        Skip,
        Replace,
    }

    internal sealed class ExtractionPolicy
    {
        public const long DefaultMaxFiles = 10000;
        public const long DefaultMaxTotalBytes = 4L * 1024 * 1024 * 1024;
        public const long DefaultMaxEntryBytes = 1024L * 1024 * 1024;
        public const int DefaultMaxDepth = 32;

        public long MaxFiles { get; private set; }
        public long MaxTotalBytes { get; private set; }
        public long MaxEntryBytes { get; private set; }
        public int MaxDepth { get; private set; }
        public OverwriteMode Overwrite { get; private set; }
        public bool DryRun { get; private set; }

        public static ExtractionPolicy FromCommand (ParsedCommand command)
        {
            string overwrite = command.GetSingle ("overwrite", "never").ToLowerInvariant();
            OverwriteMode mode;
            switch (overwrite)
            {
            case "never": mode = OverwriteMode.Never; break;
            case "skip": mode = OverwriteMode.Skip; break;
            case "replace": mode = OverwriteMode.Replace; break;
            default:
                throw CliException.Usage ("invalid_overwrite_mode",
                    "--overwrite must be one of: never, skip, replace.");
            }
            return new ExtractionPolicy {
                MaxFiles = command.GetInt64 (
                    "max-files", DefaultMaxFiles, 1, int.MaxValue),
                MaxTotalBytes = command.GetInt64 (
                    "max-total-bytes", DefaultMaxTotalBytes, 1, long.MaxValue),
                MaxEntryBytes = command.GetInt64 (
                    "max-entry-bytes", DefaultMaxEntryBytes, 1, long.MaxValue),
                MaxDepth = (int)command.GetInt64 (
                    "max-depth", DefaultMaxDepth, 1, 1024),
                Overwrite = mode,
                DryRun = command.HasFlag ("dry-run"),
            };
        }

        public Dictionary<string, object> ToDictionary ()
        {
            return new Dictionary<string, object> {
                { "maxFiles", MaxFiles },
                { "maxTotalBytes", MaxTotalBytes },
                { "maxEntryBytes", MaxEntryBytes },
                { "maxDepth", MaxDepth },
                { "overwrite", Overwrite.ToString().ToLowerInvariant() },
                { "dryRun", DryRun },
            };
        }
    }

    internal sealed class OutputPathResolver
    {
        static readonly HashSet<string> s_reserved_names =
            new HashSet<string> (StringComparer.OrdinalIgnoreCase) {
                "CON", "PRN", "AUX", "NUL",
                "CLOCK$",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            };

        readonly string m_root;
        readonly string m_root_prefix;
        readonly int m_max_depth;
        readonly HashSet<string> m_destinations =
            new HashSet<string> (StringComparer.OrdinalIgnoreCase);

        public string Root { get { return m_root; } }

        public OutputPathResolver (string root, int max_depth)
        {
            if (string.IsNullOrWhiteSpace (root))
                throw CliException.Usage ("missing_destination",
                    "A non-empty --destination directory is required.");
            try
            {
                m_root = Path.GetFullPath (root);
                string volume_root = Path.GetPathRoot (m_root);
                if (!string.Equals (m_root, volume_root,
                                    StringComparison.OrdinalIgnoreCase))
                {
                    m_root = m_root.TrimEnd (
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException || exception is NotSupportedException
                    || exception is PathTooLongException)
                {
                    throw CliException.Invalid ("invalid_destination",
                        "Destination path is invalid: " + root);
                }
                throw;
            }
            string path_root = Path.GetPathRoot (m_root);
            if (string.Equals (m_root, path_root,
                               StringComparison.OrdinalIgnoreCase))
            {
                m_root_prefix = m_root;
            }
            else
            {
                m_root_prefix = m_root + Path.DirectorySeparatorChar;
            }
            m_max_depth = max_depth;
        }

        public string Resolve (string entry_name)
        {
            if (string.IsNullOrWhiteSpace (entry_name))
                throw UnsafePath (entry_name, "empty_name");
            if (Path.IsPathRooted (entry_name) || entry_name.IndexOf (':') >= 0)
                throw UnsafePath (entry_name, "rooted_path");

            string normalized = entry_name.Replace ('/', Path.DirectorySeparatorChar)
                                          .Replace ('\\', Path.DirectorySeparatorChar);
            string[] segments = normalized.Split (
                new[] { Path.DirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (0 == segments.Length)
                throw UnsafePath (entry_name, "empty_name");
            if (segments.Length > m_max_depth)
                throw UnsafePath (entry_name, "depth_limit_exceeded");
            foreach (string segment in segments)
                ValidateSegment (entry_name, segment);

            string relative = Path.Combine (segments);
            string destination = Path.GetFullPath (Path.Combine (m_root, relative));
            if (!destination.StartsWith (m_root_prefix,
                                         StringComparison.OrdinalIgnoreCase))
            {
                throw UnsafePath (entry_name, "path_escape");
            }
            ValidateExistingPathComponents (entry_name, segments);
            if (!m_destinations.Add (destination))
                throw UnsafePath (entry_name, "duplicate_destination");
            return destination;
        }

        void ValidateExistingPathComponents (
            string entry_name, IEnumerable<string> segments)
        {
            string current = m_root;
            RejectReparsePoint (entry_name, current);
            foreach (string segment in segments)
            {
                current = Path.Combine (current, segment);
                RejectReparsePoint (entry_name, current);
            }
        }

        static void RejectReparsePoint (string entry_name, string path)
        {
            if (!File.Exists (path) && !Directory.Exists (path))
                return;
            FileAttributes attributes = File.GetAttributes (path);
            if (0 != (attributes & FileAttributes.ReparsePoint))
                throw UnsafePath (entry_name, "reparse_point");
        }

        static void ValidateSegment (string entry_name, string segment)
        {
            if ("." == segment || ".." == segment)
                throw UnsafePath (entry_name, "relative_segment");
            if (segment.EndsWith (" ", StringComparison.Ordinal)
                || segment.EndsWith (".", StringComparison.Ordinal))
            {
                throw UnsafePath (entry_name, "ambiguous_windows_name");
            }
            if (segment.IndexOfAny (Path.GetInvalidFileNameChars()) >= 0)
                throw UnsafePath (entry_name, "invalid_character");
            string stem = segment;
            int dot = stem.IndexOf ('.');
            if (dot >= 0)
                stem = stem.Substring (0, dot);
            if (s_reserved_names.Contains (stem))
                throw UnsafePath (entry_name, "reserved_windows_name");
        }

        static CliException UnsafePath (string entry_name, string reason)
        {
            return CliException.Invalid (
                "unsafe_output_path",
                "Archive entry cannot be mapped safely below the destination directory.",
                new Dictionary<string, object> {
                    { "entry", entry_name ?? string.Empty },
                    { "reason", reason },
                });
        }
    }

    internal sealed class ExtractionBudget
    {
        readonly ExtractionPolicy m_policy;

        public long TotalBytes { get; private set; }

        public ExtractionBudget (ExtractionPolicy policy)
        {
            m_policy = policy;
        }

        public void CheckDeclaredEntry (long size)
        {
            if (size > m_policy.MaxEntryBytes)
                throw LimitExceeded ("entry_size_limit_exceeded", size,
                                     m_policy.MaxEntryBytes);
            if (size > m_policy.MaxTotalBytes - TotalBytes)
                throw LimitExceeded ("total_size_limit_exceeded",
                                     TotalBytes + size, m_policy.MaxTotalBytes);
        }

        public void AddActual (long entry_bytes, int count)
        {
            if (entry_bytes > m_policy.MaxEntryBytes - count)
                throw LimitExceeded ("entry_size_limit_exceeded",
                                     entry_bytes + count, m_policy.MaxEntryBytes);
            if (TotalBytes > m_policy.MaxTotalBytes - count)
                throw LimitExceeded ("total_size_limit_exceeded",
                                     TotalBytes + count, m_policy.MaxTotalBytes);
            TotalBytes += count;
        }

        static CliException LimitExceeded (string code, long observed, long limit)
        {
            return CliException.Invalid (
                code, "Extraction safety limit exceeded.",
                new Dictionary<string, object> {
                    { "observed", observed },
                    { "limit", limit },
                });
        }
    }

    internal static class SafeFileWriter
    {
        public static long CopyToFile (Stream input, string destination,
                                       OverwriteMode overwrite,
                                       ExtractionBudget budget)
        {
            string directory = Path.GetDirectoryName (destination);
            Directory.CreateDirectory (directory);
            string temporary = destination + ".garbro-"
                + Guid.NewGuid().ToString ("N") + ".partial";
            long entry_bytes = 0;
            try
            {
                using (var output = new FileStream (
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[0x10000];
                    for (;;)
                    {
                        CancellationState.ThrowIfRequested();
                        int read = input.Read (buffer, 0, buffer.Length);
                        if (0 == read)
                            break;
                        budget.AddActual (entry_bytes, read);
                        output.Write (buffer, 0, read);
                        entry_bytes += read;
                    }
                    output.Flush();
                }

                if (File.Exists (destination))
                {
                    if (OverwriteMode.Replace != overwrite)
                        throw CliException.Conflict (
                            "destination_exists",
                            "Destination file already exists: " + destination,
                            new Dictionary<string, object> {
                                { "path", destination },
                            });
                    File.Replace (temporary, destination, null, true);
                }
                else
                {
                    File.Move (temporary, destination);
                }
                return entry_bytes;
            }
            finally
            {
                if (File.Exists (temporary))
                {
                    try { File.Delete (temporary); }
                    catch { }
                }
            }
        }

        public static long WriteToFile (string destination,
                                        OverwriteMode overwrite,
                                        ExtractionBudget budget,
                                        Action<Stream> writer)
        {
            string directory = Path.GetDirectoryName (destination);
            Directory.CreateDirectory (directory);
            string temporary = destination + ".garbro-"
                + Guid.NewGuid().ToString ("N") + ".partial";
            try
            {
                long written;
                using (var file = new FileStream (
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var output = new BudgetedWriteStream (file, budget))
                {
                    writer (output);
                    output.Flush();
                    written = output.BytesWritten;
                }
                Commit (temporary, destination, overwrite);
                return written;
            }
            finally
            {
                if (File.Exists (temporary))
                {
                    try { File.Delete (temporary); }
                    catch { }
                }
            }
        }

        static void Commit (string temporary, string destination,
                            OverwriteMode overwrite)
        {
            if (File.Exists (destination))
            {
                if (OverwriteMode.Replace != overwrite)
                    throw CliException.Conflict (
                        "destination_exists",
                        "Destination file already exists: " + destination,
                        new Dictionary<string, object> {
                            { "path", destination },
                        });
                File.Replace (temporary, destination, null, true);
            }
            else
            {
                File.Move (temporary, destination);
            }
        }

        sealed class BudgetedWriteStream : Stream
        {
            readonly Stream m_output;
            readonly ExtractionBudget m_budget;

            public long BytesWritten { get; private set; }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return m_output.CanSeek; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return m_output.Length; } }
            public override long Position
            {
                get { return m_output.Position; }
                set { m_output.Position = value; }
            }

            public BudgetedWriteStream (Stream output, ExtractionBudget budget)
            {
                m_output = output;
                m_budget = budget;
            }

            public override void Flush ()
            {
                m_output.Flush();
            }

            public override int Read (byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek (long offset, SeekOrigin origin)
            {
                return m_output.Seek (offset, origin);
            }

            public override void SetLength (long value)
            {
                if (value > m_output.Length)
                {
                    long growth = value - m_output.Length;
                    if (growth > int.MaxValue)
                        throw CliException.Invalid (
                            "entry_size_limit_exceeded",
                            "Output stream requested an unsafe size increase.");
                    m_budget.AddActual (BytesWritten, (int)growth);
                    BytesWritten += growth;
                }
                m_output.SetLength (value);
            }

            public override void Write (byte[] buffer, int offset, int count)
            {
                CancellationState.ThrowIfRequested();
                m_budget.AddActual (BytesWritten, count);
                m_output.Write (buffer, offset, count);
                BytesWritten += count;
            }

            protected override void Dispose (bool disposing)
            {
                if (disposing)
                    m_output.Flush();
                base.Dispose (disposing);
            }
        }
    }

    internal static class GlobMatcher
    {
        public static bool IsMatch (string value, string pattern)
        {
            if (string.IsNullOrEmpty (pattern) || "*" == pattern)
                return true;
            string expression = "^" + Regex.Escape (pattern)
                .Replace ("\\*", ".*")
                .Replace ("\\?", ".") + "$";
            return Regex.IsMatch (value ?? string.Empty, expression,
                                  RegexOptions.IgnoreCase
                                  | RegexOptions.CultureInvariant);
        }

        public static bool IsAnyMatch (string value, IEnumerable<string> patterns)
        {
            var list = patterns as IList<string> ?? patterns.ToList();
            return 0 == list.Count || list.Any (x => IsMatch (value, x));
        }
    }
}
