//! \file       ImagePreview.cs
//! \date       Sun Jul 06 06:34:56 2014
//! \brief      preview images.
//
// Copyright (C) 2014-2018 by morkt
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
using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GARbro.GUI.Strings;
using GARbro.GUI.Properties;
using GameRes;
using System.Text;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;

namespace GARbro.GUI
{
    public partial class MainWindow : Window
    {
        private readonly BackgroundWorker   m_preview_worker = new BackgroundWorker();
        private PreviewFile                 m_current_preview = new PreviewFile();
        private bool                        m_preview_pending = false;
        private bool                        m_script_text_modes_updating = false;
        private string                      m_preview_script_text_mode = ScriptTextMode.Filtered;

        private UIElement m_active_viewer;
        public UIElement ActiveViewer
        {
            get { return m_active_viewer;  }
            set
            {
                if (value == m_active_viewer)
                    return;
                m_active_viewer = value;
                m_active_viewer.Visibility = Visibility.Visible;
                bool exists = false;
                foreach (UIElement c in PreviewPane.Children)
                {
                    if (c != m_active_viewer)
                        c.Visibility = Visibility.Collapsed;
                    else
                        exists = true;
                }
                if (!exists)
                    PreviewPane.Children.Add (m_active_viewer);
            }
        }

        class PreviewFile
        {
            public IEnumerable<string> Path { get; set; }
            public string Name { get; set; }
            public Entry Entry { get; set; }
            public BitmapSource OriginalBitmap { get; set; }

            public bool IsEqual (IEnumerable<string> path, Entry entry)
            {
                return Path != null && path.SequenceEqual (Path) && Entry == entry;
            }
        }

        private void InitPreviewPane ()
        {
            m_preview_worker.DoWork += (s, e) => LoadPreviewImage (e.Argument as PreviewFile);
            m_preview_worker.RunWorkerCompleted += (s, e) => {
                if (m_preview_pending)
                    RefreshPreviewPane();
            };
            ActiveViewer = ImageView;
            TextView.IsWordWrapEnabled = true;
        }

        private IEnumerable<Encoding> m_encoding_list = GetEncodingList();
        public IEnumerable<Encoding> TextEncodings { get { return m_encoding_list; } }

        internal static IEnumerable<Encoding> GetEncodingList (bool exclude_utf16 = false)
        {
            var list = new HashSet<Encoding>();
            try 
            {
                list.Add(Encoding.Default);
                var oem = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                list.Add(Encoding.GetEncoding(oem));
            } 
            catch (Exception X) 
            {
                if (X is ArgumentException || X is NotSupportedException) 
                    list.Add(Encoding.GetEncoding(20127)); //default to US-ASCII
                else 
                    throw;
            }
            list.Add (Encoding.GetEncoding (932));
            list.Add (Encoding.GetEncoding (936));
            list.Add (Encoding.UTF8);
            if (!exclude_utf16)
            {
                list.Add (Encoding.Unicode);
                list.Add (Encoding.BigEndianUnicode);
            }
            return list;
        }
 
        private void OnEncodingSelect (object sender, SelectionChangedEventArgs e)
        {
            var enc = this.EncodingChoice.SelectedItem as Encoding;
            if (null == enc || null == CurrentTextInput)
                return;
            TextView.CurrentEncoding = enc;
        }

        private void OnScriptTextModeSelect (object sender, SelectionChangedEventArgs e)
        {
            if (m_script_text_modes_updating)
                return;
            var mode = ScriptTextModeChoice.SelectedItem as ScriptTextModeModel;
            if (null == mode || mode.Value == m_preview_script_text_mode)
                return;
            m_preview_script_text_mode = mode.Value;
            if (null != m_current_preview.Entry && "script" == m_current_preview.Entry.Type)
                LoadPreviewText (m_current_preview);
        }

        /// <summary>
        /// Display entry in preview panel
        /// </summary>
        private void PreviewEntry (Entry entry)
        {
            if (m_current_preview.IsEqual (ViewModel.Path, entry))
                return;
            UpdatePreviewPane (entry);
        }

        void RefreshPreviewPane ()
        {
            m_preview_pending = false;
            var current = CurrentDirectory.SelectedItem as EntryViewModel;
            if (null != current)
                UpdatePreviewPane (current.Source);
            else
                ResetPreviewPane();
        }

