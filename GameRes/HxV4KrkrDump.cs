//! \file       HxV4KrkrDump.cs
//! \date       2026 Jul 30
//! \brief      Shared KrkrDump runner for Hx v4 GUI and CLI workflows.
//
// Copyright (C) 2026 by GARbro-Mod-Onachi contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GameRes
{
    public sealed class HxV4KrkrDumpRunRequest
    {
        public string SourceArchive { get; set; }
        public string GameExecutable { get; set; }
        public string OutputDirectory { get; set; }
        public string ToolDirectory { get; set; }
        public bool Elevate { get; set; }
        public Func<bool> CancellationRequested { get; set; }

        public HxV4KrkrDumpRunRequest ()
        {
            Elevate = true;
        }
    }

    public sealed class HxV4KrkrDumpRuntimeMissingException : FileNotFoundException
    {
        public string Architecture { get; private set; }

        public HxV4KrkrDumpRuntimeMissingException (
            string message, string architecture)
            : base (message)
        {
            Architecture = architecture;
        }
    }

    public sealed class HxV4KrkrDumpRunner
    {
        public const string SourceRepositoryUrl =
            "https://github.com/crskycode/KrkrDump";

        public ResourceParameterCommandResult Run (
            HxV4KrkrDumpRunRequest request, Action<string> report_status = null)
        {
            if (null == request)
                throw new ArgumentNullException ("request");
            if (string.IsNullOrEmpty (request.SourceArchive)
                || !File.Exists (request.SourceArchive))
                throw new FileNotFoundException (
                    "The source XP3 archive was not found.", request.SourceArchive);
            if (string.IsNullOrEmpty (request.GameExecutable)
                || !File.Exists (request.GameExecutable))
                throw new FileNotFoundException (
                    "The game executable was not found.", request.GameExecutable);
            if (string.IsNullOrWhiteSpace (request.OutputDirectory))
                throw new ArgumentNullException ("request.OutputDirectory");

            var source_archive = Path.GetFullPath (request.SourceArchive);
            var game_executable = Path.GetFullPath (request.GameExecutable);
            var architecture = GetExecutableArchitecture (game_executable);
            var tool_directory = ResolveToolDirectory (
                game_executable, architecture, request.ToolDirectory);
            if (string.IsNullOrEmpty (tool_directory))
            {
                throw new HxV4KrkrDumpRuntimeMissingException (
                    "A matching KrkrDump runtime was not found for " + architecture + ".",
                    architecture);
            }

            var final_output_directory = Path.GetFullPath (request.OutputDirectory);
            Directory.CreateDirectory (final_output_directory);
            var dump_directory = Path.Combine (
                final_output_directory, ".krkrdump");
            Directory.CreateDirectory (dump_directory);
            var runtime_directory = Path.Combine (dump_directory, "runtime");
            Directory.CreateDirectory (runtime_directory);

            Report (report_status, "preparing_runtime");
            CopyDirectory (tool_directory, runtime_directory);
            var loader = Path.Combine (runtime_directory, "KrkrDumpLoader.exe");
            var dll = Path.Combine (runtime_directory, "KrkrDump.dll");
            if (!File.Exists (loader) || !File.Exists (dll))
            {
                throw new HxV4KrkrDumpRuntimeMissingException (
                    "The selected KrkrDump runtime is incomplete.", architecture);
            }

            WriteConfiguration (
                Path.Combine (runtime_directory, "KrkrDump.json"), dump_directory);
            var original_outputs = CaptureOutputState (
                runtime_directory, Path.GetDirectoryName (game_executable));

            Report (report_status, "launching");
            var start = new ProcessStartInfo {
                FileName = loader,
                Arguments = Quote (game_executable),
                WorkingDirectory = runtime_directory,
                UseShellExecute = true,
            };
            if (request.Elevate)
                start.Verb = "runas";

            Process process;
            try
            {
                process = Process.Start (start);
            }
            catch (Win32Exception X)
            {
                throw new OperationCanceledException (X.Message, X);
            }
            if (null == process)
                throw new OperationCanceledException ("KrkrDump launch was canceled.");

            Report (report_status, "waiting_for_game_exit");
            using (process)
            {
                while (!process.WaitForExit (250))
                {
                    if (null != request.CancellationRequested
                        && request.CancellationRequested())
                    {
                        throw new OperationCanceledException (
                            "KrkrDump wait was canceled. The game process was left running.");
                    }
                }
            }

            Report (report_status, "collecting_output");
            var game_directory = Path.GetDirectoryName (game_executable);
            var log_file = CopyNewestLog (
                runtime_directory, dump_directory, original_outputs,
                game_directory);
            bool table_copied = CopyOutputFile (
                "CxdecTable.bin", dump_directory,
                original_outputs, runtime_directory, game_directory);
            bool order_copied = CopyOutputFile (
                "CxdecOrder.bin", dump_directory,
                original_outputs, runtime_directory, game_directory);
            bool has_output = !string.IsNullOrEmpty (log_file)
                || table_copied || order_copied;

            var result = new ResourceParameterCommandResult {
                Success = has_output,
                OutputDirectory = dump_directory,
                LogFileName = log_file,
                Message = has_output
                    ? "KrkrDump output was collected."
                    : "KrkrDump did not produce a log or Cxdec output.",
            };
            result.Metadata["SourceArchive"] = source_archive;
            result.Metadata["GameExecutable"] = game_executable;
            result.Metadata["GameDirectory"] = game_directory;
            result.Metadata["RuntimeDirectory"] = runtime_directory;
            result.Metadata["Architecture"] = architecture;
            result.Metadata["ToolDirectory"] = tool_directory;
            return result;
        }

        public static ResourceParameterCommandResult CollectExistingResult (
            string source_archive, string result_directory,
            string game_executable = null)
        {
            if (string.IsNullOrWhiteSpace (source_archive)
                || !File.Exists (source_archive))
                throw new FileNotFoundException (
                    "The source XP3 archive was not found.", source_archive);
            if (string.IsNullOrWhiteSpace (result_directory)
                || !Directory.Exists (result_directory))
                throw new DirectoryNotFoundException (
                    "The KrkrDump result directory was not found: "
                    + result_directory);
            if (!string.IsNullOrWhiteSpace (game_executable)
                && !File.Exists (game_executable))
                throw new FileNotFoundException (
                    "The game executable was not found.", game_executable);

            source_archive = Path.GetFullPath (source_archive);
            result_directory = Path.GetFullPath (result_directory);
            if (!string.IsNullOrWhiteSpace (game_executable))
                game_executable = Path.GetFullPath (game_executable);
            var game_directory = !string.IsNullOrEmpty (game_executable)
                ? Path.GetDirectoryName (game_executable)
                : Path.GetDirectoryName (source_archive);
            var log_file = Directory.GetFiles (
                    result_directory, "KrkrDump-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending (File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            var result = new ResourceParameterCommandResult {
                Success = true,
                OutputDirectory = result_directory,
                LogFileName = log_file,
                Message = "Existing KrkrDump output was collected.",
            };
            result.Metadata["SourceArchive"] = source_archive;
            result.Metadata["GameDirectory"] = game_directory;
            result.Metadata["RuntimeDirectory"] = result_directory;
            if (!string.IsNullOrEmpty (game_executable))
            {
                result.Metadata["GameExecutable"] = game_executable;
                result.Metadata["Architecture"] =
                    GetExecutableArchitecture (game_executable);
            }
            return result;
        }

        public string ResolveToolDirectory (
            string game_executable, string architecture, string explicit_directory = null)
        {
            if (!string.IsNullOrWhiteSpace (explicit_directory))
            {
                var full = Path.GetFullPath (explicit_directory);
                return IsToolDirectory (full, architecture) ? full : null;
            }

            var base_directory = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string> {
                Path.Combine (base_directory, "Tools", "KrkrDump", architecture),
                Path.Combine (base_directory, "KrkrDump", architecture),
            };
            var development_root = Path.GetFullPath (
                Path.Combine (base_directory, "..", "..", ".."));
            if ("x64" == architecture)
            {
                candidates.Add (Path.Combine (
                    development_root, "KrkrDump", "x64", "Release"));
                candidates.Add (Path.Combine (
                    development_root, "KrkrDump", "x64", "Debug"));
            }
            else
            {
                candidates.Add (Path.Combine (
                    base_directory, "Tools", "KrkrDump", "x86"));
                candidates.Add (Path.Combine (
                    base_directory, "Tools", "KrkrDump", "Win32"));
                candidates.Add (Path.Combine (
                    base_directory, "KrkrDump", "x86"));
                candidates.Add (Path.Combine (
                    base_directory, "KrkrDump", "Win32"));
                candidates.Add (Path.Combine (
                    development_root, "KrkrDump", "x86", "Release"));
                candidates.Add (Path.Combine (
                    development_root, "KrkrDump", "x86", "Debug"));
                candidates.Add (Path.Combine (
                    development_root, "KrkrDump", "Win32", "Release"));
                candidates.Add (Path.Combine (
                    development_root, "KrkrDump", "Win32", "Debug"));
            }
            candidates.Add (Path.Combine (
                base_directory, "Tools", "KrkrDump"));
            candidates.Add (Path.Combine (base_directory, "KrkrDump"));
            candidates.Add (Path.Combine (
                development_root, "KrkrDump", "Release"));
            candidates.Add (Path.Combine (
                development_root, "KrkrDump", "Debug"));
            return candidates.FirstOrDefault (
                directory => IsToolDirectory (directory, architecture));
        }

        public static string GetExecutableArchitecture (string file_name)
        {
            try
            {
                using (var file = File.OpenRead (file_name))
                using (var reader = new BinaryReader (file))
                {
                    if (reader.ReadUInt16() != 0x5A4D)
                        return "x86";
                    file.Position = 0x3C;
                    var pe_offset = reader.ReadInt32();
                    if (pe_offset <= 0 || pe_offset + 6 >= file.Length)
                        return "x86";
                    file.Position = pe_offset;
                    if (reader.ReadUInt32() != 0x00004550)
                        return "x86";
                    var machine = reader.ReadUInt16();
                    return 0x8664 == machine ? "x64" : "x86";
                }
            }
            catch
            {
                return "x86";
            }
        }

        static bool IsToolDirectory (string directory, string architecture)
        {
            if (string.IsNullOrEmpty (directory) || !Directory.Exists (directory))
                return false;
            var loader = Path.Combine (directory, "KrkrDumpLoader.exe");
            var dll = Path.Combine (directory, "KrkrDump.dll");
            if (!File.Exists (loader) || !File.Exists (dll))
                return false;
            return string.IsNullOrEmpty (architecture)
                || architecture.Equals (
                    GetExecutableArchitecture (dll),
                    StringComparison.OrdinalIgnoreCase);
        }

        static void CopyDirectory (string source, string destination)
        {
            foreach (var directory in Directory.GetDirectories (
                source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory (Path.Combine (
                    destination, directory.Substring (source.Length).TrimStart (
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            }
            foreach (var file in Directory.GetFiles (
                source, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring (source.Length).TrimStart (
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.Combine (destination, relative);
                Directory.CreateDirectory (Path.GetDirectoryName (target));
                File.Copy (file, target, true);
            }
        }

        static string CopyNewestLog (
            string runtime_directory, string output_directory,
            IDictionary<string, OutputSnapshot> original_outputs,
            string game_directory)
        {
            var log = new[] { runtime_directory, game_directory }
                .Where (x => !string.IsNullOrEmpty (x) && Directory.Exists (x))
                .SelectMany (x => Directory.GetFiles (x, "KrkrDump-*.log"))
                .Where (x => HasChanged (x, original_outputs))
                .OrderByDescending (File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (string.IsNullOrEmpty (log))
                return null;
            var target = Path.Combine (output_directory, Path.GetFileName (log));
            File.Copy (log, target, true);
            return target;
        }

        static bool CopyOutputFile (
            string name, string output_directory,
            IDictionary<string, OutputSnapshot> original_outputs,
            params string[] source_directories)
        {
            foreach (var directory in source_directories)
            {
                if (string.IsNullOrEmpty (directory))
                    continue;
                var source = Path.Combine (directory, name);
                if (!File.Exists (source)
                    || !HasChanged (source, original_outputs))
                    continue;
                File.Copy (source, Path.Combine (output_directory, name), true);
                return true;
            }
            return false;
        }

        static Dictionary<string, OutputSnapshot> CaptureOutputState (
            params string[] directories)
        {
            var result = new Dictionary<string, OutputSnapshot> (
                StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
            {
                if (string.IsNullOrEmpty (directory)
                    || !Directory.Exists (directory))
                    continue;
                foreach (var file in Directory.EnumerateFiles (
                    directory, "KrkrDump-*.log", SearchOption.TopDirectoryOnly)
                    .Concat (new[] {
                        Path.Combine (directory, "CxdecTable.bin"),
                        Path.Combine (directory, "CxdecOrder.bin"),
                    }))
                {
                    if (!File.Exists (file))
                        continue;
                    var info = new FileInfo (file);
                    result[Path.GetFullPath (file)] = new OutputSnapshot {
                        Length = info.Length,
                        LastWriteTimeUtc = info.LastWriteTimeUtc,
                    };
                }
            }
            return result;
        }

        static bool HasChanged (
            string file, IDictionary<string, OutputSnapshot> original_outputs)
        {
            var full_path = Path.GetFullPath (file);
            OutputSnapshot original;
            if (null == original_outputs
                || !original_outputs.TryGetValue (full_path, out original))
                return true;
            var current = new FileInfo (full_path);
            return current.Length != original.Length
                || current.LastWriteTimeUtc != original.LastWriteTimeUtc;
        }

        sealed class OutputSnapshot
        {
            public long Length;
            public DateTime LastWriteTimeUtc;
        }

        static void WriteConfiguration (string path, string output_directory)
        {
            var rules = new[] {
                @"file://\\./.+?\.xp3>(.+?\..+$)",
                @"archive://./(.+)",
                @"arc://./(.+)",
                @"bres://./(.+)",
            };
            var json = new StringBuilder();
            json.AppendLine ("{");
            json.AppendLine ("  \"logLevel\": 2,");
            json.AppendLine ("  \"enableExtract\": false,");
            json.AppendFormat (
                "  \"outputDirectory\": \"{0}\",\r\n",
                JsonEscape (output_directory));
            json.AppendLine ("  \"rules\": [");
            for (int i = 0; i < rules.Length; ++i)
            {
                json.AppendFormat (
                    "    \"{0}\"{1}\r\n", JsonEscape (rules[i]),
                    i+1 == rules.Length ? "" : ",");
            }
            json.AppendLine ("  ],");
            json.AppendLine ("  \"includeExtensions\": [],");
            json.AppendLine ("  \"excludeExtensions\": [],");
            json.AppendLine ("  \"decryptSimpleCrypt\": true,");
            json.AppendLine ("  \"dumpHash\": true,");
            json.AppendLine ("  \"dumpHxKey\": true,");
            json.AppendLine ("  \"dumpDir\": true,");
            json.AppendLine ("  \"patchSignatureCheck\": false,");
            json.AppendLine ("  \"patchSbeam\": false");
            json.AppendLine ("}");
            File.WriteAllText (path, json.ToString(), new UTF8Encoding (false));
        }

        static string JsonEscape (string value)
        {
            return string.IsNullOrEmpty (value)
                ? string.Empty
                : value.Replace ("\\", "\\\\").Replace ("\"", "\\\"");
        }

        static string Quote (string value)
        {
            return "\"" + value.Replace ("\"", "\\\"") + "\"";
        }

        static void Report (Action<string> report_status, string status)
        {
            if (null != report_status)
                report_status (status);
        }
    }
}
