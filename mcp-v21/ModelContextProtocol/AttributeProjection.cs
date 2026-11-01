using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // Never pass an SDK object to the serializer or reflect over its properties.
    public static class AttributeProjection
    {
        private const int MaxDepth = 8;
        internal const int MaxItems = 256;

        public delegate bool ValueConverter(object value, out object? converted);

        public static Attribute Read(string name, string accessMode, bool readable, Func<object?> read,
            ValueConverter? converter = null)
        {
            var result = new Attribute { Name = name, AccessMode = accessMode };
            if (!readable)
            {
                result.Status = "NotReadable";
                result.Error = "Attribute metadata does not permit reading.";
                return result;
            }

            object? value;
            try
            {
                value = read();
                result.ValueType = value?.GetType().FullName;
            }
            catch (Exception ex)
            {
                result.Status = "ReadFailed";
                result.Error = DescribeException(ex);
                return result;
            }

            try
            {
                result.Value = Project(value, 0, converter);
                result.Status = "Success";
            }
            catch (Exception ex)
            {
                result.Status = "ProjectionFailed";
                result.Error = DescribeException(ex);
            }
            return result;
        }

        public static object? Project(object? value, ValueConverter? converter = null) => Project(value, 0, converter);

        private static object? Project(object? value, int depth, ValueConverter? converter)
        {
            if (value == null) return null;
            if (depth > MaxDepth) throw new NotSupportedException("Attribute nesting exceeds 8 levels (possible cycle).");
            if (value is string || value is bool || value is byte || value is sbyte ||
                value is short || value is ushort || value is int || value is uint ||
                value is long || value is ulong || value is decimal) return value;
            if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d)) return d;
            if (value is float f && !float.IsNaN(f) && !float.IsInfinity(f)) return f;
            if (value is Enum || value is char || value is Guid || value is Version || value is Uri)
                return value.ToString();
            if (value is DateTime date) return date.ToString("O", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset offset) return offset.ToString("O", CultureInfo.InvariantCulture);
            if (value is TimeSpan duration) return duration.ToString("c", CultureInfo.InvariantCulture);
            if (value is CultureInfo culture) return culture.Name;
            if (value is FileSystemInfo path) return path.FullName;
            // Validate converter output too; an adapter cannot leak raw objects to JSON.
            if (converter != null && converter(value, out var converted))
                return Project(converted, depth + 1, converter);
            if (value is IDictionary dictionary)
            {
                var projected = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (projected.Count >= MaxItems) throw new NotSupportedException("Attribute collection exceeds 256 entries.");
                    if (!(entry.Key is string key)) throw new NotSupportedException("Attribute dictionary keys must be strings.");
                    projected.Add(key, Project(entry.Value, depth + 1, converter));
                }
                return projected;
            }
            if (value is IList list)
            {
                var projected = new List<object?>();
                foreach (var entry in list)
                {
                    if (projected.Count >= MaxItems) throw new NotSupportedException("Attribute collection exceeds 256 entries.");
                    projected.Add(Project(entry, depth + 1, converter));
                }
                return projected;
            }
            throw new NotSupportedException($"Attribute value type '{value.GetType().FullName}' has no JSON-safe projection. Add an explicit value converter; SDK properties were not traversed.");
        }

        public static string DescribeException(Exception exception)
        {
            var messages = new List<string>();
            for (Exception? current = exception; current != null && messages.Count < 5; current = current.InnerException)
                messages.Add(current.GetType().FullName + ": " + current.Message);
            return string.Join(" -> ", messages);
        }

        public static JsonObject Metadata(JsonObject? metadata, IEnumerable<Attribute>? attributes)
        {
            var result = metadata == null ? new JsonObject() : (JsonObject)metadata.DeepClone();
            int failed = 0;
            if (attributes != null)
                foreach (var attribute in attributes)
                    if (!attribute.Succeeded) failed++;
            result["attributesComplete"] = failed == 0;
            result["attributeFailureCount"] = failed;
            if (failed != 0)
            {
                result["success"] = false;
                result["partial"] = true;
            }
            return result;
        }
    }
}