        void ResetPreviewPane ()
        {
            ActiveViewer = ImageView;
            ImageCanvas.Source = null;
            TextView.Clear();
            ScriptTextModeWidget.Visibility = Visibility.Collapsed;
            CurrentTextInput = null;
        }

        bool IsPreviewPossible (Entry entry)
        {
            return "image" == entry.Type || "script" == entry.Type
                || (string.IsNullOrEmpty (entry.Type) && entry.Size < 0x100000);
        }

        void UpdatePreviewPane (Entry entry)
        {
            SetStatusText ("");
            var vm = ViewModel;
            m_current_preview = new PreviewFile { Path = vm.Path, Name = entry.Name, Entry = entry };
            if (!IsPreviewPossible (entry))
            {
                ResetPreviewPane();
                return;
            }
            if ("image" != entry.Type)
                LoadPreviewText (m_current_preview);
            else if (!m_preview_worker.IsBusy)
                m_preview_worker.RunWorkerAsync (m_current_preview);
            else
                m_preview_pending = true;
        }

        private Stream m_current_text;
        private Stream CurrentTextInput
        {
            get { return m_current_text; }
            set
            {
                if (value == m_current_text)
                    return;
                if (null != m_current_text)
                    m_current_text.Dispose();
                m_current_text = value;
            }
        }

        void LoadPreviewText (PreviewFile preview)
        {
            Stream file = null;
            try
            {
                file = OpenPreviewText (preview.Entry);
                if (!TextView.IsTextFile (file))
                {
                    ResetPreviewPane();
                    return;
                }
                var enc = EncodingChoice.SelectedItem as Encoding;
                if (null == enc)
                {
                    enc = TextView.GuessEncoding (file);
                    EncodingChoice.SelectedItem = enc;
                }
                TextView.DisplayStream (file, enc);
                ActiveViewer = TextView;
                CurrentTextInput = file;
                file = null;
            }
            catch (Exception X)
            {
                Trace.WriteLine (string.Format ("PreviewText failed. entry='{0}', type='{1}', message='{2}'",
                    preview.Entry.Name, preview.Entry.Type, X.Message), "[Preview]");
                Trace.WriteLine (X.ToString(), "[Preview]");
                ResetPreviewPane();
                SetStatusText (X.Message);
            }
            finally
            {
                if (file != null)
                    file.Dispose();
            }
        }

        Stream OpenPreviewText (Entry entry)
        {
            var input = VFS.OpenBinaryStream (entry);
            if ("script" == entry.Type)
            {
                var script_format = ScriptFormat.FindFormat (input);
                UpdateScriptTextModeWidget (script_format);
                bool convert = ShouldConvertScript (script_format);
                Trace.WriteLine (string.Format ("PreviewText script. entry='{0}', offset={1}, size={2}, format='{3}', mode='{4}', convert={5}",
                    entry.Name, entry.Offset, entry.Size, null != script_format ? script_format.Tag : "<none>",
                    m_preview_script_text_mode, convert), "[Preview]");
                if (convert)
                {
                    input.Position = 0;
                    try
                    {
                        var configurable = script_format as IConfigurableScriptFormat;
                        if (null != configurable)
                        {
                            Trace.WriteLine (string.Format ("PreviewText convert configurable. entry='{0}', format='{1}', mode='{2}'",
                                entry.Name, script_format.Tag, m_preview_script_text_mode), "[Preview]");
                            return configurable.ConvertFrom (input, m_preview_script_text_mode);
                        }
                        Trace.WriteLine (string.Format ("PreviewText convert. entry='{0}', format='{1}'",
                            entry.Name, script_format.Tag), "[Preview]");
                        return script_format.ConvertFrom (input);
                    }
                    finally
                    {
                        input.Dispose();
                    }
                }
            }
            else
            {
                ScriptTextModeWidget.Visibility = Visibility.Collapsed;
            }
            input.Position = 0;
            return input.AsStream;
        }

