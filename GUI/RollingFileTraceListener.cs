//! \file       RollingFileTraceListener.cs
//! \date       Sun Jun 07 2026
//! \brief      Rolling file trace listener.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GARbro.GUI
{
    internal sealed class RollingFileTraceListener : TraceListener
    {
        readonly object     m_sync = new object();
        readonly string     m_directory;
        readonly string     m_prefix;
        readonly long       m_max_file_size;
        readonly int        m_max_file_count;

        StreamWriter        m_writer;
        string              m_current_date = "";

        public RollingFileTraceListener (string directory, string prefix, long max_file_size, int max_file_count)
        {
            m_directory = directory;
            m_prefix = prefix;
            m_max_file_size = Math.Max (1, max_file_size);
            m_max_file_count = Math.Max (1, max_file_count);
        }

        public override void Write (string message)
        {
            lock (m_sync)
            {
                try
                {
                    EnsureWriter();
                    m_writer.Write (message);
                    if (Trace.AutoFlush)
                        m_writer.Flush();
                }
                catch
                {
                }
            }
        }

        public override void Write (string message, string category)
        {
            Write (FormatMessage (message, category));
        }

        public override void WriteLine (string message)
        {
            WriteLine (message, null);
        }

        public override void WriteLine (string message, string category)
        {
            lock (m_sync)
            {
                try
                {
                    EnsureWriter();
                    m_writer.WriteLine (FormatMessage (message, category));
                    if (Trace.AutoFlush)
                        m_writer.Flush();
                    if (m_writer.BaseStream.Length >= m_max_file_size)
                        CloseWriter();
                }
                catch
                {
                }
            }
        }

        public override void Flush ()
        {
            lock (m_sync)
            {
                if (null != m_writer)
                    m_writer.Flush();
            }
        }

        public override void Close ()
        {
            lock (m_sync)
            {
                CloseWriter();
            }
            base.Close();
        }

        protected override void Dispose (bool disposing)
        {
            if (disposing)
            {
                lock (m_sync)
                {
                    CloseWriter();
                }
            }
            base.Dispose (disposing);
        }

        void EnsureWriter ()
        {
            string date = DateTime.Now.ToString ("yyyyMMdd", CultureInfo.InvariantCulture);
            if (null != m_writer && date == m_current_date && m_writer.BaseStream.Length < m_max_file_size)
                return;

            CloseWriter();
            Directory.CreateDirectory (m_directory);
            m_current_date = date;
            string path = GetCurrentLogPath (date);
            var stream = new FileStream (path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            m_writer = new StreamWriter (stream, new UTF8Encoding (false));
            DeleteOldLogs();
        }

        string GetCurrentLogPath (string date)
        {
            for (int index = 0; ; ++index)
            {
                string path = GetLogPath (date, index);
                if (!File.Exists (path))
                    return path;
                try
                {
                    if (new FileInfo (path).Length < m_max_file_size)
                        return path;
                }
                catch
                {
                    return path;
                }
            }
        }

        string GetLogPath (string date, int index)
        {
            string suffix = 0 == index ? "" : "-" + index.ToString (CultureInfo.InvariantCulture);
            return Path.Combine (m_directory, string.Format ("{0}-{1}{2}.log", m_prefix, date, suffix));
        }

        string FormatMessage (string message, string category)
        {
            var builder = new StringBuilder();
            builder.Append (DateTime.Now.ToString ("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty (category))
            {
                builder.Append (' ');
                if (category.StartsWith ("[", StringComparison.Ordinal))
                    builder.Append (category);
                else
                    builder.AppendFormat (CultureInfo.InvariantCulture, "[{0}]", category);
            }
            builder.Append (' ');
            builder.Append (message);
            return builder.ToString();
        }

        void DeleteOldLogs ()
        {
            try
            {
                var logs = Directory.EnumerateFiles (m_directory, m_prefix + "-*.log")
                    .Select (f => new FileInfo (f))
                    .OrderByDescending (f => f.LastWriteTimeUtc)
                    .Skip (m_max_file_count)
                    .ToList();
                foreach (var file in logs)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        void CloseWriter ()
        {
            if (null == m_writer)
                return;
            try
            {
                m_writer.Flush();
                m_writer.Dispose();
            }
            catch
            {
            }
            m_writer = null;
        }
    }
}
