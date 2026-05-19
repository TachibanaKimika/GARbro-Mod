//! \file       GarExtract.cs
//! \date       Fri Jul 25 05:52:27 2014
//! \brief      Extract archive frontend.
//
// Copyright (C) 2014-2017 by morkt
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
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GameRes;
using GARbro.GUI.Strings;
using GARbro.GUI.Properties;

namespace GARbro.GUI
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Handle "Extract item" command.
        /// </summary>
        private void ExtractItemExec (object sender, ExecutedRoutedEventArgs e)
        {
            var entry = CurrentDirectory.SelectedItem as EntryViewModel;
            if (null == entry && !ViewModel.IsArchive)
            {
                SetStatusText (guiStrings.MsgChooseFiles);
                return;
            }
            GarExtract extractor = null;
            try
            {
                var vm = ViewModel;
                if (vm.IsArchive)
                {
                    var archive_name = vm.Path[vm.Path.Count-2];
                    string destination = GetDefaultExtractDestination (vm.Path.First(), archive_name);
                    if (string.IsNullOrEmpty (destination))
                        destination = Path.GetDirectoryName (vm.Path.First());
                    extractor = new GarExtract (this, archive_name, VFS.Top as ArchiveFileSystem);
                    if (null == entry || (entry.Name == ".." && string.IsNullOrEmpty (vm.Path.Last()))) // root entry
                        extractor.ExtractAll (destination);
                    else
                        extractor.Extract (entry, destination);
                }
                else
                {
                    var selected = GetSelectedFilesInViewOrder();
                    if (!selected.Any())
                    {
                        SetStatusText (guiStrings.MsgChooseFiles);
                        return;
                    }
                    var sources = selected.Select (x => x.Source.Name).ToList();
                    var source = sources.First();
                    string destination = GetDefaultExtractDestination (source, source);
                    SetBusyState();
                    if (string.IsNullOrEmpty (destination))
                    {
                        // extract into directory named after archive
                        if (!string.IsNullOrEmpty (Path.GetExtension (selected.First().Name)))
                            destination = Path.GetFileNameWithoutExtension (source);
                        else
                            destination = vm.Path.First();
                    }
                    extractor = sources.Skip (1).Any()
                        ? new GarExtract (this, sources)
                        : new GarExtract (this, source);
                    extractor.ExtractAll (destination);
                }
            }
            catch (OperationCanceledException X)
            {
                SetStatusText (X.Message);
            }
            catch (Exception X)
            {
                PopupError (X.Message, guiStrings.MsgErrorExtracting);
            }
            finally
            {
                if (null != extractor && !extractor.IsActive)
                    extractor.Dispose();
            }
        }

        private List<EntryViewModel> GetSelectedFilesInViewOrder ()
        {
            var selected = new HashSet<EntryViewModel> (CurrentDirectory.SelectedItems.Cast<EntryViewModel>());
            return CurrentDirectory.Items.Cast<EntryViewModel>()
                .Where (x => selected.Contains (x) && !x.IsDirectory)
                .ToList();
        }

        internal string GetDefaultExtractDestination (string source, string fallback_name, string fallback_destination = "")
        {
            if (!Settings.Default.appAutoSelectExtractPath)
            {
                string destination = Settings.Default.appLastDestination;
                return Directory.Exists (destination) ? destination : fallback_destination;
            }

            string parent = GetLastExtractParent();
            string game_name = FindGameDirectoryName (source);
            if (string.IsNullOrEmpty (game_name))
                game_name = GetFallbackExtractName (fallback_name, source);
            if (string.IsNullOrEmpty (parent) || string.IsNullOrEmpty (game_name))
                return fallback_destination;
            return Path.Combine (parent, game_name);
        }

        private static string GetLastExtractParent ()
        {
            string parent = GetFullPathOrEmpty (Settings.Default.appLastExtractParent);
            if (!string.IsNullOrEmpty (parent))
                return parent;
            return GetParentDirectory (Settings.Default.appLastDestination);
        }

        private static string GetSourceDirectory (string source)
        {
            string path = GetFullPathOrEmpty (source);
            if (string.IsNullOrEmpty (path))
                return "";
            if (Directory.Exists (path))
                return path;
            return Path.GetDirectoryName (path) ?? "";
        }

        private static string FindGameDirectoryName (string source)
        {
            string dir = GetSourceDirectory (source);
            while (!string.IsNullOrEmpty (dir))
            {
                try
                {
                    if (Directory.EnumerateFiles (dir, "*.exe").Any())
                    {
                        string name = new DirectoryInfo (dir).Name;
                        if (!string.IsNullOrEmpty (name))
                            return name;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
                catch (System.Security.SecurityException)
                {
                }
                var parent = Directory.GetParent (dir);
                if (null == parent || parent.FullName == dir)
                    break;
                dir = parent.FullName;
            }
            return "";
        }

        private static string GetFallbackExtractName (string fallback_name, string source)
        {
            string name = "";
            try
            {
                name = Path.GetFileNameWithoutExtension (fallback_name);
            }
            catch (ArgumentException)
            {
            }
            if (!string.IsNullOrEmpty (name))
                return name;
            string dir = GetSourceDirectory (source);
            return string.IsNullOrEmpty (dir) ? "" : new DirectoryInfo (dir).Name;
        }

        internal static string GetParentDirectory (string path)
        {
            path = GetFullPathOrEmpty (path);
            if (string.IsNullOrEmpty (path))
                return "";
            var info = new DirectoryInfo (path);
            if (null != info.Parent)
                return info.Parent.FullName;
            return Path.GetPathRoot (path) ?? "";
        }

        private static string GetFullPathOrEmpty (string path)
        {
            if (string.IsNullOrEmpty (path))
                return "";
            try
            {
                return Path.GetFullPath (path);
            }
            catch (ArgumentException)
            {
                return "";
            }
            catch (NotSupportedException)
            {
                return "";
            }
            catch (PathTooLongException)
            {
                return "";
            }
        }
    }

    sealed internal class GarExtract : GarOperation, IDisposable
    {
        private string              m_arc_name;
        private string              m_arc_source;
        private List<string>        m_arc_sources;
        private string              m_destination = ".";
        private ArchiveFileSystem   m_fs;
        private readonly bool       m_should_ascend;
        private bool                m_skip_images = false;
        private bool                m_skip_script = false;
        private bool                m_skip_audio  = false;
        private bool                m_adjust_image_offset = false;
        private bool                m_convert_audio;
        private ImageFormat         m_image_format;
        private string              m_script_text_output_mode = ScriptTextMode.Filtered;
        private int                 m_extract_count;
        private int                 m_skip_count;
        private bool                m_extract_in_progress = false;

        public bool IsActive { get { return m_extract_in_progress; } }

        public GarExtract (MainWindow parent, string source) : base (parent, guiStrings.TextExtractionError)
        {
            m_arc_source = source;
            m_arc_name = Path.GetFileName (source);
            try
            {
                VFS.ChDir (source);
                m_should_ascend = true;
            }
            catch (Exception X)
            {
                throw new OperationCanceledException (string.Format ("{1}: {0}", X.Message, m_arc_name));
            }
            m_fs = VFS.Top as ArchiveFileSystem;
        }

        public GarExtract (MainWindow parent, IEnumerable<string> sources) : base (parent, guiStrings.TextExtractionError)
        {
            m_arc_sources = sources.Select (Path.GetFullPath).ToList();
            if (!m_arc_sources.Any())
                throw new OperationCanceledException (guiStrings.MsgChooseFiles);
            m_arc_source = m_arc_sources.First();
            m_arc_name = GetArchiveBatchName (m_arc_sources);
            m_should_ascend = false;
        }

        public GarExtract (MainWindow parent, string source, ArchiveFileSystem fs) : base (parent, guiStrings.TextExtractionError)
        {
            if (null == fs)
                throw new UnknownFormatException();
            m_fs = fs;
            m_arc_source = parent.ViewModel.Path.First();
            m_arc_name = Path.GetFileName (source);
            m_should_ascend = false;
        }

        private void PrepareDestination (string destination)
        {
            bool stop_watch = !m_main.ViewModel.IsArchive;
            if (stop_watch)
                m_main.StopWatchDirectoryChanges();
            try
            {
                Directory.CreateDirectory (destination);
                Directory.SetCurrentDirectory (destination);
                Settings.Default.appLastDestination = destination;
                Settings.Default.appLastExtractParent = MainWindow.GetParentDirectory (destination);
            }
            finally
            {
                if (stop_watch)
                    m_main.ResumeWatchDirectoryChanges();
            }
        }

        static string GetArchiveBatchName (IList<string> sources)
        {
            string first_name = Path.GetFileName (sources[0]);
            if (1 == sources.Count)
                return first_name;
            return string.Format ("{0} (+{1})", first_name, sources.Count-1);
        }

        public void ExtractAll (string destination)
        {
            if (m_arc_sources != null)
                ExtractAllArchives (destination);
            else
                ExtractCurrentArchive (destination);
        }

        private void ExtractCurrentArchive (string destination)
        {
            var file_list = m_fs.GetFilesRecursive();
            if (!file_list.Any())
            {
                m_main.SetStatusText (string.Format ("{1}: {0}", guiStrings.MsgEmptyArchive, m_arc_name));
                return;
            }
            destination = GetArchiveDialogDestination (destination);
            var extractDialog = new ExtractArchiveDialog (m_arc_name, destination);
            extractDialog.Owner = m_main;
            var result = extractDialog.ShowDialog();
            if (!result.Value)
                return;
            m_script_text_output_mode = extractDialog.ScriptTextOutputMode;

            destination = extractDialog.Destination;
            if (!string.IsNullOrEmpty (destination))
            {
                destination = Path.GetFullPath (destination);
                PrepareDestination (destination);
            }
            else
                destination = ".";
            m_skip_images = !extractDialog.ExtractImages.IsChecked.Value;
            m_skip_script = !extractDialog.ExtractText.IsChecked.Value;
            m_skip_audio  = !extractDialog.ExtractAudio.IsChecked.Value;
            if (!m_skip_images)
                m_image_format = extractDialog.GetImageFormat (extractDialog.ImageConversionFormat);

            m_main.SetStatusText (string.Format(guiStrings.MsgExtractingTo, m_arc_name, destination));
            ExtractFilesFromArchive (string.Format (guiStrings.MsgExtractingArchive, m_arc_name), file_list);
        }

        private void ExtractAllArchives (string destination)
        {
            destination = GetArchiveDialogDestination (destination);
            var extractDialog = new ExtractArchiveDialog (m_arc_name, destination);
            extractDialog.Owner = m_main;
            var result = extractDialog.ShowDialog();
            if (!result.Value)
                return;
            m_script_text_output_mode = extractDialog.ScriptTextOutputMode;

            destination = extractDialog.Destination;
            if (!string.IsNullOrEmpty (destination))
            {
                destination = Path.GetFullPath (destination);
                PrepareDestination (destination);
            }
            else
                destination = ".";
            m_destination = destination;
            m_skip_images = !extractDialog.ExtractImages.IsChecked.Value;
            m_skip_script = !extractDialog.ExtractText.IsChecked.Value;
            m_skip_audio  = !extractDialog.ExtractAudio.IsChecked.Value;
            if (!m_skip_images)
                m_image_format = extractDialog.GetImageFormat (extractDialog.ImageConversionFormat);

            m_main.SetStatusText (string.Format(guiStrings.MsgExtractingTo, m_arc_name, destination));
            ExtractArchivesFromList (string.Format (guiStrings.MsgExtractingArchive, m_arc_name));
        }

        public void Extract (EntryViewModel entry, string destination)
        {
            var view_model = m_main.ViewModel;
            var selected = m_main.CurrentDirectory.SelectedItems.Cast<EntryViewModel>();
            if (!selected.Any() && entry.Name == "..")
                selected = view_model;

            IEnumerable<Entry> file_list = selected.Select (e => e.Source);
            if (m_fs is TreeArchiveFileSystem)
                file_list = (m_fs as TreeArchiveFileSystem).GetFilesRecursive (file_list);

            if (!file_list.Any())
            {
                m_main.SetStatusText (guiStrings.MsgChooseFiles);
                return;
            }

            ExtractDialog extractDialog;
            bool multiple_files = file_list.Skip (1).Any();
            if (multiple_files)
                extractDialog = new ExtractArchiveDialog (m_arc_name, GetArchiveDialogDestination (destination));
            else
                extractDialog = new ExtractFile (entry, GetArchiveDialogDestination (destination));
            extractDialog.Owner = m_main;
            var result = extractDialog.ShowDialog();
            if (!result.Value)
                return;
            m_script_text_output_mode = extractDialog.ScriptTextOutputMode;
            if (multiple_files)
            {
                m_skip_images = !Settings.Default.appExtractImages;
                m_skip_script = !Settings.Default.appExtractText;
                m_skip_audio  = !Settings.Default.appExtractAudio;
            }
            destination = extractDialog.Destination;
            if (!string.IsNullOrEmpty (destination))
            {
                destination = Path.GetFullPath (destination);
                PrepareDestination (destination);
            }
            if (!m_skip_images)
                m_image_format = FormatCatalog.Instance.ImageFormats.FirstOrDefault (f => f.Tag.Equals (Settings.Default.appImageFormat));

            ExtractFilesFromArchive (string.Format (guiStrings.MsgExtractingFile, m_arc_name), file_list);
        }

        string GetArchiveDialogDestination (string destination)
        {
            string fallback_name = m_arc_sources != null ? m_arc_source : m_arc_name;
            return m_main.GetDefaultExtractDestination (m_arc_source, fallback_name, destination);
        }

        private void ExtractFilesFromArchive (string text, IEnumerable<Entry> file_list)
        {
            var files = GetFilesToExtract (file_list);
            if (!files.Any())
            {
                m_main.SetStatusText (string.Format ("{1}: {0}", guiStrings.MsgNoFiles, m_arc_name));
                return;
            }
            m_progress_dialog = new ProgressDialog ()
            {
                WindowTitle = guiStrings.TextTitle,
                Text        = text,
                Description = "",
                MinimizeBox = true,
            };
            if (1 == files.Count)
            {
                m_progress_dialog.Description = files.First().Name;
                m_progress_dialog.ProgressBarStyle = ProgressBarStyle.MarqueeProgressBar;
            }
            m_convert_audio = !m_skip_audio && Settings.Default.appConvertAudio;
            m_progress_dialog.DoWork += (s, e) => ExtractWorker (files);
            m_progress_dialog.RunWorkerCompleted += OnExtractComplete;
            m_main.IsEnabled = false;
            m_progress_dialog.ShowDialog (m_main);
            m_extract_in_progress = true;
        }

        private void ExtractArchivesFromList (string text)
        {
            m_progress_dialog = new ProgressDialog ()
            {
                WindowTitle = guiStrings.TextTitle,
                Text        = text,
                Description = "",
                MinimizeBox = true,
            };
            m_convert_audio = !m_skip_audio && Settings.Default.appConvertAudio;
            m_progress_dialog.DoWork += (s, e) => ExtractArchivesWorker();
            m_progress_dialog.RunWorkerCompleted += OnExtractComplete;
            m_main.IsEnabled = false;
            m_progress_dialog.ShowDialog (m_main);
            m_extract_in_progress = true;
        }

        private List<Entry> GetFilesToExtract (IEnumerable<Entry> file_list)
        {
            file_list = file_list.Where (e => e.Offset >= 0);
            var files = file_list.ToList();
            if (files.Count > 1 && (m_skip_images || m_skip_script || m_skip_audio))
            {
                files = files.Where (f => !(m_skip_images && f.Type == "image") &&
                                          !(m_skip_script && f.Type == "script") &&
                                          !(m_skip_audio  && f.Type == "audio")).ToList();
            }
            return files.OrderBy (e => e.Offset).ToList();
        }

        void ExtractWorker (IList<Entry> file_list)
        {
            m_extract_count = 0;
            m_skip_count = 0;
            var arc = m_fs.Source;
            bool ignore_errors = false;
            ExtractEntries (arc, file_list, m_arc_name, 0, 1, ref ignore_errors);
        }

        void ExtractArchivesWorker ()
        {
            m_extract_count = 0;
            m_skip_count = 0;
            bool ignore_errors = false;
            int total = m_arc_sources.Count;
            for (int i = 0; i < total; ++i)
            {
                if (m_progress_dialog.CancellationPending)
                    break;
                string source = m_arc_sources[i];
                string archive_name = Path.GetFileName (source);
                ArchiveFileSystem fs = null;
                try
                {
                    m_progress_dialog.ReportProgress (i*100/total,
                        string.Format (guiStrings.MsgExtractingArchive, archive_name), "");
                    fs = OpenArchiveFileSystem (source);
                    Directory.SetCurrentDirectory (m_destination);
                    var file_list = GetFilesToExtract (fs.GetFilesRecursive());
                    if (!file_list.Any())
                    {
                        ++m_skip_count;
                        continue;
                    }
                    if (!ExtractEntries (fs.Source, file_list, archive_name, i, total, ref ignore_errors))
                        break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception X)
                {
                    if (!HandleExtractError (archive_name, X, ref ignore_errors))
                        break;
                    ++m_skip_count;
                }
                finally
                {
                    if (null != fs && object.ReferenceEquals (VFS.Top, fs))
                        VFS.ChDir ("..");
                }
            }
        }

        ArchiveFileSystem OpenArchiveFileSystem (string source)
        {
            string source_dir = Path.GetDirectoryName (source);
            if (!string.IsNullOrEmpty (source_dir))
                Directory.SetCurrentDirectory (source_dir);
            VFS.ChDir (source);
            var fs = VFS.Top as ArchiveFileSystem;
            if (null == fs)
                throw new UnknownFormatException();
            return fs;
        }

        bool ExtractEntries (ArcFile arc, IList<Entry> file_list, string archive_name, int archive_index,
                             int archive_count, ref bool ignore_errors)
        {
            int total = file_list.Count;
            int progress_count = 0;
            foreach (var entry in file_list)
            {
                if (m_progress_dialog.CancellationPending)
                    return false;
                if (archive_count > 1)
                {
                    int progress = (archive_index*100 + progress_count*100/total) / archive_count;
                    m_progress_dialog.ReportProgress (progress,
                        string.Format (guiStrings.MsgExtractingArchive, archive_name), entry.Name);
                    ++progress_count;
                }
                else if (total > 1)
                    m_progress_dialog.ReportProgress (progress_count++*100/total, null, entry.Name);
                if (!ExtractEntry (arc, entry, ref ignore_errors))
                    return false;
            }
            return true;
        }

        bool ExtractEntry (ArcFile arc, Entry entry, ref bool ignore_errors)
        {
            try
            {
                if (null != m_image_format && entry.Type == "image")
                    ExtractImage (arc, entry, m_image_format);
                else if (m_convert_audio && entry.Type == "audio")
                    ExtractAudio (arc, entry);
                else if (entry.Type == "script")
                    ExtractScript (arc, entry);
                else
                    ExtractEntryAsIs (arc, entry);
                ++m_extract_count;
            }
            catch (SkipExistingFileException)
            {
                ++m_skip_count;
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception X)
            {
                if (!HandleExtractError (entry.Name, X, ref ignore_errors))
                    return false;
                ++m_skip_count;
            }
            return true;
        }

        bool HandleExtractError (string name, Exception X, ref bool ignore_errors)
        {
            if (!ignore_errors)
            {
                var error_text = string.Format (guiStrings.TextErrorExtracting, name, X.Message);
                var result = ShowErrorDialog (error_text);
                if (!result.Continue)
                    return false;
                ignore_errors = result.IgnoreErrors;
            }
            return true;
        }

        void ExtractEntryAsIs (ArcFile arc, Entry entry)
        {
            using (var input = arc.OpenEntry (entry))
            using (var output = CreateNewFile (entry.Name, true))
                input.CopyTo (output);
        }

        void ExtractScript (ArcFile arc, Entry entry)
        {
            using (var input = arc.OpenBinaryEntry (entry))
            {
                var script_format = ScriptFormat.FindFormat (input);
                if (!ShouldConvertScript (script_format))
                {
                    input.Position = 0;
                    using (var output = CreateNewFile (entry.Name, true))
                        input.AsStream.CopyTo (output);
                    return;
                }

                input.Position = 0;
                var configurable = script_format as IConfigurableScriptFormat;
                if (null == configurable)
                {
                    var output_name = Path.ChangeExtension (entry.Name, "txt");
                    using (var script = script_format.ConvertFrom (input))
                    using (var output = CreateNewFile (output_name, true))
                        script.CopyTo (output);
                    return;
                }

                if (ExtractDialog.ScriptTextBoth == m_script_text_output_mode)
                {
                    ExtractScriptText (input, configurable, entry, ScriptTextMode.Filtered, true);
                    input.Position = 0;
                    ExtractScriptText (input, configurable, entry, ScriptTextMode.Raw, true);
                }
                else
                {
                    var mode = ResolveScriptTextMode (configurable, m_script_text_output_mode);
                    ExtractScriptText (input, configurable, entry, mode, mode != ScriptTextMode.Filtered);
                }
            }
        }

        void ExtractScriptText (IBinaryStream input, IConfigurableScriptFormat format, Entry entry, string mode, bool use_suffix)
        {
            var output_name = GetScriptTextOutputName (entry.Name, mode, use_suffix);
            using (var script = format.ConvertFrom (input, mode))
            using (var output = CreateNewFile (output_name, true))
                script.CopyTo (output);
        }

        static string NormalizeScriptTextMode (string mode)
        {
            if (string.Equals (mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase))
                return ScriptTextMode.Raw;
            if (string.Equals (mode, ScriptTextMode.Dump, StringComparison.OrdinalIgnoreCase))
                return ScriptTextMode.Dump;
            if (string.Equals (mode, ScriptTextMode.JsonLines, StringComparison.OrdinalIgnoreCase))
                return ScriptTextMode.JsonLines;
            return ScriptTextMode.Filtered;
        }

        static string ResolveScriptTextMode (IConfigurableScriptFormat format, string mode)
        {
            mode = NormalizeScriptTextMode (mode);
            if (format.TextModes.Any (m => string.Equals (m, mode, StringComparison.OrdinalIgnoreCase)))
                return mode;
            if (format.TextModes.Any (m => string.Equals (m, format.DefaultTextMode, StringComparison.OrdinalIgnoreCase)))
                return format.DefaultTextMode;
            return ScriptTextMode.Filtered;
        }

        static string GetScriptTextOutputName (string name, string mode, bool use_suffix)
        {
            if (!use_suffix)
                return Path.ChangeExtension (name, "txt");
            string ext = ScriptTextMode.Raw == mode ? "raw.txt"
                : ScriptTextMode.Dump == mode ? "dump.txt"
                : ScriptTextMode.JsonLines == mode ? "jsonl" : "filtered.txt";
            return Path.ChangeExtension (name, ext);
        }

        static bool ShouldConvertScript (ScriptFormat format)
        {
            return null != format
                && ("PS3/CMVS" == format.Tag || "SPT/SystemNNN" == format.Tag
                    || "MJO/Majiro" == format.Tag || "KiriKiri/Script" == format.Tag
                    || "BGI/Script" == format.Tag || "TXT/Whale" == format.Tag);
        }

        void ExtractImage (ArcFile arc, Entry entry, ImageFormat target_format)
        {
            using (var decoder = arc.OpenImage (entry))
            {
                var src_format = decoder.SourceFormat; // could be null
                string target_ext = target_format.Extensions.FirstOrDefault() ?? "";
                string outname = Path.ChangeExtension (entry.Name, target_ext);
                if (src_format == target_format)
                {
                    // source format is the same as a target, copy file as is
                    using (var output = CreateNewFile (outname, true))
                        decoder.Source.CopyTo (output);
                    return;
                }
                ImageData image = decoder.Image;
                if (m_adjust_image_offset)
                {
                    image = AdjustImageOffset (image);
                }
                using (var outfile = CreateNewFile (outname, true))
                {
                    target_format.Write (outfile, image);
                }
            }
        }

        static ImageData AdjustImageOffset (ImageData image)
        {
            if (0 == image.OffsetX && 0 == image.OffsetY)
                return image;
            int width = (int)image.Width + image.OffsetX;
            int height = (int)image.Height + image.OffsetY;
            if (width <= 0 || height <= 0)
                return image;

            int x = Math.Max (image.OffsetX, 0);
            int y = Math.Max (image.OffsetY, 0);
            int src_x = image.OffsetX < 0 ? Math.Abs (image.OffsetX) : 0;
            int src_y = image.OffsetY < 0 ? Math.Abs (image.OffsetY) : 0;
            int src_stride = (int)image.Width * (image.BPP+7) / 8;
            int dst_stride = width * (image.BPP+7) / 8;
            var pixels = new byte[height*dst_stride];
            int offset = y * dst_stride + x * image.BPP / 8;
            Int32Rect rect = new Int32Rect (src_x, src_y, (int)image.Width - src_x, 1);
            for (int row = src_y; row < image.Height; ++row)
            {
                rect.Y = row;
                image.Bitmap.CopyPixels (rect, pixels, src_stride, offset);
                offset += dst_stride;
            }
            var bitmap = BitmapSource.Create (width, height, image.Bitmap.DpiX, image.Bitmap.DpiY,
                image.Bitmap.Format, image.Bitmap.Palette, pixels, dst_stride);
            return new ImageData (bitmap);
        }

        void ExtractAudio (ArcFile arc, Entry entry)
        {
            using (var file = arc.OpenBinaryEntry (entry))
            using (var sound = AudioFormat.Read (file))
            {
                if (null == sound)
                    throw new InvalidFormatException (guiStrings.MsgUnableInterpretAudio);
                ConvertAudio (entry.Name, sound);
            }
        }

        public void ConvertAudio (string filename, SoundInput input)
        {
            string source_format = input.SourceFormat;
            if (GarConvertMedia.CommonAudioFormats.Contains (source_format))
            {
                var output_name = Path.ChangeExtension (filename, source_format);
                using (var output = CreateNewFile (output_name, true))
                {
                    input.Source.Position = 0;
                    input.Source.CopyTo (output);
                }
            }
            else
            {
                var output_name = Path.ChangeExtension (filename, "wav");
                using (var output = CreateNewFile (output_name, true))
                    AudioFormat.Wav.Write (input, output);
            }
        }

        void OnExtractComplete (object sender, RunWorkerCompletedEventArgs e)
        {
            m_main.IsEnabled = true;
            m_extract_in_progress = false;
            m_progress_dialog.Dispose();
            m_main.Activate();
            m_main.ListViewFocus();
            if (!m_main.ViewModel.IsArchive)
            {
                m_main.Dispatcher.Invoke (m_main.RefreshView);
            }
            m_main.SetStatusText (Localization.Format ("MsgExtractedFiles", m_extract_count));
            this.Dispose();
        }
        
        #region IDisposable Members
        bool disposed = false;

        public void Dispose ()
        {
            if (!disposed)
            {
                if (m_should_ascend)
                {
                    VFS.ChDir ("..");
                }
                disposed = true;
            }
            GC.SuppressFinalize (this);
        }
        #endregion
    }
}
