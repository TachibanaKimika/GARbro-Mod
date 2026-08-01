using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GameRes;
using GameRes.Formats.KiriKiri;

namespace GARbro.Cli
{
    internal sealed class ArchiveSchemeDescriptor
    {
        internal ICrypt Scheme { get; private set; }
        public string Name { get; private set; }
        public string DisplayName { get; private set; }
        public string AlgorithmType { get; private set; }
        public string Family { get; private set; }
        public bool SupportsHxNames { get; private set; }
        public string Source { get; private set; }

        internal ArchiveSchemeDescriptor (string name, string display_name, ICrypt scheme,
                                          string algorithm_type, string family,
                                          bool supports_hx_names, string source)
        {
            Name = name;
            DisplayName = display_name;
            Scheme = scheme;
            AlgorithmType = algorithm_type;
            Family = family;
            SupportsHxNames = supports_hx_names;
            Source = source;
        }

        public Dictionary<string, object> ToDictionary ()
        {
            return new Dictionary<string, object> {
                { "name", Name },
                { "displayName", DisplayName },
                { "algorithmType", AlgorithmType },
                { "family", Family },
                { "supportsHxNames", SupportsHxNames },
                { "source", Source },
            };
        }
    }

    internal sealed class ArchiveSchemeArtifact
    {
        public string Kind { get; private set; }
        public string Path { get; private set; }
        public string Sha256 { get; private set; }

        public ArchiveSchemeArtifact (string kind, string path, string sha256)
        {
            Kind = kind;
            Path = path;
            Sha256 = sha256;
        }

        public Dictionary<string, object> ToDictionary ()
        {
            return new Dictionary<string, object> {
                { "kind", Kind },
                { "path", Path },
                { "sha256", Sha256 },
            };
        }
    }

    internal sealed class ArchiveSchemeResolution
    {
        readonly List<ArchiveSchemeArtifact> m_artifacts;
        readonly List<string> m_source_chain;

        internal ICrypt Scheme { get; private set; }
        internal ArchiveSchemeDescriptor EffectiveScheme { get; private set; }
        internal ArchiveSchemeDescriptor BaseScheme { get; private set; }
        public string RequestedScheme { get; private set; }
        public bool BaseSchemeSuperseded { get; private set; }
        public string CxDumpDirectory { get; private set; }
        public bool CxCompatModifierStripped { get; private set; }
        public bool CxNamesCacheWritten { get; private set; }
        public string HxNamesFile { get; private set; }
        public string Identity { get; private set; }
        public string Fingerprint { get; private set; }

        internal IEnumerable<string> InputArtifactPaths
        {
            get
            {
                return m_artifacts.Select (x => x.Path)
                    .Where (x => !string.IsNullOrEmpty (x));
            }
        }

        internal ArchiveSchemeResolution (
            string requested_scheme,
            ArchiveSchemeDescriptor base_scheme,
            ArchiveSchemeDescriptor effective_scheme,
            bool base_scheme_superseded,
            string cx_dump_directory,
            bool cx_compat_modifier_stripped,
            bool cx_names_cache_written,
            string hx_names_file,
            IEnumerable<ArchiveSchemeArtifact> artifacts,
            IEnumerable<string> source_chain)
        {
            RequestedScheme = requested_scheme;
            BaseScheme = base_scheme;
            EffectiveScheme = effective_scheme;
            Scheme = effective_scheme.Scheme;
            BaseSchemeSuperseded = base_scheme_superseded;
            CxDumpDirectory = cx_dump_directory;
            CxCompatModifierStripped = cx_compat_modifier_stripped;
            CxNamesCacheWritten = cx_names_cache_written;
            HxNamesFile = hx_names_file;
            m_artifacts = new List<ArchiveSchemeArtifact> (artifacts ?? Enumerable.Empty<ArchiveSchemeArtifact>());
            m_source_chain = new List<string> (source_chain ?? Enumerable.Empty<string>());
            Identity = CreateIdentity();
            Fingerprint = "sha256:" + HashText (CreateFingerprintInput());
        }

        internal void RefreshAfterArchiveOpen (string archive_path)
        {
            AddExternalControlBlockArtifact (archive_path);
            Fingerprint = "sha256:" + HashText (CreateFingerprintInput());
        }

