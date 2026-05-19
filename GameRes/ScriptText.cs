//! \file       ScriptText.cs
//! \date       Thu Jul 10 11:09:32 2014
//! \brief      Script text resource interface.
//

using System.IO;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Text;

namespace GameRes
{
    public struct ScriptLine
    {
        public uint   Id;
        public string Text;
    }

    public class ScriptData
    {
        public ICollection<ScriptLine> TextLines { get { return m_text; } }

        protected List<ScriptLine> m_text = new List<ScriptLine>();
/*
        public abstract void Serialize (Stream output);
        public abstract void Deserialize (Stream input);
*/
    }

    public class ScriptTextEntry
    {
        public readonly List<string> Names = new List<string>();
        public string Voice;
        public string Message;

        public ScriptTextEntry ()
        {
        }

        public ScriptTextEntry (string message)
        {
            Message = message;
        }

        public ScriptTextEntry (string name, string message)
        {
            if (!string.IsNullOrEmpty (name))
                Names.Add (name);
            Message = message;
        }
    }

    public static class ScriptJsonLines
    {
        public static Stream CreateStream (IEnumerable<ScriptTextEntry> entries, string name)
        {
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (false), 0x400, true))
                Write (writer, entries);
            return new BinMemoryStream (output, name);
        }

        public static void Write (TextWriter writer, IEnumerable<ScriptTextEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (null == entry || string.IsNullOrWhiteSpace (entry.Message))
                    continue;

                writer.Write ('{');
                if (1 == entry.Names.Count)
                {
                    writer.Write ("\"name\":");
                    WriteJsonString (writer, entry.Names[0]);
                    writer.Write (',');
                }
                else if (entry.Names.Count > 1)
                {
                    writer.Write ("\"names\":[");
                    for (int i = 0; i < entry.Names.Count; ++i)
                    {
                        if (i > 0)
                            writer.Write (',');
                        WriteJsonString (writer, entry.Names[i]);
                    }
                    writer.Write ("],");
                }
                if (!string.IsNullOrEmpty (entry.Voice))
                {
                    writer.Write ("\"voice\":");
                    WriteJsonString (writer, entry.Voice);
                    writer.Write (',');
                }
                writer.Write ("\"message\":");
                WriteJsonString (writer, entry.Message);
                writer.WriteLine ('}');
            }
        }

        public static void WriteJsonString (TextWriter writer, string text)
        {
            writer.Write ('"');
            for (int i = 0; i < text.Length; ++i)
            {
                char c = text[i];
                switch (c)
                {
                case '"':  writer.Write ("\\\""); break;
                case '\\': writer.Write ("\\\\"); break;
                case '\b': writer.Write ("\\b");  break;
                case '\f': writer.Write ("\\f");  break;
                case '\n': writer.Write ("\\n");  break;
                case '\r': writer.Write ("\\r");  break;
                case '\t': writer.Write ("\\t");  break;
                default:
                    if (c < ' ')
                    {
                        writer.Write ("\\u");
                        writer.Write (((int)c).ToString ("X4"));
                    }
                    else
                    {
                        writer.Write (c);
                    }
                    break;
                }
            }
            writer.Write ('"');
        }
    }

    public abstract class ScriptFormat : IResource
    {
        public override string Type { get { return "script"; } }

        public abstract bool IsScript (IBinaryStream file);

        public abstract Stream ConvertFrom (IBinaryStream file);
        public abstract Stream ConvertBack (IBinaryStream file);

        public abstract ScriptData Read (string name, Stream file);
        public abstract void Write (Stream file, ScriptData script);

        public static ScriptFormat FindFormat (IBinaryStream file)
        {
            foreach (var impl in FormatCatalog.Instance.FindFormats<ScriptFormat> (file.Name, file.Signature))
            {
                try
                {
                    file.Position = 0;
                    if (impl.IsScript (file))
                        return impl;
                }
                catch (System.OperationCanceledException)
                {
                    throw;
                }
                catch { }
            }
            return null;
        }
    }

    public static class ScriptTextMode
    {
        public const string Filtered = "filtered";
        public const string Raw = "raw";
        public const string Dump = "dump";
        public const string JsonLines = "jsonl";
    }

    public interface IConfigurableScriptFormat
    {
        IEnumerable<string> TextModes { get; }
        string DefaultTextMode { get; }
        Stream ConvertFrom (IBinaryStream file, string text_mode);
    }

    public abstract class GenericScriptFormat : ScriptFormat
    {
        public override bool IsScript (IBinaryStream file)
        {
            return false;
        }

        public override Stream ConvertFrom (IBinaryStream file)
        {
            return file.AsStream;
        }

        public override Stream ConvertBack (IBinaryStream file)
        {
            return file.AsStream;
        }

        public override ScriptData Read (string name, Stream file)
        {
            throw new System.NotImplementedException();
        }

        public override void Write (Stream file, ScriptData script)
        {
            throw new System.NotImplementedException();
        }
    }

    [Export(typeof(ScriptFormat))]
    public class TextScriptFormat : GenericScriptFormat
    {
        public override string         Tag { get { return "TXT"; } }
        public override string Description { get { return "Text file"; } }
        public override uint     Signature { get { return 0; } }
    }

    [Export(typeof(ScriptFormat))]
    public class BinScriptFormat : GenericScriptFormat
    {
        public override string         Tag { get { return "SCR"; } }
        public override string Description { get { return "Binary script format"; } }
        public override uint     Signature { get { return 0; } }

        public BinScriptFormat ()
        {
            Extensions = new[] { "scr", "bin" };
        }
    }
}