        void UpdateScriptTextModeWidget (ScriptFormat format)
        {
            var configurable = format as IConfigurableScriptFormat;
            if (null == configurable)
            {
                ScriptTextModeWidget.Visibility = Visibility.Collapsed;
                return;
            }
            var modes = configurable.TextModes.Select (m => new ScriptTextModeModel (m, GetScriptTextModeLabel (m))).ToList();
            if (!modes.Any())
            {
                ScriptTextModeWidget.Visibility = Visibility.Collapsed;
                return;
            }
            var selected = modes.FirstOrDefault (m => m.Value == m_preview_script_text_mode)
                ?? modes.FirstOrDefault (m => m.Value == configurable.DefaultTextMode)
                ?? modes.First();
            m_script_text_modes_updating = true;
            try
            {
                ScriptTextModeChoice.ItemsSource = modes;
                ScriptTextModeChoice.DisplayMemberPath = "Label";
                ScriptTextModeChoice.SelectedItem = selected;
                m_preview_script_text_mode = selected.Value;
                ScriptTextModeWidget.Visibility = Visibility.Visible;
            }
            finally
            {
                m_script_text_modes_updating = false;
            }
        }

        static string GetScriptTextModeLabel (string mode)
        {
            if (string.Equals (mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase))
                return "Raw";
            if (string.Equals (mode, ScriptTextMode.Dump, StringComparison.OrdinalIgnoreCase))
                return "Dump";
            if (string.Equals (mode, ScriptTextMode.JsonLines, StringComparison.OrdinalIgnoreCase))
                return "JSONL";
            return "Filtered";
        }

        static bool ShouldConvertScript (ScriptFormat format)
        {
            return null != format
                && ("PS3/CMVS" == format.Tag || "SPT/SystemNNN" == format.Tag
                    || "MJO/Majiro" == format.Tag || "KiriKiri/Script" == format.Tag
                    || "BGI/Script" == format.Tag || "TXT/Whale" == format.Tag
                    || "SRC/SOFTPAL" == format.Tag);
        }

        void LoadPreviewImage (PreviewFile preview)
        {
            try
            {
                using (var data = VFS.OpenImage (preview.Entry))
                {
                    SetPreviewImage (preview, data.Image.Bitmap, data.SourceFormat);
                }
            }
            catch (Exception X)
            {
                Dispatcher.Invoke (ResetPreviewPane);
                SetStatusText (X.Message);
            }
        }

        void SetPreviewImage (PreviewFile preview, BitmapSource bitmap, ImageFormat format)
        {
            // Save the original bitmap before any processing
            var originalBitmap = bitmap;
            if (!originalBitmap.IsFrozen)
            {
                originalBitmap = originalBitmap.Clone();
                originalBitmap.Freeze();
            }
            
            if (bitmap.DpiX != Desktop.DpiX || bitmap.DpiY != Desktop.DpiY)
            {
                int stride = bitmap.PixelWidth * ((bitmap.Format.BitsPerPixel + 7) / 8); 
                var pixels = new byte[stride*bitmap.PixelHeight];
                bitmap.CopyPixels (pixels, stride, 0);
                var fixed_bitmap = BitmapSource.Create (bitmap.PixelWidth, bitmap.PixelHeight,
                    Desktop.DpiX, Desktop.DpiY, bitmap.Format, bitmap.Palette, pixels, stride);
                bitmap = fixed_bitmap;
            }
            if (!bitmap.IsFrozen)
                bitmap.Freeze();
            Dispatcher.Invoke (() =>
            {
                if (m_current_preview == preview) // compare by reference
                {
                    ActiveViewer = ImageView;
                    ImageCanvas.Source = bitmap;
                    preview.OriginalBitmap = originalBitmap; // Store original for copying
                    ApplyDownScaleSetting();
                    SetStatusText (string.Format (guiStrings.MsgImageSize, bitmap.PixelWidth,
                                                  bitmap.PixelHeight, bitmap.Format.BitsPerPixel, format?.Tag ?? "?"));
                }
            });
        }

