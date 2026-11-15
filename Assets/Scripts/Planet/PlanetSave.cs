using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using UnityEngine;

namespace LivingPlanet
{
    // Format versionné : seules les données de simulation connues sont lues, jamais des types provenant du fichier.
    static class PlanetSave
    {
        const int Version = 20;
        const int Magic = 0x4c505331;
        static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
        static readonly Type lavaFlow = typeof(PlanetSim).GetNestedType("LavaFlow", BindingFlags.NonPublic);
        public static string PathName { get { return Path.Combine(Application.persistentDataPath, "planet.save"); } }
        public static string SaveDirectory { get { return Path.Combine(Application.persistentDataPath, "Planets"); } }
        public static string NewPath() { return Path.Combine(SaveDirectory, Guid.NewGuid().ToString("N") + ".save"); }

        public sealed class Preview
        {
            public const int Width = 128, Height = 64;
            public string path;
            public string name;
            public int seed, year, day, season;
            public DateTime savedAt;
            public Color32[] pixels;
        }

        public static void WritePreview(BinaryWriter w, Preview p)
        {
            w.Write(p.seed); w.Write(p.year); w.Write(p.day); w.Write(p.season);
            w.Write(p.name ?? "");
            foreach (var c in p.pixels) { w.Write(c.r); w.Write(c.g); w.Write(c.b); }
        }

