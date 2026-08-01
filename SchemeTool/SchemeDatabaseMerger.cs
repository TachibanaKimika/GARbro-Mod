using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using GameRes;
using GameRes.Compression;

namespace SchemeTool
{
    internal sealed class SchemeDatabaseException : Exception
    {
        public SchemeDatabaseException (string message) : base (message)
        {
        }

        public SchemeDatabaseException (string message, Exception inner) : base (message, inner)
        {
        }
    }

    internal sealed class SchemeDatabaseSnapshot
    {
        public SchemeDataBase Database;
        public SchemeDatabaseMetadata Metadata;
    }

    internal sealed class SchemeDatabaseMetadata
    {
        public string Role;
        public long Length;
        public string Sha256;
        public int Version;
        public int SchemeCount;
        public int GameMapCount;
    }

    internal sealed class SchemeDatabaseInspection
    {
        public string SchemaVersion = "garbro.scheme-database-inspection/v1";
        public SchemeDatabaseMetadata Input;
        public string SemanticHash;
        public List<SchemeInventoryEntry> Schemes;
    }

    internal sealed class SchemeInventoryEntry
    {
        public string Key;
        public string ValueType;
        public string SemanticHash;
    }

    internal sealed class SchemeMergeReport
    {
        public string SchemaVersion = "garbro.scheme-database-merge/v1";
        public string Status;
        public List<SchemeDatabaseMetadata> Inputs;
        public SchemeMergeResultMetadata Result;
        public SchemeMergeSummary Summary;
        public List<SchemeMergeChange> Changes;
        public List<SchemeMergeChange> Conflicts;
        public List<SchemeMergeInventoryEntry> Schemes;
    }

    internal sealed class SchemeMergeResultMetadata
    {
        public int Version;
        public int SchemeCount;
        public int GameMapCount;
        public string SemanticHash;
    }

    internal sealed class SchemeMergeSummary
    {
        public int Changes;
        public int Conflicts;
        public int OursDecisions;
        public int TheirsDecisions;
        public int SameChangeDecisions;
    }

    internal sealed class SchemeMergeChange
    {
        public string Path;
        public string Decision;
        public string ValueType;
        public string BaseHash;
        public string OursHash;
        public string TheirsHash;
        public string ResultHash;
    }

    internal sealed class SchemeMergeInventoryEntry
    {
        public string Key;
        public string BaseHash;
        public string OursHash;
        public string TheirsHash;
        public string ResultHash;
        public string ResultType;
    }

    internal sealed class SchemeMergeOutcome
    {
        public SchemeDataBase Database;
        public SchemeMergeReport Report;
    }

    internal static class SchemeDatabaseFile
    {
        const string SchemeId = "GARbroDB";
        const long MaximumInputBytes = 64L * 1024 * 1024;

        public static SchemeDatabaseSnapshot Read (string path, string role)
        {
            string fullPath = ValidateInputPath (path);
            var info = new FileInfo (fullPath);
            SchemeDataBase database;
            int headerVersion;
            try
            {
                using (var input = File.OpenRead (fullPath))
                using (var reader = new BinaryReader (input, Encoding.UTF8, true))
                {
                    var header = new string (reader.ReadChars (SchemeId.Length));
                    if (!string.Equals (header, SchemeId, StringComparison.Ordinal))
                        throw new SchemeDatabaseException ("Invalid scheme database header: " + role);
                    headerVersion = reader.ReadInt32();
                    using (var compressed = new ZLibStream (input, CompressionMode.Decompress, true))
                    {
                        var formatter = new BinaryFormatter();
                        database = formatter.Deserialize (compressed) as SchemeDataBase;
                    }
                }
            }
            catch (SchemeDatabaseException)
            {
                throw;
            }
            catch (Exception X)
            {
                throw new SchemeDatabaseException ("Could not deserialize trusted scheme database " + role + ".", X);
            }
            ValidateDatabase (database, headerVersion, role);
            return new SchemeDatabaseSnapshot {
                Database = database,
                Metadata = new SchemeDatabaseMetadata {
                    Role = role,
                    Length = info.Length,
                    Sha256 = ComputeFileHash (fullPath),
                    Version = database.Version,
                    SchemeCount = database.SchemeMap.Count,
                    GameMapCount = database.GameMap.Count,
                },
            };
        }

