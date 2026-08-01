using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        public string BudgetSource { get; private set; }

        public static ExtractionPolicy FromCommand (ParsedCommand command)
        {
            return FromCommand (command, null);
        }

        public static ExtractionPolicy FromCommand (
            ParsedCommand command, ExtractionLimits automatic_limits)
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
            string budget = command.GetSingle ("budget");
            bool automatic = !string.IsNullOrEmpty (budget);
            if (automatic && !"auto".Equals (budget, StringComparison.OrdinalIgnoreCase))
            {
                throw CliException.Usage (
                    "invalid_budget_mode", "--budget currently accepts only: auto.");
            }
            if (automatic && null == automatic_limits)
            {
                throw CliException.Usage (
                    "automatic_budget_unavailable",
                    "--budget auto is available only for archive operations with a completed plan.");
            }
            long default_max_files = automatic
                ? automatic_limits.MaxFiles : DefaultMaxFiles;
            long default_max_total = automatic
                ? automatic_limits.MaxTotalBytes : DefaultMaxTotalBytes;
            long default_max_entry = automatic
                ? automatic_limits.MaxEntryBytes : DefaultMaxEntryBytes;
            int default_max_depth = automatic
                ? automatic_limits.MaxDepth : DefaultMaxDepth;
            return new ExtractionPolicy {
                MaxFiles = command.GetInt64 (
                    "max-files", default_max_files, 1, int.MaxValue),
                MaxTotalBytes = command.GetInt64 (
                    "max-total-bytes", default_max_total, 1, long.MaxValue),
                MaxEntryBytes = command.GetInt64 (
                    "max-entry-bytes", default_max_entry, 1, long.MaxValue),
                MaxDepth = (int)command.GetInt64 (
                    "max-depth", default_max_depth, 1, 1024),
                Overwrite = mode,
                DryRun = command.HasFlag ("dry-run"),
                BudgetSource = automatic ? "archivePlan" : "explicitOrDefault",
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
                { "budgetSource", BudgetSource },
            };
        }
    }

    internal sealed class ResolvedOutputPath
    {
        public string RelativePath { get; private set; }
        public string FullPath { get; private set; }
        public int Depth { get; private set; }

        public ResolvedOutputPath (string relative_path, string full_path, int depth)
        {
            RelativePath = relative_path;
            FullPath = full_path;
            Depth = depth;
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
        readonly HashSet<string> m_destination_parents =
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
            ValidateDestinationRoot (root);
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
            ResolvedOutputPath result = NormalizeAndValidate (entry_name);
            Reserve (result, entry_name);
            return result.FullPath;
        }

        public ResolvedOutputPath NormalizeAndValidate (string entry_name)
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
            return new ResolvedOutputPath (relative, destination, segments.Length);
        }

        public void Reserve (ResolvedOutputPath output, string entry_name)
        {
            if (null == output)
                throw new ArgumentNullException ("output");
            if (!TryReserve (output))
                throw UnsafePath (entry_name, "destination_collision");
        }

        public bool TryReserve (ResolvedOutputPath output)
        {
            if (null == output)
                throw new ArgumentNullException ("output");
            if (m_destinations.Contains (output.FullPath)
                || m_destination_parents.Contains (output.FullPath))
            {
                return false;
            }

            string parent = Path.GetDirectoryName (output.FullPath);
            while (!string.IsNullOrEmpty (parent)
                   && !string.Equals (parent, m_root,
                                      StringComparison.OrdinalIgnoreCase))
            {
                if (m_destinations.Contains (parent))
                    return false;
                parent = Path.GetDirectoryName (parent);
            }

            m_destinations.Add (output.FullPath);
            parent = Path.GetDirectoryName (output.FullPath);
            while (!string.IsNullOrEmpty (parent)
                   && !string.Equals (parent, m_root,
                                      StringComparison.OrdinalIgnoreCase))
            {
                m_destination_parents.Add (parent);
                parent = Path.GetDirectoryName (parent);
            }
            return true;
        }

        void ValidateExistingPathComponents (
            string entry_name, IList<string> segments)
        {
            string current = m_root;
            RejectReparsePoint (entry_name, current);
            for (int i = 0; i < segments.Count; ++i)
            {
                current = Path.Combine (current, segments[i]);
                RejectReparsePoint (entry_name, current);
                if (i + 1 < segments.Count && File.Exists (current))
                    throw UnsafePath (entry_name, "parent_is_file");
            }
        }

        void ValidateDestinationRoot (string original_root)
        {
            string current = m_root;
            while (!string.IsNullOrEmpty (current))
            {
                if (File.Exists (current))
                {
                    throw CliException.Invalid (
                        "invalid_destination",
                        "Destination path traverses an existing file: "
                            + original_root,
                        new Dictionary<string, object> {
                            { "path", m_root },
                            { "component", current },
                            { "reason", "component_is_file" },
                        });
                }
                if (Directory.Exists (current))
                {
                    FileAttributes attributes = File.GetAttributes (current);
                    if (0 != (attributes & FileAttributes.ReparsePoint))
                    {
                        throw CliException.Invalid (
                            "invalid_destination",
                            "Destination path traverses a reparse point: "
                                + original_root,
                            new Dictionary<string, object> {
                                { "path", m_root },
                                { "component", current },
                                { "reason", "reparse_point" },
                            });
                    }
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

        public long ObservedBytes { get; private set; }
        public long TotalBytes { get { return ObservedBytes; } }

        public ExtractionBudget (ExtractionPolicy policy)
        {
            m_policy = policy;
        }

        public void CheckDeclaredEntry (long size)
        {
            if (size > m_policy.MaxEntryBytes)
                throw LimitExceeded ("entry_size_limit_exceeded", size,
                                     m_policy.MaxEntryBytes);
            if (TotalBytes > m_policy.MaxTotalBytes
                || size > m_policy.MaxTotalBytes - TotalBytes)
                throw LimitExceeded ("total_size_limit_exceeded",
                                     AddSaturating (TotalBytes, size),
                                     m_policy.MaxTotalBytes);
        }

        public void AddActual (long entry_bytes, int count)
        {
            long entry_observed = AddSaturating (entry_bytes, count);
            long total_observed = AddSaturating (ObservedBytes, count);

            // Charge bytes as soon as a decoder or encoder presents them to a
            // materialization stream.  Failed atomic writes deliberately keep
            // their charge so repeated late failures cannot bypass max-total.
            ObservedBytes = total_observed;

            if (entry_observed > m_policy.MaxEntryBytes)
                throw LimitExceeded ("entry_size_limit_exceeded",
                                     entry_observed,
                                     m_policy.MaxEntryBytes);
            if (total_observed > m_policy.MaxTotalBytes)
                throw LimitExceeded ("total_size_limit_exceeded",
                                     total_observed,
                                     m_policy.MaxTotalBytes);
        }

        static long AddSaturating (long value, long count)
        {
            return value > long.MaxValue - count
                ? long.MaxValue : value + count;
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

    internal sealed class FileWriteResult
    {
        public long BytesWritten { get; private set; }
        public string Sha256 { get; private set; }

        public FileWriteResult (long bytes_written, string sha256)
        {
            BytesWritten = bytes_written;
            Sha256 = sha256;
        }
    }

    internal static class Sha256Utility
    {
        public static string ComputeFile (string path)
        {
            using (var input = new FileStream (
                path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha256 = SHA256.Create())
            {
                var buffer = new byte[0x10000];
                for (;;)
                {
                    CancellationState.ThrowIfRequested();
                    int read = input.Read (buffer, 0, buffer.Length);
                    if (0 == read)
                        break;
                    sha256.TransformBlock (buffer, 0, read, buffer, 0);
                }
                sha256.TransformFinalBlock (new byte[0], 0, 0);
                return ToHex (sha256.Hash);
            }
        }

        public static string ToHex (byte[] value)
        {
            if (null == value)
                return null;
            var text = new StringBuilder (value.Length * 2);
            foreach (byte item in value)
                text.Append (item.ToString ("x2", System.Globalization.CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }

    internal static class SafeFileWriter
    {
        public static long CopyToFile (Stream input, string destination,
                                       OverwriteMode overwrite,
                                       ExtractionBudget budget)
        {
            return CopyToFile (
                input, destination, overwrite, budget, false).BytesWritten;
        }

        public static FileWriteResult CopyToFile (
            Stream input, string destination, OverwriteMode overwrite,
            ExtractionBudget budget, bool compute_sha256)
        {
            string directory = Path.GetDirectoryName (destination);
            Directory.CreateDirectory (directory);
            string temporary = destination + ".garbro-"
                + Guid.NewGuid().ToString ("N") + ".partial";
            long entry_bytes = 0;
            HashAlgorithm sha256 = compute_sha256 ? SHA256.Create() : null;
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
                        if (null != sha256)
                            sha256.TransformBlock (buffer, 0, read, buffer, 0);
                        output.Write (buffer, 0, read);
                        entry_bytes += read;
                    }
                    output.Flush();
                }

                string checksum = null;
                if (null != sha256)
                {
                    sha256.TransformFinalBlock (new byte[0], 0, 0);
                    checksum = Sha256Utility.ToHex (sha256.Hash);
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
                return new FileWriteResult (entry_bytes, checksum);
            }
            finally
            {
                if (null != sha256)
                    sha256.Dispose();
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
                long old_length = m_output.Length;
                if (value > old_length)
                {
                    long growth = value - old_length;
                    if (growth > int.MaxValue)
                        throw CliException.Invalid (
                            "entry_size_limit_exceeded",
                            "Output stream requested an unsafe size increase.");
                    m_budget.AddActual (old_length, (int)growth);
                    m_output.SetLength (value);
                }
                else
                {
                    m_output.SetLength (value);
                }
                BytesWritten = m_output.Length;
            }

            public override void Write (byte[] buffer, int offset, int count)
            {
                CancellationState.ThrowIfRequested();
                long old_length = m_output.Length;
                long end_position;
                try
                {
                    end_position = checked (m_output.Position + count);
                }
                catch (OverflowException)
                {
                    throw CliException.Invalid (
                        "entry_size_limit_exceeded",
                        "Output stream requested an unsafe write position.");
                }
                long growth = Math.Max (0, end_position - old_length);
                if (growth > int.MaxValue)
                    throw CliException.Invalid (
                        "entry_size_limit_exceeded",
                        "Output stream requested an unsafe size increase.");
                if (growth > 0)
                    m_budget.AddActual (old_length, (int)growth);
                m_output.Write (buffer, offset, count);
                BytesWritten = m_output.Length;
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