        /// <summary>
        /// Fit window size to image.
        /// </summary>
        private void FitWindowExec (object sender, ExecutedRoutedEventArgs e)
        {
            var image = ImageCanvas.Source;
            if (null == image)
                return;
            var width = image.Width + Settings.Default.lvPanelWidth.Value + 1;
            var height = image.Height;
            width = Math.Max (ContentGrid.ActualWidth, width);
            height = Math.Max (ContentGrid.ActualHeight, height);
            if (width > ContentGrid.ActualWidth || height > ContentGrid.ActualHeight)
            {
                ContentGrid.Width = width;
                ContentGrid.Height = height;
                this.SizeToContent = SizeToContent.WidthAndHeight;
                Dispatcher.InvokeAsync (() => {
                    this.SizeToContent = SizeToContent.Manual;
                    ContentGrid.Width = double.NaN;
                    ContentGrid.Height = double.NaN;
                }, DispatcherPriority.ContextIdle);
            }
        }

        private void SetImageScaleMode (bool scale)
        {
            if (scale)
            {
                ImageCanvas.Stretch = Stretch.Uniform;
                RenderOptions.SetBitmapScalingMode (ImageCanvas, BitmapScalingMode.HighQuality);
                ImageView.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ImageView.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            else
            {
                ImageCanvas.Stretch = Stretch.None;
                RenderOptions.SetBitmapScalingMode (ImageCanvas, BitmapScalingMode.NearestNeighbor);
                ImageView.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                ImageView.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
        }

        private void ApplyDownScaleSetting ()
        {
            bool image_need_scale = DownScaleImage.Get<bool>();
            if (image_need_scale && ImageCanvas.Source != null)
            {
                var image = ImageCanvas.Source;
                image_need_scale = image.Width > ImageView.ActualWidth || image.Height > ImageView.ActualHeight;
            }
            SetImageScaleMode (image_need_scale);
        }

        private void PreviewSizeChanged (object sender, SizeChangedEventArgs e)
        {
            var image = ImageCanvas.Source;
            if (null == image || !DownScaleImage.Get<bool>())
                return;
            SetImageScaleMode (image.Width > e.NewSize.Width || image.Height > e.NewSize.Height);
        }

        /// <summary>
        /// Copy image to clipboard (like browser's "Copy Image")
        /// </summary>
        private void CopyImageExec (object sender, ExecutedRoutedEventArgs e)
        {
            try
            {
                // Use the original bitmap stored in preview to avoid DPI conversion artifacts
                var bitmap = m_current_preview.OriginalBitmap as BitmapSource;
                if (null == bitmap)
                    return;

                // Ensure bitmap is frozen and accessible from any thread
                if (!bitmap.IsFrozen)
                {
                    bitmap = bitmap.Clone();
                    bitmap.Freeze();
                }

                // Copy pixel data to avoid threading issues with BitmapFrame.Create
                int stride = bitmap.PixelWidth * ((bitmap.Format.BitsPerPixel + 7) / 8);
                byte[] pixels = new byte[stride * bitmap.PixelHeight];
                bitmap.CopyPixels(pixels, stride, 0);

                Dispatcher.Invoke (() => 
                {
                    // Recreate bitmap from pixel data in UI thread
                    var newBitmap = BitmapSource.Create(
                        bitmap.PixelWidth, 
                        bitmap.PixelHeight,
                        bitmap.DpiX, 
                        bitmap.DpiY, 
                        bitmap.Format, 
                        bitmap.Palette, 
                        pixels, 
                        stride);
                    newBitmap.Freeze();

                    // Copy the image to clipboard preserving alpha channel
                    Clipboard.Clear();
                    
                    // Create PNG byte array to preserve transparency
                    byte[] pngData;
                    using (var stream = new MemoryStream())
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(newBitmap));
                        encoder.Save(stream);
                        pngData = stream.ToArray();
                    }
                    
                    // Use DataObject to set multiple formats
                    var dataObject = new DataObject();
                    
                    // Set PNG data (preserves transparency)
                    dataObject.SetData("PNG", new MemoryStream(pngData), false);
                    
                    // Create a new frozen bitmap for format conversion
                    var convertedBitmap = new FormatConvertedBitmap(newBitmap, PixelFormats.Bgra32, null, 0);
                    convertedBitmap.Freeze();
                    dataObject.SetImage(convertedBitmap);
                    
                    Clipboard.SetDataObject(dataObject, true);
                });

            }
            catch (Exception X)
            {
                SetStatusText ("CopyImage Failed: " + X.Message);
            }
        }

        private void CanExecuteCopyImage (object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = ImageCanvas.Source != null && m_current_preview.Entry != null;
        }
    }
}
