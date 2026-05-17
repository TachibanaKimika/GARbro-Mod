//! \file       TextViewer.cs
//! \date       Mon May 11 23:24:33 2015
//! \brief      Text file viewer widget.
//
// Copyright (C) 2015 by morkt
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
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JustView
{
    /// <summary>
    /// Interaction logic for TextViewer.xaml
    /// </summary>
    public partial class TextViewer : Border
    {
        const int VirtualLineThreshold = 4096;
        const int VirtualCharThreshold = 0x100000;

        Lazy<double>        m_default_width;
        bool                m_using_virtual_view;

        public static readonly DependencyProperty VirtualTextWrappingProperty =
            DependencyProperty.Register ("VirtualTextWrapping", typeof(TextWrapping), typeof(TextViewer),
                                         new PropertyMetadata (TextWrapping.NoWrap));

        public TextViewer ()
        {
            m_default_width = new Lazy<double> (() => GetFixedWidth (80));
            InitializeComponent();
            DefaultZoom = 100;
        }

        public void Clear ()
        {
            TextBoxView.Clear();
            VirtualTextView.ItemsSource = null;
            VirtualTextView.Visibility = Visibility.Collapsed;
            TextBoxView.Visibility = Visibility.Visible;
            m_using_virtual_view = false;
            Input = null;
        }

        public ScrollViewer ScrollViewer
        {
            get
            {
                return FindVisualChild<ScrollViewer> (m_using_virtual_view
                    ? (DependencyObject)VirtualTextView : TextBoxView);
            }
        }

        public double       DefaultWidth { get { return m_default_width.Value; } }
        public double        DefaultZoom { get; private set; }
        public Stream              Input { get; set; }
        public double       MaxLineWidth { get; set; }

        public TextWrapping VirtualTextWrapping
        {
            get { return (TextWrapping)GetValue (VirtualTextWrappingProperty); }
            set { SetValue (VirtualTextWrappingProperty, value); }
        }

        private bool m_word_wrap;
        public bool IsWordWrapEnabled
        {
            get { return m_word_wrap; }
            set
            {
                m_word_wrap = value;
                ApplyWordWrap (value);
            }
        }

        private Encoding m_current_encoding;
        public Encoding CurrentEncoding
        {
            get { return m_current_encoding; }
            set
            {
                if (m_current_encoding != value)
                {
                    m_current_encoding = value;
                    Refresh();
                }
            }
        }

        public void DisplayStream (Stream file, Encoding enc)
        {
            if (file.Length > 0xffffff)
                throw new ApplicationException ("File is too long");
            ReadStream (file, enc);
            ScrollToHome();
            Input = file;
            m_current_encoding = enc;
        }

        public void Refresh ()
        {
            if (Input != null)
            {
                Input.Position = 0;
                ReadStream (Input, CurrentEncoding);
            }
        }

        byte[] m_test_buf = new byte[0x400];

        public Encoding GuessEncoding (Stream file)
        {
            var enc = Encoding.Default;
            if (3 == file.Read (m_test_buf, 0, 3))
            {
                if (IsUTF8())
                    enc = Encoding.UTF8;
                else if (IsUTF16BE())
                    enc = Encoding.BigEndianUnicode;
                else if (IsUTF16LE())
                    enc = Encoding.Unicode;
            }
            file.Position = 0;
            return enc;
        }

        private bool IsUTF8 ()
        {
            return 0xEF == m_test_buf[0] && 0xBB == m_test_buf[1] && 0xBF == m_test_buf[2];
        }

        private bool IsUTF16BE ()
        {
            return 0xFE == m_test_buf[0] && 0xFF == m_test_buf[1];
        }

        private bool IsUTF16LE ()
        {
            return 0xFF == m_test_buf[0] && 0xFE == m_test_buf[1];
        }

        public bool IsTextFile (Stream file)
        {
            int read = file.Read (m_test_buf, 0, m_test_buf.Length);
            file.Position = 0;
            if (read > 3 && (IsUTF8() || IsUTF16LE() || IsUTF16BE()))
                return true;
            bool found_eol = false;
            for (int i = 0; i < read; ++i)
            {
                byte c = m_test_buf[i];
                if (c < 9 || (c > 0x0d && c < 0x1a) || (c > 0x1b && c < 0x20))
                    return false;
                found_eol = found_eol || 0x0A == c;
            }
            return found_eol || read < 80;
        }

        double GetFixedWidth (int char_width)
        {
            var block = new TextBlock();
            block.FontFamily = TextBoxView.FontFamily;
            block.FontSize = TextBoxView.FontSize;
            block.Padding = TextBoxView.Padding;
            block.Text = new string ('M', char_width);
            block.Measure (new Size (double.PositiveInfinity, double.PositiveInfinity));
            return block.DesiredSize.Width;
        }

        void ReadStream (Stream file, Encoding enc)
        {
            using (var reader = new StreamReader (file, enc, false, 0x400, true))
            {
                string text = reader.ReadToEnd();
                if (ShouldUseVirtualView (text))
                    DisplayVirtualText (text);
                else
                    DisplayTextBox (text);
            }
        }

        bool ShouldUseVirtualView (string text)
        {
            if (text.Length >= VirtualCharThreshold)
                return true;
            int line_count = 1;
            foreach (var c in text)
            {
                if ('\n' == c && ++line_count >= VirtualLineThreshold)
                    return true;
            }
            return false;
        }

        void DisplayTextBox (string text)
        {
            VirtualTextView.ItemsSource = null;
            VirtualTextView.Visibility = Visibility.Collapsed;
            TextBoxView.Visibility = Visibility.Visible;
            TextBoxView.Text = text;
            m_using_virtual_view = false;
            MaxLineWidth = DefaultWidth;
            ApplyWordWrap (IsWordWrapEnabled);
        }

        void DisplayVirtualText (string text)
        {
            var lines = new List<string>();
            using (var reader = new StringReader (text))
            {
                string line;
                while (null != (line = reader.ReadLine()))
                    lines.Add (line);
            }
            TextBoxView.Clear();
            TextBoxView.Visibility = Visibility.Collapsed;
            VirtualTextView.ItemsSource = lines;
            VirtualTextView.Visibility = Visibility.Visible;
            m_using_virtual_view = true;
            MaxLineWidth = DefaultWidth;
            ApplyWordWrap (IsWordWrapEnabled);
        }

        void ScrollToHome ()
        {
            if (m_using_virtual_view)
            {
                if (VirtualTextView.Items.Count > 0)
                    VirtualTextView.ScrollIntoView (VirtualTextView.Items[0]);
            }
            else
            {
                TextBoxView.CaretIndex = 0;
                TextBoxView.ScrollToHome();
            }
            var scroll = ScrollViewer;
            if (null != scroll)
                scroll.ScrollToHome();
        }

        public void ApplyWordWrap (bool word_wrap)
        {
            var wrapping = word_wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            TextBoxView.TextWrapping = wrapping;
            TextBoxView.HorizontalScrollBarVisibility = word_wrap
                ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            VirtualTextWrapping = wrapping;
            VirtualTextView.SetValue (ScrollViewer.HorizontalScrollBarVisibilityProperty,
                word_wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        }

        static T FindVisualChild<T> (DependencyObject parent) where T : DependencyObject
        {
            if (null == parent)
                return null;
            int count = VisualTreeHelper.GetChildrenCount (parent);
            for (int i = 0; i < count; ++i)
            {
                var child = VisualTreeHelper.GetChild (parent, i);
                var result = child as T ?? FindVisualChild<T> (child);
                if (null != result)
                    return result;
            }
            return null;
        }
    }
}