        public static void Write (string path, SchemeDataBase database, bool overwrite)
        {
            if (null == database)
                throw new ArgumentNullException ("database");
            string fullPath = Path.GetFullPath (path);
            string directory = Path.GetDirectoryName (fullPath);
            if (string.IsNullOrEmpty (directory) || !Directory.Exists (directory))
                throw new SchemeDatabaseException ("Output directory does not exist.");
            if (IsReparsePoint (directory))
                throw new SchemeDatabaseException ("Output directory is a reparse point.");
            if (File.Exists (fullPath) && IsReparsePoint (fullPath))
                throw new SchemeDatabaseException ("Output path is a reparse point.");
            if (File.Exists (fullPath) && !overwrite)
                throw new SchemeDatabaseException ("Output already exists; pass --overwrite deliberately.");

            string temporary = Path.Combine (directory, "." + Path.GetFileName (fullPath)
                + "." + Guid.NewGuid().ToString ("N") + ".tmp");
            try
            {
                using (var output = new FileStream (temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new BinaryWriter (output, Encoding.UTF8, true))
                    {
                        writer.Write (SchemeId.ToCharArray());
                        writer.Write (database.Version);
                    }
                    var formatter = new BinaryFormatter();
                    using (var compressed = new ZLibStream (output, CompressionMode.Compress, true))
                        formatter.Serialize (compressed, database);
                    output.Flush (true);
                }
                var verified = Read (temporary, "output-validation");
                var hasher = new SemanticHasher();
                if (!string.Equals (hasher.Hash (database), hasher.Hash (verified.Database),
                    StringComparison.Ordinal))
                {
                    throw new SchemeDatabaseException ("Serialized scheme database failed semantic round-trip validation.");
                }
                if (File.Exists (fullPath))
                {
                    string backup = temporary + ".bak";
                    File.Replace (temporary, fullPath, backup, true);
                    File.Delete (backup);
                }
                else
                {
                    File.Move (temporary, fullPath);
                }
            }
            finally
            {
                if (File.Exists (temporary))
                    File.Delete (temporary);
            }
        }

        static string ValidateInputPath (string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                throw new SchemeDatabaseException ("Scheme database path is empty.");
            string fullPath = Path.GetFullPath (path);
            if (!File.Exists (fullPath))
                throw new SchemeDatabaseException ("Scheme database does not exist: " + fullPath);
            var info = new FileInfo (fullPath);
            if (info.Length <= 12 || info.Length > MaximumInputBytes)
                throw new SchemeDatabaseException ("Scheme database size is outside the trusted 12-byte to 64-MiB boundary.");
            if (IsReparsePoint (fullPath))
                throw new SchemeDatabaseException ("Scheme database is a reparse point: " + fullPath);
            return fullPath;
        }

        static bool IsReparsePoint (string path)
        {
            return 0 != (File.GetAttributes (path) & FileAttributes.ReparsePoint);
        }

        static void ValidateDatabase (SchemeDataBase database, int headerVersion, string role)
        {
            if (null == database)
                throw new SchemeDatabaseException ("Deserialized database is null: " + role);
            if (database.Version != headerVersion)
                throw new SchemeDatabaseException ("Header and payload versions differ: " + role);
            if (database.SchemeMap == null)
                database.SchemeMap = new Dictionary<string, ResourceScheme>();
            if (database.GameMap == null)
                database.GameMap = new Dictionary<string, string>();
        }

        static string ComputeFileHash (string path)
        {
            using (var input = File.OpenRead (path))
            using (var sha = SHA256.Create())
                return ToHex (sha.ComputeHash (input));
        }