        static Preview ReadPreview(BinaryReader r, string path, int version)
        {
            var p = new Preview { path = path, seed = r.ReadInt32(), year = r.ReadInt32(), day = r.ReadInt32(), season = r.ReadInt32() };
            if (p.year < 1 || p.day < 1 || p.day > 12 || p.season < 0 || p.season > 3)
                throw new InvalidDataException("Date de l'aperçu invalide.");
            p.name = version >= 17 ? r.ReadString() : "";
            p.pixels = new Color32[Preview.Width * Preview.Height];
            for (int i = 0; i < p.pixels.Length; i++) p.pixels[i] = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), 255);
            return p;
        }

        public static Preview ReadPreview(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var r = new BinaryReader(gzip))
            {
                if (r.ReadInt32() != Magic) throw new InvalidDataException("Format de sauvegarde non reconnu.");
                int version = r.ReadInt32();
                if (version >= 2) return ReadPreview(r, path, version);
                if (version != 1) throw new InvalidDataException("Version de sauvegarde non prise en charge.");
                r.ReadInt32(); r.ReadSingle();
                return new Preview { path = path, seed = r.ReadInt32() };
            }
        }

        static bool DataType(Type t)
        {
            if (t == typeof(int) || t == typeof(float) || t == typeof(bool) || t == typeof(byte) ||
                t == typeof(string) || t == typeof(Vector3) || t == typeof(Quaternion)) return true;
            if (t.IsArray) return t.GetArrayRank() == 1 && DataType(t.GetElementType());
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return DataType(t.GetGenericArguments()[0]);
            return t == typeof(Plant) || t == typeof(Animal) || t == typeof(Pop) ||
                t == typeof(PlanetSim.Volcano) || t == lavaFlow || t == typeof(Tsunami) ||
                t == typeof(QuakeEvent) || t == typeof(EventMsg) || t == typeof(Asteroid) || t == typeof(AstFx) ||
                t == typeof(Iceberg);
        }

        static FieldInfo[] Fields(Type type)
        {
            lock (fieldCache) return FieldsLocked(type);
        }

        static FieldInfo[] FieldsLocked(Type type)
        {
            FieldInfo[] fields;
            if (fieldCache.TryGetValue(type, out fields)) return fields;
            var result = new List<FieldInfo>();
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (type == typeof(PlanetSim) && f.Name == "aeolian") continue;
                // Fissures magmatiques transitoires (voir Hazards.cs/Fissure) : effet d'ambiance qui s'éteint en
                // quelques centaines de pas, pas besoin de persister à la sauvegarde (même traitement qu'aeolian).
                if (type == typeof(PlanetSim) && f.Name == "fissures") continue;
                // Couches visuelles d'érosion (voir ColorCell ViewMode 10/11) : simple moyenne mobile diagnostique
                // reconstruite en quelques dizaines de pas, pas besoin d'être persistée (même traitement qu'aeolian).
                if (type == typeof(PlanetSim) && (f.Name == "waterErode" || f.Name == "windErode")) continue;
                if (type == typeof(PlanetSim) && (f.Name == "tsunamiOut" || f.Name == "tsunamiActiveN")) continue;
                if (type == typeof(WaterState) && (f.Name == "nextSalt" || f.Name == "outSalt" || f.Name == "airOutScale")) continue;
                if (DataType(f.FieldType))
                {
                    if (!(f.IsInitOnly && f.FieldType.IsValueType)) result.Add(f);
                }
                else if (f.FieldType != typeof(PlanetGrid) && f.FieldType != typeof(PlanetSim) &&
                         f.FieldType != typeof(Ecosystem) && f.FieldType != typeof(EcoFields) &&
                         f.FieldType != typeof(WaterState) && !typeof(System.Random).IsAssignableFrom(f.FieldType) && !typeof(Delegate).IsAssignableFrom(f.FieldType))
                    throw new InvalidDataException("Type non pris en charge dans la sauvegarde : " + type.Name + "." + f.Name);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            fields = result.ToArray();
            fieldCache.Add(type, fields);
            return fields;
        }

        public sealed class Snapshot
        {
            internal FieldInfo[] fields;
            internal object[] values;
        }

        public static Snapshot Capture(object value)
        {
            var fields = Fields(value.GetType());
            var snapshot = new Snapshot { fields = fields, values = new object[fields.Length] };
            for (int i = 0; i < fields.Length; i++)
                snapshot.values[i] = CaptureValue(fields[i].FieldType, fields[i].GetValue(value));
            return snapshot;
        }

        static object CaptureValue(Type type, object value)
        {
            if (value == null || type.IsValueType || type == typeof(string)) return value;
            if (type.IsArray)
            {
                var array = (Array)value;
                Type element = type.GetElementType();
                if (element.IsValueType || element == typeof(string)) return array.Clone();
                var items = new object[array.Length];
                for (int i = 0; i < items.Length; i++) items[i] = CaptureValue(element, array.GetValue(i));
                return items;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)value;
                Type element = type.GetGenericArguments()[0];
                var items = new object[list.Count];
                for (int i = 0; i < items.Length; i++) items[i] = CaptureValue(element, list[i]);
                return items;
            }
            return Capture(value);
        }

        public static void WriteSnapshot(BinaryWriter writer, Snapshot snapshot)
        {
            writer.Write(snapshot.fields.Length);
            for (int i = 0; i < snapshot.fields.Length; i++)
            {
                var field = snapshot.fields[i];
                writer.Write(field.Name);
                WriteSnapshotValue(writer, field.FieldType, snapshot.values[i]);
            }
        }

        static void WriteSnapshotValue(BinaryWriter w, Type type, object value)
        {
            if (value == null || type.IsValueType || type == typeof(string))
            {
                WriteValue(w, type, value);
                return;
            }
            if (type.IsArray && (type.GetElementType().IsValueType || type.GetElementType() == typeof(string)))
            {
                WriteValue(w, type, value);
                return;
            }
            w.Write(true);
            if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            {
                var items = (object[])value;
                Type element = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
                w.Write(items.Length);
                foreach (var item in items) WriteSnapshotValue(w, element, item);
                return;
            }
            WriteSnapshot(w, (Snapshot)value);
        }

        public static void WriteObject(BinaryWriter writer, object value)
        {
            var fields = Fields(value.GetType());
            writer.Write(fields.Length);
            foreach (var field in fields)
            {
                writer.Write(field.Name);
                WriteValue(writer, field.FieldType, field.GetValue(value));
            }
        }

        static void WriteValue(BinaryWriter w, Type type, object value)
        {
            if (type == typeof(int)) { w.Write((int)value); return; }
            if (type == typeof(float)) { w.Write((float)value); return; }
            if (type == typeof(bool)) { w.Write((bool)value); return; }
            if (type == typeof(byte)) { w.Write((byte)value); return; }
            if (type == typeof(Vector3))
            {
                var v = (Vector3)value; w.Write(v.x); w.Write(v.y); w.Write(v.z); return;
            }
            if (type == typeof(Quaternion))
            {
                var q = (Quaternion)value; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); return;
            }
            w.Write(value != null);
            if (value == null) return;
            if (type == typeof(string)) { w.Write((string)value); return; }
            if (type.IsArray)
            {
                var array = (Array)value;
                w.Write(array.Length);
                Type element = type.GetElementType();
                if (element == typeof(float) || element == typeof(int) || element == typeof(byte))
                {
                    var bytes = new byte[Buffer.ByteLength(array)];
                    Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
                    w.Write(bytes);
                }
                else for (int i = 0; i < array.Length; i++) WriteValue(w, element, array.GetValue(i));
                return;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)value;
                w.Write(list.Count);
                Type element = type.GetGenericArguments()[0];
                foreach (var item in list) WriteValue(w, element, item);
                return;
            }
            WriteObject(w, value);
        }

        public static void ReadObject(BinaryReader reader, object value, int maxItems, string[] allowMissingFields = null, string[] ignoreFields = null)
        {
            var fields = Fields(value.GetType());
            int count = reader.ReadInt32();
            int allowedMissing = allowMissingFields != null ? allowMissingFields.Length : 0;
            if (count > fields.Length || count < fields.Length - allowedMissing)
                throw new InvalidDataException("La structure de la sauvegarde ne correspond pas à cette version.");
            var byName = new Dictionary<string, FieldInfo>(fields.Length);
            foreach (var field in fields) byName.Add(field.Name, field);
            var seen = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                FieldInfo field;
                if (!byName.TryGetValue(name, out field) || !seen.Add(name))
                    throw new InvalidDataException("Champ de sauvegarde inattendu : " + name);
                object loaded = ReadValue(reader, field.FieldType, maxItems);
                // champ dont la forme a changé entre deux versions (ex. nombre de couches géologiques) : on consomme
                // sa valeur pour avancer dans le flux, mais on garde celle déjà allouée par le constructeur (remise à zéro).
                if (ignoreFields != null && Array.IndexOf(ignoreFields, name) >= 0) continue;
                if (field.FieldType.IsArray && field.GetValue(value) is Array original &&
                    (loaded == null || ((Array)loaded).Length != original.Length))
                    throw new InvalidDataException("Taille de tableau invalide : " + field.Name);
                if (!field.IsInitOnly) field.SetValue(value, loaded);
                else if (field.FieldType.IsArray)
                {
                    var existing = (Array)field.GetValue(value);
                    var saved = (Array)loaded;
                    if (existing == null || saved == null || existing.Length != saved.Length)
                        throw new InvalidDataException("Taille de tableau invalide : " + field.Name);
                    Array.Copy(saved, existing, saved.Length);
                }
                else
                {
                    var existing = (IList)field.GetValue(value);
                    var saved = (IList)loaded;
                    if (existing == null || saved == null) throw new InvalidDataException("Liste manquante : " + field.Name);
                    existing.Clear();
                    foreach (var item in saved) existing.Add(item);
                }
            }
            foreach (var field in fields)
                if (!seen.Contains(field.Name) && !(allowMissingFields != null && Array.IndexOf(allowMissingFields, field.Name) >= 0))
                    throw new InvalidDataException("Champ de sauvegarde manquant : " + field.Name);
        }

        static int ReadCount(BinaryReader r, int maxItems)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > maxItems) throw new InvalidDataException("Taille de données invalide dans la sauvegarde.");
            return n;
        }

        static object ReadValue(BinaryReader r, Type type, int maxItems)
        {
            if (type == typeof(int)) return r.ReadInt32();
            if (type == typeof(float)) return r.ReadSingle();
            if (type == typeof(bool)) return r.ReadBoolean();
            if (type == typeof(byte)) return r.ReadByte();
            if (type == typeof(Vector3)) return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (type == typeof(Quaternion)) return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!r.ReadBoolean()) return null;
            if (type == typeof(string)) return r.ReadString();
            if (type.IsArray)
            {
                int count = ReadCount(r, maxItems);
                Type element = type.GetElementType();
                Array array = Array.CreateInstance(element, count);
                if (element == typeof(float) || element == typeof(int) || element == typeof(byte))
                {
                    int byteCount = Buffer.ByteLength(array);
                    byte[] bytes = r.ReadBytes(byteCount);
                    if (bytes.Length != byteCount) throw new EndOfStreamException();
                    Buffer.BlockCopy(bytes, 0, array, 0, byteCount);
                }
                else for (int i = 0; i < count; i++) array.SetValue(ReadValue(r, element, maxItems), i);
                return array;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                int count = ReadCount(r, maxItems);
                var list = (IList)Activator.CreateInstance(type);
                Type element = type.GetGenericArguments()[0];
                for (int i = 0; i < count; i++) list.Add(ReadValue(r, element, maxItems));
                return list;
            }
            object instance = Activator.CreateInstance(type, true);
            ReadObject(r, instance, maxItems);
            return instance;
        }

        public static void WriteFile(string path, Action<BinaryWriter> write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
                using (var writer = new BinaryWriter(gzip))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    write(writer);
                    writer.Write(Magic ^ Version);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public static void ReadFile(string path, Action<BinaryReader, int> read)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new BinaryReader(gzip))
            {
                if (reader.ReadInt32() != Magic)
                    throw new InvalidDataException("Format ou version de sauvegarde non pris en charge.");
                int version = reader.ReadInt32();
                if (version < 1 || version > Version) throw new InvalidDataException("Format ou version de sauvegarde non pris en charge.");
                if (version >= 2) ReadPreview(reader, path, version);
                read(reader, version);
                if (reader.ReadInt32() != (Magic ^ version)) throw new InvalidDataException("Fin de sauvegarde invalide.");
                if (gzip.ReadByte() != -1) throw new InvalidDataException("Données supplémentaires dans la sauvegarde.");
            }
        }
    }
}
