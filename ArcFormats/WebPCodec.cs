//! \file       WebPCodec.cs
//! \date       Sun Apr 12 2026
//! \brief      Google WebP image format.
//
// Copyright (C) 2016 by morkt
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
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameRes.Formats
{
    public class WebPCodec
    {
        [DllImport("libwebp.dll", EntryPoint = "WebPGetInfo", CallingConvention = CallingConvention.Cdecl)]
        public static extern int WebPGetInfo ([MarshalAs(UnmanagedType.LPArray)] byte[] data, UIntPtr data_size, ref int width, ref int height);

        [DllImport("libwebp.dll", EntryPoint = "WebPDecodeBGRAInto", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr WebPDecodeBGRAInto ([MarshalAs(UnmanagedType.LPArray)] byte[] data, UIntPtr data_size, IntPtr output_buffer, UIntPtr output_buffer_size, int output_stride);

        [DllImport("libwebp.dll", EntryPoint = "WebPEncodeBGRA", CallingConvention = CallingConvention.Cdecl)]
        static extern UIntPtr WebPEncodeBGRA ([MarshalAs(UnmanagedType.LPArray)] byte[] bgra, int width, int height, int stride, float quality_factor, out IntPtr output);

        [DllImport("libwebp.dll", EntryPoint = "WebPEncodeLosslessBGRA", CallingConvention = CallingConvention.Cdecl)]
        static extern UIntPtr WebPEncodeLosslessBGRA ([MarshalAs(UnmanagedType.LPArray)] byte[] bgra, int width, int height, int stride, out IntPtr output);

        [DllImport("libwebp.dll", EntryPoint = "WebPFree", CallingConvention = CallingConvention.Cdecl)]
        static extern void WebPFree (IntPtr pointer);

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
        static extern IntPtr LoadLibraryEx (string lpFileName, IntPtr hReservedNull, uint dwFlags);

        static bool loaded = false;

        const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;
        const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        public static void Load ()
        {
            if (loaded)
                return;
            var dir = Path.GetDirectoryName (Assembly.GetExecutingAssembly ().Location);
            dir = Path.Combine (dir, (IntPtr.Size == 4) ? "x86" : "x64");
            var path = Path.Combine (dir, "libwebp.dll");
            path = Path.GetFullPath (path);
            var lib = LoadLibraryEx (path, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (IntPtr.Zero == lib)
                throw new Win32Exception (Marshal.GetLastWin32Error ());
            loaded = true;
        }

        public static void Encode (Stream file, ImageData image, bool lossless)
        {
            if (null == file)
                throw new ArgumentNullException ("file");
            if (null == image || null == image.Bitmap)
                throw new ArgumentNullException ("image");

            BitmapSource bitmap = image.Bitmap;
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap (bitmap, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                bitmap = converted;
            }

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            if (width <= 0 || height <= 0)
                throw new InvalidFormatException ("Invalid WebP image dimensions.");
            int stride = checked (width * 4);
            var pixels = new byte[checked (stride * height)];
            bitmap.CopyPixels (pixels, stride, 0);

            Load();
            IntPtr output;
            UIntPtr encoded_size = lossless
                ? WebPEncodeLosslessBGRA (pixels, width, height, stride, out output)
                : WebPEncodeBGRA (pixels, width, height, stride, 80.0f, out output);
            ulong size = encoded_size.ToUInt64();
            if (0 == size || IntPtr.Zero == output || size > int.MaxValue)
            {
                if (IntPtr.Zero != output)
                    WebPFree (output);
                throw new InvalidOperationException ("WebP image encoder failed.");
            }

            try
            {
                int remaining = (int)size;
                int offset = 0;
                var buffer = new byte[Math.Min (0x10000, remaining)];
                while (remaining > 0)
                {
                    int count = Math.Min (buffer.Length, remaining);
                    Marshal.Copy (IntPtr.Add (output, offset), buffer, 0, count);
                    file.Write (buffer, 0, count);
                    offset += count;
                    remaining -= count;
                }
            }
            finally
            {
                WebPFree (output);
            }
        }
    }

    public abstract class WebPEncoderFormat : ImageFormat
    {
        readonly bool m_lossless;

        protected WebPEncoderFormat (bool lossless)
        {
            m_lossless = lossless;
            Extensions = new[] { "webp" };
        }

        public override uint     Signature { get { return 0; } }
        public override bool      CanWrite { get { return true; } }

        public override ImageMetaData ReadMetaData (IBinaryStream file)
        {
            return null;
        }

        public override ImageData Read (IBinaryStream file, ImageMetaData info)
        {
            throw new NotSupportedException ("WebP encoder formats are write-only.");
        }

        public override void Write (Stream file, ImageData image)
        {
            WebPCodec.Encode (file, image, m_lossless);
        }
    }

    [Export(typeof(ImageFormat))]
    [ExportMetadata("Priority", 90)]
    public sealed class WebPQuality80Format : WebPEncoderFormat
    {
        public const string FormatTag = "WEBP/80";

        public override string         Tag { get { return FormatTag; } }
        public override string Description { get { return "Google WebP image format (quality 80%)"; } }

        public WebPQuality80Format () : base (false)
        {
        }
    }

    [Export(typeof(ImageFormat))]
    [ExportMetadata("Priority", 80)]
    public sealed class WebPLosslessFormat : WebPEncoderFormat
    {
        public const string FormatTag = "WEBP/LOSSLESS";

        public override string         Tag { get { return FormatTag; } }
        public override string Description { get { return "Google WebP lossless image format"; } }

        public WebPLosslessFormat () : base (true)
        {
        }
    }
}