        internal static string ToHex (byte[] bytes)
        {
            var text = new StringBuilder (bytes.Length * 2);
            foreach (byte value in bytes)
                text.Append (value.ToString ("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }

    internal sealed class SchemeDatabaseMerger
    {
        readonly SemanticHasher m_hasher = new SemanticHasher();
        readonly List<SchemeMergeChange> m_changes = new List<SchemeMergeChange>();
        readonly List<SchemeMergeChange> m_conflicts = new List<SchemeMergeChange>();

        public SchemeMergeOutcome Merge (SchemeDatabaseSnapshot baseSnapshot,
                                         SchemeDatabaseSnapshot oursSnapshot,
                                         SchemeDatabaseSnapshot theirsSnapshot)
        {
            if (null == baseSnapshot || null == oursSnapshot || null == theirsSnapshot)
                throw new ArgumentNullException ("snapshot");

            Node schemeMap = MergeNode ("schemeMap",
                Node.PresentValue (baseSnapshot.Database.SchemeMap),
                Node.PresentValue (oursSnapshot.Database.SchemeMap),
                Node.PresentValue (theirsSnapshot.Database.SchemeMap), 0);
            Node gameMap = MergeNode ("gameMap",
                Node.PresentValue (baseSnapshot.Database.GameMap),
                Node.PresentValue (oursSnapshot.Database.GameMap),
                Node.PresentValue (theirsSnapshot.Database.GameMap), 0);

            int maximumVersion = Math.Max (baseSnapshot.Database.Version,
                Math.Max (oursSnapshot.Database.Version, theirsSnapshot.Database.Version));
            if (maximumVersion == int.MaxValue)
                throw new SchemeDatabaseException ("Scheme database version cannot be incremented.");
            var result = new SchemeDataBase {
                Version = maximumVersion + 1,
                SchemeMap = (Dictionary<string, ResourceScheme>)schemeMap.Value,
                GameMap = (Dictionary<string, string>)gameMap.Value,
            };

            var report = new SchemeMergeReport {
                Status = m_conflicts.Count == 0 ? "clean" : "conflict",
                Inputs = new List<SchemeDatabaseMetadata> {
                    baseSnapshot.Metadata,
                    oursSnapshot.Metadata,
                    theirsSnapshot.Metadata,
                },
                Result = new SchemeMergeResultMetadata {
                    Version = result.Version,
                    SchemeCount = result.SchemeMap.Count,
                    GameMapCount = result.GameMap.Count,
                    SemanticHash = m_hasher.Hash (result),
                },
                Changes = m_changes.OrderBy (x => x.Path, StringComparer.Ordinal).ToList(),
                Conflicts = m_conflicts.OrderBy (x => x.Path, StringComparer.Ordinal).ToList(),
                Schemes = BuildSchemeInventory (baseSnapshot.Database.SchemeMap,
                    oursSnapshot.Database.SchemeMap, theirsSnapshot.Database.SchemeMap,
                    result.SchemeMap),
            };
            report.Summary = new SchemeMergeSummary {
                Changes = report.Changes.Count,
                Conflicts = report.Conflicts.Count,
                OursDecisions = report.Changes.Count (x => x.Decision == "ours"),
                TheirsDecisions = report.Changes.Count (x => x.Decision == "theirs"),
                SameChangeDecisions = report.Changes.Count (x => x.Decision == "both_same"),
            };
            return new SchemeMergeOutcome { Database = result, Report = report };
        }

        Node MergeNode (string path, Node baseNode, Node oursNode, Node theirsNode, int depth)
        {
            if (depth > 64)
                return Conflict (path, baseNode, oursNode, theirsNode, "maximum_depth");
            if (Equivalent (oursNode, theirsNode))
            {
                if (!Equivalent (baseNode, oursNode))
                    RecordChange (path, "both_same", baseNode, oursNode, theirsNode, oursNode);
                return oursNode;
            }
            if (Equivalent (oursNode, baseNode))
            {
                RecordChange (path, "theirs", baseNode, oursNode, theirsNode, theirsNode);
                return theirsNode;
            }
            if (Equivalent (theirsNode, baseNode))
            {
                RecordChange (path, "ours", baseNode, oursNode, theirsNode, oursNode);
                return oursNode;
            }
            if (!oursNode.Present || !theirsNode.Present)
                return Conflict (path, baseNode, oursNode, theirsNode, "delete_modify_conflict");
            if (null == oursNode.Value || null == theirsNode.Value)
                return Conflict (path, baseNode, oursNode, theirsNode, "null_value_conflict");
            Type oursType = oursNode.Value.GetType();
            Type theirsType = theirsNode.Value.GetType();
            if (oursType != theirsType || (baseNode.Present && null != baseNode.Value
                && baseNode.Value.GetType() != oursType))
            {
                return Conflict (path, baseNode, oursNode, theirsNode, "type_conflict");
            }
            if (oursNode.Value is IDictionary)
                return MergeDictionary (path, baseNode, oursNode, theirsNode, depth + 1);
            if (CanMergeObject (oursType))
                return MergeObject (path, baseNode, oursNode, theirsNode, depth + 1);
            return Conflict (path, baseNode, oursNode, theirsNode, "value_conflict");
        }

        Node MergeDictionary (string path, Node baseNode, Node oursNode, Node theirsNode, int depth)
        {
            var baseDictionary = baseNode.Present ? baseNode.Value as IDictionary : null;
            var oursDictionary = (IDictionary)oursNode.Value;
            var theirsDictionary = (IDictionary)theirsNode.Value;
            IDictionary result;
            object comparer;
            try
            {
                if (!TryMergeDictionaryComparer (path, baseDictionary, oursDictionary,
                    theirsDictionary, out comparer))
                {
                    return Conflict (path, baseNode, oursNode, theirsNode,
                        "dictionary_comparer_conflict");
                }
                result = CreateEmptyDictionary (oursDictionary, comparer);
            }
            catch (Exception)
            {
                return Conflict (path, baseNode, oursNode, theirsNode, "dictionary_constructor_conflict");
            }

            var keys = new List<object>();
            AddKeys (keys, baseDictionary, comparer, oursNode.Value.GetType());
            AddKeys (keys, oursDictionary, comparer, oursNode.Value.GetType());
            AddKeys (keys, theirsDictionary, comparer, oursNode.Value.GetType());
            keys.Sort ((left, right) => StringComparer.Ordinal.Compare (
                KeySortToken (left), KeySortToken (right)));
            foreach (object key in keys)
            {
                Node baseValue = GetDictionaryNode (baseDictionary, key);
                Node oursValue = GetDictionaryNode (oursDictionary, key);
                Node theirsValue = GetDictionaryNode (theirsDictionary, key);
                Node merged = MergeNode (path + "[" + FormatKey (key) + "]",
                    baseValue, oursValue, theirsValue, depth);
                if (merged.Present)
                {
                    try
                    {
                        result.Add (key, merged.Value);
                    }
                    catch (Exception)
                    {
                        return Conflict (path, baseNode, oursNode, theirsNode,
                            "dictionary_key_conflict");
                    }
                }
            }
            return Node.PresentValue (result);
        }

        bool TryMergeDictionaryComparer (string path, IDictionary baseDictionary,
                                          IDictionary oursDictionary,
                                          IDictionary theirsDictionary,
                                          out object result)
        {
            result = null;
            Type type = oursDictionary.GetType();
            PropertyInfo property = type.GetProperty ("Comparer", BindingFlags.Instance | BindingFlags.Public);
            if (null == property || !property.CanRead)
                return true;
            Node baseNode = null == baseDictionary ? Node.Absent
                : Node.PresentValue (property.GetValue (baseDictionary, null));
            Node oursNode = Node.PresentValue (property.GetValue (oursDictionary, null));
            Node theirsNode = Node.PresentValue (property.GetValue (theirsDictionary, null));
            if (Equivalent (oursNode, theirsNode))
            {
                result = oursNode.Value;
                if (!Equivalent (baseNode, oursNode))
                    RecordChange (path + ".Comparer", "both_same", baseNode,
                        oursNode, theirsNode, oursNode);
                return true;
            }
            if (Equivalent (oursNode, baseNode))
            {
                result = theirsNode.Value;
                RecordChange (path + ".Comparer", "theirs", baseNode,
                    oursNode, theirsNode, theirsNode);
                return true;
            }
            if (Equivalent (theirsNode, baseNode))
            {
                result = oursNode.Value;
                RecordChange (path + ".Comparer", "ours", baseNode,
                    oursNode, theirsNode, oursNode);
                return true;
            }
            return false;
        }

        static IDictionary CreateEmptyDictionary (IDictionary source, object comparer)
        {
            Type type = source.GetType();
            PropertyInfo comparerProperty = type.GetProperty ("Comparer", BindingFlags.Instance | BindingFlags.Public);
            if (null != comparerProperty && comparerProperty.CanRead)
            {
                ConstructorInfo constructor = type.GetConstructor (new Type[] {
                    typeof (int), comparerProperty.PropertyType,
                });
                if (null != constructor)
                    return (IDictionary)constructor.Invoke (new object[] { source.Count, comparer });
                constructor = type.GetConstructor (new Type[] { comparerProperty.PropertyType });
                if (null != constructor)
                    return (IDictionary)constructor.Invoke (new object[] { comparer });
            }
            return (IDictionary)Activator.CreateInstance (type);
        }

        Node MergeObject (string path, Node baseNode, Node oursNode, Node theirsNode, int depth)
        {
            Type type = oursNode.Value.GetType();
            MemberInfo[] members;
            try
            {
                members = FormatterServices.GetSerializableMembers (type)
                    .OrderBy (MemberKey, StringComparer.Ordinal).ToArray();
            }
            catch (Exception)
            {
                return Conflict (path, baseNode, oursNode, theirsNode, "serialization_members_conflict");
            }
            object[] baseValues = baseNode.Present && null != baseNode.Value
                ? FormatterServices.GetObjectData (baseNode.Value, members) : null;
            object[] oursValues = FormatterServices.GetObjectData (oursNode.Value, members);
            object[] theirsValues = FormatterServices.GetObjectData (theirsNode.Value, members);
            var resultValues = new object[members.Length];
            for (int i = 0; i < members.Length; ++i)
            {
                Node merged = MergeNode (path + "." + members[i].Name,
                    null == baseValues ? Node.Absent : Node.PresentValue (baseValues[i]),
                    Node.PresentValue (oursValues[i]), Node.PresentValue (theirsValues[i]), depth);
                if (!merged.Present)
                    return Conflict (path + "." + members[i].Name,
                        null == baseValues ? Node.Absent : Node.PresentValue (baseValues[i]),
                        Node.PresentValue (oursValues[i]), Node.PresentValue (theirsValues[i]),
                        "required_field_deleted");
                resultValues[i] = merged.Value;
            }
            try
            {
                object result = FormatterServices.GetUninitializedObject (type);
                result = FormatterServices.PopulateObjectMembers (result, members, resultValues);
                return Node.PresentValue (result);
            }
            catch (Exception)
            {
                return Conflict (path, baseNode, oursNode, theirsNode, "object_construction_conflict");
            }
        }

        Node Conflict (string path, Node baseNode, Node oursNode, Node theirsNode, string decision)
        {
            Node fallback = oursNode.Present ? oursNode : theirsNode;
            var change = CreateChange (path, decision, baseNode, oursNode, theirsNode, fallback);
            m_conflicts.Add (change);
            return fallback;
        }

        void RecordChange (string path, string decision, Node baseNode, Node oursNode,
                           Node theirsNode, Node resultNode)
        {
            m_changes.Add (CreateChange (path, decision, baseNode, oursNode, theirsNode, resultNode));
        }

        SchemeMergeChange CreateChange (string path, string decision, Node baseNode,
                                        Node oursNode, Node theirsNode, Node resultNode)
        {
            object typeValue = resultNode.Present ? resultNode.Value
                : (oursNode.Present ? oursNode.Value : (theirsNode.Present ? theirsNode.Value : baseNode.Value));
            return new SchemeMergeChange {
                Path = path,
                Decision = decision,
                ValueType = null == typeValue ? null : typeValue.GetType().FullName,
                BaseHash = HashNode (baseNode),
                OursHash = HashNode (oursNode),
                TheirsHash = HashNode (theirsNode),
                ResultHash = HashNode (resultNode),
            };
        }

        bool Equivalent (Node left, Node right)
        {
            if (left.Present != right.Present)
                return false;
            if (!left.Present)
                return true;
            return string.Equals (m_hasher.Hash (left.Value), m_hasher.Hash (right.Value),
                StringComparison.Ordinal);
        }

        string HashNode (Node node)
        {
            return node.Present ? m_hasher.Hash (node.Value) : null;
        }

        static bool CanMergeObject (Type type)
        {
            if (!type.IsSerializable || type.IsPrimitive || type.IsEnum || type.IsArray)
                return false;
            if (type == typeof (string) || type == typeof (decimal) || type == typeof (DateTime)
                || type == typeof (DateTimeOffset) || type == typeof (TimeSpan)
                || type == typeof (Guid) || typeof (IEnumerable).IsAssignableFrom (type))
                return false;
            return true;
        }

        static void AddKeys (List<object> keys, IDictionary dictionary,
                             object comparer, Type dictionaryType)
        {
            if (null == dictionary)
                return;
            foreach (object key in dictionary.Keys)
            {
                if (!keys.Any (existing => KeysEqual (existing, key, comparer, dictionaryType)))
                    keys.Add (key);
            }
        }

        static bool KeysEqual (object left, object right, object comparer, Type dictionaryType)
        {
            if (null == comparer || !dictionaryType.IsGenericType)
                return object.Equals (left, right);
            Type keyType = dictionaryType.GetGenericArguments()[0];
            Type equalityType = typeof (IEqualityComparer<>).MakeGenericType (keyType);
            if (!equalityType.IsInstanceOfType (comparer))
                return object.Equals (left, right);
            MethodInfo equals = equalityType.GetMethod ("Equals", new Type[] { keyType, keyType });
            return (bool)equals.Invoke (comparer, new object[] { left, right });
        }

        static Node GetDictionaryNode (IDictionary dictionary, object key)
        {
            return null != dictionary && dictionary.Contains (key)
                ? Node.PresentValue (dictionary[key]) : Node.Absent;
        }

        string KeySortToken (object key)
        {
            if (null == key)
                return "0:null";
            var text = key as string;
            if (null != text)
                return "1:string:" + text;
            if (key.GetType().IsPrimitive || key.GetType().IsEnum || key is decimal || key is Guid)
                return "2:" + key.GetType().FullName + ":" + Convert.ToString (key, CultureInfo.InvariantCulture);
            return "3:" + key.GetType().FullName + ":" + m_hasher.Hash (key);
        }

        static string FormatKey (object key)
        {
            if (null == key)
                return "null";
            var text = key as string;
            if (null != text)
                return "\"" + text.Replace ("\\", "\\\\").Replace ("\"", "\\\"") + "\"";
            return Convert.ToString (key, CultureInfo.InvariantCulture);
        }

        static string MemberKey (MemberInfo member)
        {
            return (null == member.DeclaringType ? string.Empty : member.DeclaringType.FullName)
                + ":" + member.Name;
        }

        List<SchemeMergeInventoryEntry> BuildSchemeInventory (
            IDictionary<string, ResourceScheme> baseSchemes,
            IDictionary<string, ResourceScheme> oursSchemes,
            IDictionary<string, ResourceScheme> theirsSchemes,
            IDictionary<string, ResourceScheme> resultSchemes)
        {
            var keys = new SortedSet<string> (StringComparer.Ordinal);
            keys.UnionWith (baseSchemes.Keys);
            keys.UnionWith (oursSchemes.Keys);
            keys.UnionWith (theirsSchemes.Keys);
            keys.UnionWith (resultSchemes.Keys);
            return keys.Select (key => {
                ResourceScheme result;
                resultSchemes.TryGetValue (key, out result);
                return new SchemeMergeInventoryEntry {
                    Key = key,
                    BaseHash = DictionaryHash (baseSchemes, key),
                    OursHash = DictionaryHash (oursSchemes, key),
                    TheirsHash = DictionaryHash (theirsSchemes, key),
                    ResultHash = DictionaryHash (resultSchemes, key),
                    ResultType = null == result ? null : result.GetType().FullName,
                };
            }).ToList();
        }

        string DictionaryHash (IDictionary<string, ResourceScheme> dictionary, string key)
        {
            ResourceScheme value;
            return dictionary.TryGetValue (key, out value) ? m_hasher.Hash (value) : null;
        }

        struct Node
        {
            public static readonly Node Absent = new Node { Present = false, Value = null };
            public bool Present;
            public object Value;

            public static Node PresentValue (object value)
            {
                return new Node { Present = true, Value = value };
            }
        }
    }

    internal sealed class SemanticHasher
    {
        readonly Dictionary<object, string> m_cache =
            new Dictionary<object, string> (ReferenceEqualityComparer.Instance);
        readonly HashSet<object> m_visiting =
            new HashSet<object> (ReferenceEqualityComparer.Instance);

        public string Hash (object value)
        {
            if (null == value)
                return HashText ("null");
            Type type = value.GetType();
            if (!type.IsValueType && !(value is string))
            {
                string cached;
                if (m_cache.TryGetValue (value, out cached))
                    return cached;
                if (!m_visiting.Add (value))
                    throw new SchemeDatabaseException ("Cyclic scheme graph is not supported.");
            }
            string result = ComputeHash (value, type);
            if (!type.IsValueType && !(value is string))
            {
                m_visiting.Remove (value);
                m_cache[value] = result;
            }
            return result;
        }

        string ComputeHash (object value, Type type)
        {
            var bytes = value as byte[];
            if (null != bytes)
            {
                using (var sha = SHA256.Create())
                    return HashText (type.AssemblyQualifiedName + ":" + bytes.Length.ToString (CultureInfo.InvariantCulture)
                        + ":" + SchemeDatabaseFile.ToHex (sha.ComputeHash (bytes)));
            }
            if (value is string || type.IsPrimitive || type.IsEnum || value is decimal
                || value is DateTime || value is DateTimeOffset || value is TimeSpan || value is Guid)
            {
                return HashText (type.AssemblyQualifiedName + ":" + ScalarText (value));
            }
            var dictionary = value as IDictionary;
            if (null != dictionary)
            {
                var entries = new List<string>();
                foreach (DictionaryEntry entry in dictionary)
                    entries.Add (Hash (entry.Key) + ":" + Hash (entry.Value));
                entries.Sort (StringComparer.Ordinal);
                string comparerHash = DictionaryComparerHash (dictionary, type);
                return HashText (type.AssemblyQualifiedName + ":" + comparerHash
                    + "{" + string.Join (",", entries) + "}");
            }
            var array = value as Array;
            if (null != array)
            {
                var entries = new List<string> (array.Length);
                foreach (object item in array)
                    entries.Add (Hash (item));
                return HashText (type.AssemblyQualifiedName + "[" + string.Join (",", entries) + "]");
            }
            var list = value as IList;
            if (null != list)
            {
                var entries = new List<string> (list.Count);
                foreach (object item in list)
                    entries.Add (Hash (item));
                return HashText (type.AssemblyQualifiedName + "[" + string.Join (",", entries) + "]");
            }
            var enumerable = value as IEnumerable;
            if (null != enumerable)
            {
                var entries = new List<string>();
                foreach (object item in enumerable)
                    entries.Add (Hash (item));
                if (IsSet (type))
                    entries.Sort (StringComparer.Ordinal);
                string comparerHash = IsSet (type)
                    ? CollectionComparerHash (value, type) : "ordered";
                return HashText (type.AssemblyQualifiedName + ":" + comparerHash
                    + "[" + string.Join (",", entries) + "]");
            }
            if (!type.IsSerializable)
                throw new SchemeDatabaseException ("Non-serializable scheme value: " + type.FullName);
            var members = FormatterServices.GetSerializableMembers (type)
                .OrderBy (member => (null == member.DeclaringType ? string.Empty : member.DeclaringType.FullName)
                    + ":" + member.Name, StringComparer.Ordinal).ToArray();
            var values = FormatterServices.GetObjectData (value, members);
            var fields = new List<string> (members.Length);
            for (int i = 0; i < members.Length; ++i)
                fields.Add (members[i].Name + ":" + Hash (values[i]));
            return HashText (type.AssemblyQualifiedName + "{" + string.Join (",", fields) + "}");
        }

        static string ScalarText (object value)
        {
            if (value is DateTime)
                return ((DateTime)value).ToString ("O", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset)
                return ((DateTimeOffset)value).ToString ("O", CultureInfo.InvariantCulture);
            if (value is float)
                return ((float)value).ToString ("R", CultureInfo.InvariantCulture);
            if (value is double)
                return ((double)value).ToString ("R", CultureInfo.InvariantCulture);
            return Convert.ToString (value, CultureInfo.InvariantCulture);
        }

        static bool IsSet (Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof (HashSet<>))
                return true;
            return type.GetInterfaces().Any (item => item.IsGenericType
                && item.GetGenericTypeDefinition() == typeof (ISet<>));
        }

        string DictionaryComparerHash (IDictionary dictionary, Type type)
        {
            return CollectionComparerHash (dictionary, type);
        }

        string CollectionComparerHash (object collection, Type type)
        {
            PropertyInfo property = type.GetProperty ("Comparer", BindingFlags.Instance | BindingFlags.Public);
            if (null == property || !property.CanRead)
                return "default-comparer";
            object comparer = property.GetValue (collection, null);
            return null == comparer ? "null-comparer" : Hash (comparer);
        }

        static string HashText (string text)
        {
            using (var sha = SHA256.Create())
                return SchemeDatabaseFile.ToHex (sha.ComputeHash (Encoding.UTF8.GetBytes (text)));
        }
    }

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

        public new bool Equals (object left, object right)
        {
            return object.ReferenceEquals (left, right);
        }

        public int GetHashCode (object value)
        {
            return RuntimeHelpers.GetHashCode (value);
        }
    }
}
