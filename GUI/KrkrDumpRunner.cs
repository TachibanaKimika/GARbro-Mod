using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GameRes;
using GARbro.GUI.Strings;

namespace GARbro.GUI
{
    internal class KrkrDumpRunRequest
    {
        public string SourceArchive { get; set; }
        public string GameExecutable { get; set; }
        public string OutputDirectory { get; set; }
    }

    internal class KrkrDumpRuntimeMissingException : FileNotFoundException
    {
        public string Architecture { get; private set; }

        public KrkrDumpRuntimeMissingException (string message, string architecture)
            : base (message)
        {
            Architecture = architecture;
        }
    }

    internal class KrkrDumpRunner
    {
        public const string CommandName = "KiriKiri.KrkrDump";
        public const string SourceRepositoryUrl = "https://github.com/crskycode/KrkrDump";

        public ResourceParameterCommandResult Run (KrkrDumpRunRequest request, Action<string> report_status)
        {
            if (string.IsNullOrEmpty (request.SourceArchive))
                throw new InvalidOperationException (Text ("KrkrDumpNoArchive"));
            if (string.IsNullOrEmpty (request.GameExecutable) || !File.Exists (request.GameExecutable))
                throw new FileNotFoundException (Text ("KrkrDumpExecutableNotFound"), request.GameExecutable);

            var architecture = GetExecutableArchitecture (request.GameExecutable);
            var tool_dir = ResolveToolDirectory (request.GameExecutable, architecture);
            if (string.IsNullOrEmpty (tool_dir))
                throw new KrkrDumpRuntimeMissingException (string.Format (Text ("KrkrDumpRuntimeNotFound"), architecture), architecture);

            var final_output_dir = Path.GetFullPath (request.OutputDirectory);
            Directory.CreateDirectory (final_output_dir);
            var dump_dir = Path.Combine (final_output_dir, ".krkrdump");
            Directory.CreateDirectory (dump_dir);
            var runtime_dir = Path.Combine (dump_dir, "runtime");
            Directory.CreateDirectory (runtime_dir);

            Report (report_status, Text ("KrkrDumpPreparingRuntime"));
            CopyDirectory (tool_dir, runtime_dir);
            var loader = Path.Combine (runtime_dir, "KrkrDumpLoader.exe");
            var dll = Path.Combine (runtime_dir, "KrkrDump.dll");
            if (!File.Exists (loader) || !File.Exists (dll))
                throw new KrkrDumpRuntimeMissingException (Text ("KrkrDumpRuntimeIncomplete"), architecture);

            WriteConfiguration (Path.Combine (runtime_dir, "KrkrDump.json"), dump_dir);

            Report (report_status, Text ("KrkrDumpLaunching"));
            var start = new ProcessStartInfo
            {
                FileName = loader,
                Arguments = Quote (request.GameExecutable),
                WorkingDirectory = runtime_dir,
                UseShellExecute = true,
                Verb = "runas",
            };

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
                throw new OperationCanceledException (Text ("KrkrDumpLaunchCanceled"));

            Report (report_status, Text ("KrkrDumpWaitingExit"));
            process.WaitForExit();

            Report (report_status, Text ("KrkrDumpCollectingOutput"));
            var log_file = CopyNewestLog (runtime_dir, dump_dir);
            CopyOutputFile ("CxdecTable.bin", dump_dir, runtime_dir, Path.GetDirectoryName (request.GameExecutable));
            CopyOutputFile ("CxdecOrder.bin", dump_dir, runtime_dir, Path.GetDirectoryName (request.GameExecutable));

            var result = new ResourceParameterCommandResult
            {
                Success = true,
                OutputDirectory = dump_dir,
                LogFileName = log_file,
                Message = Text ("KrkrDumpFinished"),
            };
            result.Metadata["SourceArchive"] = request.SourceArchive;
            result.Metadata["GameExecutable"] = request.GameExecutable;
            result.Metadata["GameDirectory"] = Path.GetDirectoryName (request.GameExecutable);
            result.Metadata["RuntimeDirectory"] = runtime_dir;
            return result;
        }

