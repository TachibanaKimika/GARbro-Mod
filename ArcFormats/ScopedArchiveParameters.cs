using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GameRes.Formats
{
    // Explicit local parameters, scoped to one archive directory. Never serialized
    // into Formats.dat and never discovered from arbitrary adjacent files.
    internal static class ScopedArchiveParameters
    {
        public static JObject Read (string variable, string archive)
        {
            var path = Environment.GetEnvironmentVariable (variable);
            if (string.IsNullOrEmpty (path))
                return null;
            var info = new FileInfo (path);
            if (info.Length > 65536)
                throw new InvalidDataException ("Archive parameter file exceeds 64 KiB.");
            var data = JObject.Parse (File.ReadAllText (path));
            var root = (string)data["archive_directory"];
            if (string.IsNullOrEmpty (root) || !Path.IsPathRooted (root))
                throw new InvalidDataException ("archive_directory must be an absolute path.");
            if (!string.Equals (Path.GetDirectoryName (Path.GetFullPath (archive)).TrimEnd ('\\', '/'),
                                Path.GetFullPath (root).TrimEnd ('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return null;
            return data;
        }
    }
}