        void AddExternalControlBlockArtifact (string archive_path)
        {
            var cx_scheme = Scheme as CxEncryption;
            if (null == cx_scheme || !cx_scheme.ExternalControlBlockLoaded
                || string.IsNullOrEmpty (cx_scheme.ExternalControlBlockPath)
                || string.IsNullOrEmpty (cx_scheme.ExternalControlBlockSha256))
            {
                return;
            }
            string path;
            try
            {
                path = Path.GetFullPath (cx_scheme.ExternalControlBlockPath);
            }
            catch
            {
                return;
            }
            if (m_artifacts.Any (x => string.Equals (
                    x.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            m_artifacts.Add (new ArchiveSchemeArtifact (
                "xp3_tpm_control_block", path,
                cx_scheme.ExternalControlBlockSha256));
        }

        string CreateIdentity ()
        {
            if (!string.IsNullOrEmpty (CxDumpDirectory))
                return string.IsNullOrEmpty (HxNamesFile) ? "xp3:cx-dump" : "xp3:cx-dump+hx-names";
            var name = null != BaseScheme ? BaseScheme.Name : EffectiveScheme.Name;
            return string.IsNullOrEmpty (HxNamesFile)
                ? "xp3:scheme:" + name
                : "xp3:scheme:" + name + "+hx-names";
        }

        string CreateFingerprintInput ()
        {
            var text = new StringBuilder();
            text.AppendLine ("archive-scheme-resolution/v2");
            text.Append ("identity=").AppendLine (Identity ?? string.Empty);
            text.Append ("base=").AppendLine (null != BaseScheme ? BaseScheme.Name : string.Empty);
            text.Append ("baseType=").AppendLine (null != BaseScheme ? BaseScheme.AlgorithmType : string.Empty);
            text.Append ("effectiveType=").AppendLine (EffectiveScheme.AlgorithmType ?? string.Empty);
            text.Append ("family=").AppendLine (EffectiveScheme.Family ?? string.Empty);
            text.Append ("superseded=").AppendLine (BaseSchemeSuperseded ? "true" : "false");
            text.Append ("sources=").AppendLine (string.Join ("+", m_source_chain));
            text.Append ("effectiveMaterial=").AppendLine (
                HashSchemeMaterial (Scheme));
            foreach (var artifact in m_artifacts
                .OrderBy (x => x.Kind, StringComparer.Ordinal)
                .ThenBy (x => x.Sha256, StringComparer.Ordinal))
            {
                text.Append ("artifact=").Append (artifact.Kind).Append ('|')
                    .AppendLine (artifact.Sha256);
            }
            return text.ToString();
        }

        static string HashSchemeMaterial (ICrypt scheme)
        {
            if (null == scheme)
            {
                throw new CliException (
                    ExitCode.InternalError, "internal_error",
                    "xp3_scheme_fingerprint_failed",
                    "The effective XP3 scheme is unavailable for fingerprinting.");
            }
            try
            {
                using (var hash = SHA256.Create())
                using (var sink = new CryptoStream (
                    Stream.Null, hash, CryptoStreamMode.Write))
                {
                    byte[] domain = Encoding.UTF8.GetBytes (
                        "garbro.icrypt-material/v1\0"
                        + scheme.GetType().FullName + "\0");
                    sink.Write (domain, 0, domain.Length);
                    var formatter = new BinaryFormatter {
                        AssemblyFormat = FormatterAssemblyStyle.Simple,
                    };
                    formatter.Serialize (sink, scheme);
                    sink.FlushFinalBlock();
                    return "sha256:" + ToHex (hash.Hash);
                }
            }
            catch (Exception exception)
            {
                throw new CliException (
                    ExitCode.InternalError, "internal_error",
                    "xp3_scheme_fingerprint_failed",
                    "The effective XP3 scheme could not be fingerprinted.",
                    new Dictionary<string, object> {
                        { "algorithmType", scheme.GetType().FullName },
                    }, exception);
            }
        }

        static string HashText (string value)
        {
            using (var hash = SHA256.Create())
                return ToHex (hash.ComputeHash (Encoding.UTF8.GetBytes (value)));
        }

        internal static string ToHex (byte[] value)
        {
            var text = new StringBuilder (value.Length * 2);
            foreach (var b in value)
                text.Append (b.ToString ("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        public Dictionary<string, object> ToDictionary ()
        {
            var result = new Dictionary<string, object> {
                { "identity", Identity },
                { "fingerprint", Fingerprint },
                { "scheme", EffectiveScheme.ToDictionary() },
                { "baseSchemeSuperseded", BaseSchemeSuperseded },
                { "sourceChain", m_source_chain.ToArray() },
            };
            if (!string.IsNullOrEmpty (RequestedScheme))
                result["requestedScheme"] = RequestedScheme;
            if (null != BaseScheme)
                result["baseScheme"] = BaseScheme.ToDictionary();
            if (!string.IsNullOrEmpty (CxDumpDirectory))
            {
                result["cxDumpDirectory"] = CxDumpDirectory;
                result["cxCompatModifierStripped"] = CxCompatModifierStripped;
                result["cxNamesCacheWritten"] = CxNamesCacheWritten;
            }
            if (!string.IsNullOrEmpty (HxNamesFile))
                result["hxNamesFile"] = HxNamesFile;
            if (m_artifacts.Count > 0)
                result["artifacts"] = m_artifacts.Select (x => x.ToDictionary()).ToList();
            return result;
        }

        internal Dictionary<string, object> ToManifestIdentity ()
        {
            return new Dictionary<string, object> {
                { "identity", Identity },
                { "fingerprint", Fingerprint },
            };
        }
    }

    internal static class ArchiveSchemeOptions
    {
        public const string NoCryptAlias = "__NOCRYPT__";
        public const string YuzuCryptAlias = "__YUZUCRYPT__";
        public const string XorCryptAliasTemplate = "__XOR-XX__";

        static readonly Regex XorAliasRe = new Regex (
            @"^__XOR-(?<key>[0-9A-Fa-f]{2})__$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool HasOptions (ParsedCommand command)
        {
            return command.HasOption ("scheme")
                || command.HasOption ("hx-names")
                || command.HasOption ("cx-dump-dir");
        }

        public static ArchiveSchemeResolution Resolve (RuntimeContext runtime, ParsedCommand command,
                                                       string archive_path)
        {
            var requested_scheme = command.GetSingle ("scheme");
            var requested_names = command.GetSingle ("hx-names");
            var requested_cx = command.GetSingle ("cx-dump-dir");
            RequireNonBlankWhenProvided (
                command, "scheme", requested_scheme);
            RequireNonBlankWhenProvided (
                command, "hx-names", requested_names);
            RequireNonBlankWhenProvided (
                command, "cx-dump-dir", requested_cx);
            if (string.IsNullOrWhiteSpace (requested_scheme)
                && string.IsNullOrWhiteSpace (requested_names)
                && string.IsNullOrWhiteSpace (requested_cx))
            {
                return null;
            }

            var source_file = runtime.RequireFile (archive_path);
            ArchiveSchemeDescriptor base_scheme = null;
            if (!string.IsNullOrWhiteSpace (requested_scheme))
                base_scheme = ResolveDescriptor (requested_scheme);

            var effective_scheme = base_scheme;
            var artifacts = new List<ArchiveSchemeArtifact>();
            var source_chain = new List<string>();
            if (null != base_scheme)
                source_chain.Add (base_scheme.Source);

            bool cx_compat_modifier_stripped = false;
            bool cx_names_cache_written = false;
            string cx_dump_directory = null;
            bool base_scheme_superseded = false;
            if (!string.IsNullOrWhiteSpace (requested_cx))
            {
                cx_dump_directory = NormalizeCxDumpDirectory (
                    runtime, requested_cx, out cx_compat_modifier_stripped);
                var import_request = new ResourceParameterCommandResult {
                    Success = true,
                    OutputDirectory = cx_dump_directory,
                    LogFileName = SelectPreferredLog (cx_dump_directory),
                };
                import_request.Metadata["SourceArchive"] = source_file;
                import_request.Metadata["ExplicitCxDumpDirectory"] = "true";
                var import = KrkrDumpResultImporter.Import (
                    import_request, source_file, false, true, false, null);
                if (!import.Success || null == import.GetScheme())
                {
                    throw CliException.Invalid (
                        "xp3_cx_dump_invalid",
                        "The explicit KrkrDump result directory could not produce an XP3 scheme.",
                        new Dictionary<string, object> {
                            { "archivePath", source_file },
                            { "cxDumpDirectory", cx_dump_directory },
                            { "message", import.Message },
                            { "strictDirectory", true },
                        });
                }
                effective_scheme = CreateDescriptor (
                    import.SchemeName, import.GetScheme(), "cx_dump");
                base_scheme_superseded = null != base_scheme;
                source_chain.Add ("cx_dump");
                AddArtifacts (
                    artifacts, "cx_log", import.LogFiles, import);
                AddArtifact (
                    artifacts, "cx_table", import.TableFile,
                    import.GetArtifactSha256 (import.TableFile));
                AddArtifact (
                    artifacts, "cx_order", import.OrderFile,
                    import.GetArtifactSha256 (import.OrderFile));
                AddArtifact (
                    artifacts, "cx_names", import.NamesFile,
                    import.GetArtifactSha256 (import.NamesFile));
                cx_names_cache_written = !string.IsNullOrEmpty (import.NamesFile)
                    && File.Exists (import.NamesFile);
            }

            if (null == effective_scheme)
            {
                throw CliException.Invalid (
                    "xp3_scheme_required",
                    "--hx-names requires --scheme or --cx-dump-dir.",
                    new Dictionary<string, object> {
                        { "archivePath", source_file },
                    });
            }

            string hx_names_file = null;
            if (!string.IsNullOrWhiteSpace (requested_names))
            {
                if (!effective_scheme.SupportsHxNames)
                {
                    throw CliException.Invalid (
                        "xp3_hx_names_not_supported",
                        "--hx-names can only be applied to an Hx v4 scheme.",
                        new Dictionary<string, object> {
                            { "scheme", effective_scheme.ToDictionary() },
                        });
                }
                hx_names_file = runtime.RequireFile (requested_names);
                var names_request = new ResourceParameterCommandResult { Success = true };
                names_request.Metadata["NamesFile"] = hx_names_file;
                var names_import = KrkrDumpResultImporter.ImportNamesFile (
                    names_request, source_file, effective_scheme.Name, false);
                if (!names_import.Success || null == names_import.GetScheme())
                {
                    throw CliException.Invalid (
                        "xp3_hx_names_invalid",
                        "The explicit HxNames table could not be applied to the selected scheme.",
                        new Dictionary<string, object> {
                            { "archivePath", source_file },
                            { "hxNamesFile", hx_names_file },
                            { "scheme", effective_scheme.ToDictionary() },
                            { "message", names_import.Message },
                        });
                }
                effective_scheme = CreateDescriptor (
                    names_import.SchemeName, names_import.GetScheme(), "hx_names");
                source_chain.Add ("hx_names");
                AddArtifact (
                    artifacts, "hx_names", hx_names_file,
                    names_import.GetArtifactSha256 (hx_names_file));
            }

            return new ArchiveSchemeResolution (
                requested_scheme,
                base_scheme,
                effective_scheme,
                base_scheme_superseded,
                cx_dump_directory,
                cx_compat_modifier_stripped,
                cx_names_cache_written,
                hx_names_file,
                artifacts,
                source_chain);
        }

        static void RequireNonBlankWhenProvided (
            ParsedCommand command, string name, string value)
        {
            if (command.HasOption (name) && string.IsNullOrWhiteSpace (value))
            {
                throw CliException.Usage (
                    "missing_option_value",
                    "Option '--" + name + "' requires a non-empty value.");
            }
        }

        public static ArchiveSchemeDescriptor ResolveDescriptor (string requested_name)
        {
            if (string.IsNullOrWhiteSpace (requested_name))
                throw CliException.Invalid ("xp3_scheme_name_empty", "XP3 scheme name cannot be empty.");

            if (string.Equals (requested_name, NoCryptAlias, StringComparison.OrdinalIgnoreCase))
                return CreateDescriptor (NoCryptAlias, new NoCrypt(), "builtin_alias");
            if (string.Equals (requested_name, YuzuCryptAlias, StringComparison.OrdinalIgnoreCase))
                return CreateDescriptor (YuzuCryptAlias, new YuzuCrypt(), "builtin_alias");

            var xor_match = XorAliasRe.Match (requested_name);
            if (xor_match.Success)
            {
                var key = byte.Parse (xor_match.Groups["key"].Value,
                                      NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var canonical_name = string.Format (CultureInfo.InvariantCulture, "__XOR-{0:X2}__", key);
                return CreateDescriptor (canonical_name, new XorCrypt (key), "builtin_alias");
            }
            if (requested_name.StartsWith ("__XOR-", StringComparison.OrdinalIgnoreCase))
            {
                throw CliException.Invalid (
                    "xp3_xor_alias_invalid",
                    "XOR aliases must use the form __XOR-XX__ with exactly two hexadecimal digits.",
                    new Dictionary<string, object> { { "requestedScheme", requested_name } });
            }

            var known_matches = Xp3Opener.KnownSchemes
                .Where (x => string.Equals (x.Key, requested_name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (known_matches.Count > 1)
                ThrowAmbiguous (requested_name, known_matches.Select (x => x.Key));
            if (1 == known_matches.Count)
                return CreateDescriptor (known_matches[0].Key, known_matches[0].Value, "known_scheme");

            var no_crypt_matches = Xp3Opener.NoCryptTitles
                .Where (x => string.Equals (x, requested_name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (no_crypt_matches.Count > 1)
                ThrowAmbiguous (requested_name, no_crypt_matches);
            if (1 == no_crypt_matches.Count)
                return CreateDescriptor (no_crypt_matches[0], new NoCrypt(), "no_crypt_title");

            var suggestions = Xp3Opener.KnownSchemes.Keys
                .Concat (Xp3Opener.NoCryptTitles)
                .Where (x => x.IndexOf (requested_name, StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct (StringComparer.OrdinalIgnoreCase)
                .OrderBy (x => x, StringComparer.OrdinalIgnoreCase)
                .Take (8)
                .ToArray();
            throw CliException.Invalid (
                "xp3_scheme_not_found",
                "The requested XP3 scheme does not exist.",
                new Dictionary<string, object> {
                    { "requestedScheme", requested_name },
                    { "suggestions", suggestions },
                });
        }

        public static ArchiveSchemeResolution FinalizeAfterOpen (
            ArcFile archive, ArchiveSchemeResolution resolution,
            string archive_path)
        {
            if (null != resolution || null == archive)
                return resolution;
            var xp3_archive = archive as Xp3Archive;
            if (null == xp3_archive || null == xp3_archive.EffectiveScheme)
                return null;

            ICrypt effective_scheme = xp3_archive.EffectiveScheme;
            string canonical_name = Xp3Opener.KnownSchemes
                .Where (x => object.ReferenceEquals (x.Value, effective_scheme))
                .Select (x => x.Key)
                .OrderBy (x => x, StringComparer.Ordinal)
                .FirstOrDefault();
            if (string.IsNullOrEmpty (canonical_name)
                && effective_scheme.GetType() == typeof(NoCrypt))
            {
                canonical_name = NoCryptAlias;
            }
            if (string.IsNullOrEmpty (canonical_name))
                canonical_name = "__AUTO__:" + effective_scheme.GetType().FullName;

            var descriptor = CreateDescriptor (
                canonical_name, effective_scheme, "auto_detected");
            resolution = new ArchiveSchemeResolution (
                null, descriptor, descriptor, false, null, false, false,
                null, null, new[] { "auto_detected" });
            resolution.RefreshAfterArchiveOpen (archive_path);
            return resolution;
        }

        public static void RequireExplicitManifestScheme (
            ArcFile archive, ArchiveSchemeResolution resolution)
        {
            if (null == archive || null != resolution
                || !string.Equals (archive.Tag, "XP3", StringComparison.Ordinal))
            {
                return;
            }
            var encrypted = archive.Dir.OfType<Xp3Entry>()
                .FirstOrDefault (x => x.IsEncrypted && null != x.Cipher);
            if (null == encrypted)
                return;

            string suggested_scheme = Xp3Opener.KnownSchemes
                .Where (x => object.ReferenceEquals (x.Value, encrypted.Cipher))
                .Select (x => x.Key)
                .OrderBy (x => x, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            var details = new Dictionary<string, object> {
                { "archiveTag", archive.Tag },
                { "algorithmType", encrypted.Cipher.GetType().FullName },
                { "requiresExplicitScheme", true },
            };
            if (!string.IsNullOrEmpty (suggested_scheme))
                details["suggestedScheme"] = suggested_scheme;
            throw CliException.Invalid (
                "xp3_manifest_scheme_required",
                "Resumable encrypted XP3 extraction requires an explicit --scheme or --cx-dump-dir.",
                details);
        }

        static void ThrowAmbiguous (string requested_name, IEnumerable<string> matches)
        {
            throw CliException.Invalid (
                "xp3_scheme_ambiguous",
                "The requested XP3 scheme name is ambiguous when compared case-insensitively.",
                new Dictionary<string, object> {
                    { "requestedScheme", requested_name },
                    { "matches", matches.OrderBy (x => x, StringComparer.Ordinal).ToArray() },
                });
        }

        public static IEnumerable<ArchiveSchemeDescriptor> EnumerateDescriptors ()
        {
            yield return CreateDescriptor (NoCryptAlias, new NoCrypt(), "builtin_alias");
            yield return CreateDescriptor (YuzuCryptAlias, new YuzuCrypt(), "builtin_alias");
            yield return new ArchiveSchemeDescriptor (
                XorCryptAliasTemplate, "XOR byte alias (__XOR-XX__)", null,
                typeof(XorCrypt).Name, "xor", false, "builtin_alias");

            var known_names = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Xp3Opener.KnownSchemes.OrderBy (x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                known_names.Add (pair.Key);
                yield return CreateDescriptor (pair.Key, pair.Value, "known_scheme");
            }
            foreach (var title in Xp3Opener.NoCryptTitles.OrderBy (x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!known_names.Contains (title))
                    yield return CreateDescriptor (title, new NoCrypt(), "no_crypt_title");
            }
        }

        internal static ArchiveSchemeDescriptor CreateDescriptor (string name, ICrypt scheme,
                                                                   string source)
        {
            var display_name = Xp3Opener.GetSchemeDisplayName (name);
            if (string.IsNullOrWhiteSpace (display_name))
                display_name = name;
            return new ArchiveSchemeDescriptor (
                name,
                display_name,
                scheme,
                null != scheme ? scheme.GetType().Name : string.Empty,
                GetFamily (scheme),
                scheme is HxCrypt,
                source);
        }

        static string GetFamily (ICrypt scheme)
        {
            if (scheme is HxCrypt)
                return "hx-v4";
            if (scheme is HxCryptLite)
                return "hx-lite";
            if (scheme is YuzuCrypt)
                return "yuzu";
            if (scheme is NoCrypt)
                return "none";
            if (scheme is XorCrypt)
                return "xor";
            if (scheme is CxEncryption)
                return "cx";
            return null == scheme ? "template" : "custom";
        }

        static string NormalizeCxDumpDirectory (RuntimeContext runtime, string value,
                                                out bool compat_modifier_stripped)
        {
            compat_modifier_stripped = false;
            value = value.Trim();
            var separator = value.LastIndexOf ('|');
            if (separator >= 0)
            {
                var modifier = value.Substring (separator + 1);
                if (!string.Equals (modifier, "garbro-importer", StringComparison.OrdinalIgnoreCase))
                {
                    throw CliException.Invalid (
                        "xp3_cx_dump_modifier_invalid",
                        "The only supported --cx-dump-dir compatibility modifier is |garbro-importer.",
                        new Dictionary<string, object> { { "modifier", modifier } });
                }
                value = value.Substring (0, separator);
                compat_modifier_stripped = true;
            }
            string directory = runtime.RequireDirectory (value);
            RejectCxReparsePoints (directory);
            return directory;
        }

        static void RejectCxReparsePoints (string directory)
        {
            string current = Path.GetFullPath (directory);
            while (!string.IsNullOrEmpty (current))
            {
                if ((Directory.Exists (current) || File.Exists (current))
                    && 0 != (File.GetAttributes (current)
                             & FileAttributes.ReparsePoint))
                {
                    throw CliException.Invalid (
                        "xp3_cx_dump_reparse_point",
                        "--cx-dump-dir must not traverse a reparse point.",
                        new Dictionary<string, object> {
                            { "cxDumpDirectory", directory },
                            { "path", current },
                        });
                }
                string parent = Path.GetDirectoryName (current);
                if (string.IsNullOrEmpty (parent)
                    || string.Equals (parent, current,
                                      StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
        }

        static string SelectPreferredLog (string directory)
        {
            try
            {
                return Directory.EnumerateFiles (directory, "KrkrDump-*.log")
                    .OrderByDescending (x => File.GetLastWriteTimeUtc (x))
                    .ThenBy (x => x, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        static void AddArtifacts (ICollection<ArchiveSchemeArtifact> artifacts, string kind,
                                  IEnumerable<string> paths,
                                  KrkrDumpImportResult import)
        {
            if (null == paths)
                return;
            foreach (var path in paths)
                AddArtifact (
                    artifacts, kind, path,
                    null != import ? import.GetArtifactSha256 (path) : null);
        }

        static void AddArtifact (ICollection<ArchiveSchemeArtifact> artifacts, string kind,
                                 string path, string trusted_sha256 = null)
        {
            if (string.IsNullOrEmpty (path))
                return;
            path = Path.GetFullPath (path);
            if (!string.IsNullOrEmpty (trusted_sha256))
            {
                artifacts.Add (new ArchiveSchemeArtifact (
                    kind, path, trusted_sha256));
                return;
            }
            if (!File.Exists (path))
                return;
            using (var input = new FileStream (
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var hash = SHA256.Create())
            {
                artifacts.Add (new ArchiveSchemeArtifact (
                    kind, path, ArchiveSchemeResolution.ToHex (
                        hash.ComputeHash (input))));
            }
        }
    }
}