        static string FindNewestLog (params string[] directories)
        {
            return directories.Where (x => !string.IsNullOrEmpty (x) && Directory.Exists (x))
                .SelectMany (x => Directory.GetFiles (x, "KrkrDump-*.log"))
                .OrderByDescending (File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }


        string ResolveToolDirectory (string game_executable, string architecture)
        {
            var base_dir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string>
            {
                Path.Combine (base_dir, "Tools", "KrkrDump", architecture),
                Path.Combine (base_dir, "KrkrDump", architecture),
            };
            var dev_root = Path.GetFullPath (Path.Combine (base_dir, "..", "..", ".."));
            if ("x64" == architecture)
            {
                candidates.Add (Path.Combine (dev_root, "KrkrDump", "x64", "Release"));
                candidates.Add (Path.Combine (dev_root, "KrkrDump", "x64", "Debug"));
            }
            else
            {
                candidates.Add (Path.Combine (base_dir, "Tools", "KrkrDump", "x86"));
                candidates.Add (Path.Combine (base_dir, "Tools", "KrkrDump", "Win32"));
                candidates.Add (Path.Combine (base_dir, "KrkrDump", "x86"));
                candidates.Add (Path.Combine (base_dir, "KrkrDump", "Win32"));
                candidates.Add (Path.Combine (dev_root, "KrkrDump", "x86", "Release"));
                candidates.Add (Path.Combine (dev_root, "KrkrDump", "x86", "Debug"));
                candidates.Add (Path.Combine (dev_root, "KrkrDump", "Win32", "Release"));
                candidates.Add (Path.Combine (dev_root, "KrkrDump", "Win32", "Debug"));
            }
            candidates.Add (Path.Combine (base_dir, "Tools", "KrkrDump"));
            candidates.Add (Path.Combine (base_dir, "KrkrDump"));
            candidates.Add (Path.Combine (dev_root, "KrkrDump", "Release"));
            candidates.Add (Path.Combine (dev_root, "KrkrDump", "Debug"));

            return candidates.FirstOrDefault (IsToolDirectory);
        }

        static string GetExecutableArchitecture (string file_name)
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

        static bool IsToolDirectory (string directory)
        {
            if (string.IsNullOrEmpty (directory) || !Directory.Exists (directory))
                return false;
            return File.Exists (Path.Combine (directory, "KrkrDumpLoader.exe"))
                && File.Exists (Path.Combine (directory, "KrkrDump.dll"));
        }

        static void CopyDirectory (string source, string destination)
        {
            foreach (var dir in Directory.GetDirectories (source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory (Path.Combine (destination, dir.Substring (source.Length).TrimStart (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            foreach (var file in Directory.GetFiles (source, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring (source.Length).TrimStart (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.Combine (destination, relative);
                Directory.CreateDirectory (Path.GetDirectoryName (target));
                File.Copy (file, target, true);
            }
        }

        static string CopyNewestLog (string runtime_dir, string output_dir)
        {
            var log = FindNewestLog (runtime_dir);
            if (string.IsNullOrEmpty (log))
                return null;
            var target = Path.Combine (output_dir, Path.GetFileName (log));
            File.Copy (log, target, true);
            return target;
        }

        static void CopyIfExists (string source, string output_dir)
        {
            if (File.Exists (source))
                File.Copy (source, Path.Combine (output_dir, Path.GetFileName (source)), true);
        }

        static void CopyOutputFile (string name, string output_dir, params string[] source_dirs)
        {
            foreach (var dir in source_dirs)
            {
                if (string.IsNullOrEmpty (dir))
                    continue;
                var source = Path.Combine (dir, name);
                if (File.Exists (source))
                {
                    CopyIfExists (source, output_dir);
                    return;
                }
            }
        }

        static void WriteConfiguration (string path, string output_dir)
        {
            var rules = new[]
            {
                @"file://\\./.+?\.xp3>(.+?\..+$)",
                @"archive://./(.+)",
                @"arc://./(.+)",
                @"bres://./(.+)",
            };
            var json = new StringBuilder();
            json.AppendLine ("{");
            json.AppendLine ("  \"logLevel\": 2,");
            json.AppendLine ("  \"enableExtract\": false,");
            json.AppendFormat ("  \"outputDirectory\": \"{0}\",\r\n", JsonEscape (output_dir));
            json.AppendLine ("  \"rules\": [");
            for (int i = 0; i < rules.Length; ++i)
            {
                json.AppendFormat ("    \"{0}\"{1}\r\n", JsonEscape (rules[i]), i + 1 == rules.Length ? "" : ",");
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
            File.WriteAllText (path, json.ToString(), Encoding.UTF8);
        }

        static string JsonEscape (string value)
        {
            if (string.IsNullOrEmpty (value))
                return "";
            return value.Replace ("\\", "\\\\").Replace ("\"", "\\\"");
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

        static string Text (string name)
        {
            return guiStrings.ResourceManager.GetString (name) ?? name;
        }
    }
}
