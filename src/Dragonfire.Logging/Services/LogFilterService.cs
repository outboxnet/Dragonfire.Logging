using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Dragonfire.Logging.Attributes;
using Dragonfire.Logging.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dragonfire.Logging.Services
{
    /// <inheritdoc cref="ILogFilterService"/>
    public sealed class LogFilterService : ILogFilterService
    {
        private static readonly JsonSerializerSettings SerializerSettings = new()
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            MaxDepth              = 32,
            NullValueHandling     = NullValueHandling.Ignore
        };

        private readonly SensitiveDataPolicy _policy;

        public LogFilterService(SensitiveDataPolicy policy)
        {
            _policy = policy;
        }

        /// <inheritdoc/>
        public object? FilterData(
            object? data,
            string[]? excludeProperties = null,
            string[]? includeProperties = null,
            int maxLength = 0,
            int maxDepth  = 0)
        {
            if (data is null) return null;

            try
            {
                var json  = JsonConvert.SerializeObject(data, SerializerSettings);
                var token = JToken.Parse(json);

                FilterToken(
                    token,
                    excludeProperties ?? Array.Empty<string>(),
                    includeProperties ?? Array.Empty<string>(),
                    maxDepth,
                    currentDepth: 0);

                var result = token.ToString(Formatting.None);

                if (maxLength > 0 && result.Length > maxLength)
                    result = result[..maxLength] + "...[TRUNCATED]";

                // Deserialise without a target type — returns JObject/JArray/primitive.
                // Avoids type-cast failures for anonymous types, object[], etc.
                return JsonConvert.DeserializeObject(result);
            }
            catch
            {
                return "[UNSERIALIZABLE]";
            }
        }

        /// <inheritdoc/>
        public string? FilterString(string? data, int maxLength = 0)
        {
            if (string.IsNullOrEmpty(data)) return data;

            var result = ApplyPatternRedaction(data);

            if (maxLength > 0 && result.Length > maxLength)
                result = result[..maxLength] + "...[TRUNCATED]";

            return result;
        }

        /// <inheritdoc/>
        public bool ShouldLogProperty(string propertyName, string[]? excludeProperties, string[]? includeProperties)
        {
            if (includeProperties is { Length: > 0 })
                return includeProperties.Contains(propertyName, StringComparer.OrdinalIgnoreCase);

            if (excludeProperties is { Length: > 0 })
                return !excludeProperties.Contains(propertyName, StringComparer.OrdinalIgnoreCase);

            return true;
        }

        /// <inheritdoc/>
        public Dictionary<string, object?> ExtractNamedProperties(object? data)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (data is null) return result;

            ExtractFromObject(data, result);
            return result;
        }

        // ── Private helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Recursively filters <paramref name="token"/> in-place:
        /// <list type="bullet">
        ///   <item>Removes excluded / sensitive properties from objects.</item>
        ///   <item>Applies pattern-based redaction to strings.</item>
        ///   <item>When <paramref name="maxDepth"/> &gt; 0 and <paramref name="currentDepth"/>
        ///   reaches the limit, replaces nested objects with <c>[N fields]</c> and arrays
        ///   with <c>[N items]</c>.</item>
        /// </list>
        /// </summary>
        private void FilterToken(
            JToken token,
            string[] exclude,
            string[] include,
            int      maxDepth,
            int      currentDepth)
        {
            switch (token.Type)
            {
                // ── Object ────────────────────────────────────────────────────
                case JTokenType.Object:
                {
                    // Depth limit reached — replace this object with a placeholder.
                    if (maxDepth > 0 && currentDepth >= maxDepth)
                    {
                        var fieldCount = ((JObject)token).Count;
                        token.Replace(new JValue($"[{fieldCount} fields]"));
                        return;
                    }

                    var obj = (JObject)token;

                    // Remove properties that fail include/exclude or are sensitive.
                    var toRemove = obj.Properties()
                        .Where(p =>
                            !ShouldLogProperty(p.Name, exclude, include)
                            || _policy.SensitiveFields.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var prop in toRemove)
                        prop.Remove();

                    // Recurse into surviving properties.
                    foreach (var prop in obj.Properties().ToList())
                        FilterToken(prop.Value, exclude, include, maxDepth, currentDepth + 1);

                    break;
                }

                // ── Array ─────────────────────────────────────────────────────
                case JTokenType.Array:
                {
                    // Depth limit reached — replace this array with a placeholder.
                    if (maxDepth > 0 && currentDepth >= maxDepth)
                    {
                        var itemCount = ((JArray)token).Count;
                        token.Replace(new JValue($"[{itemCount} items]"));
                        return;
                    }

                    foreach (var item in token.Children().ToList())
                        FilterToken(item, exclude, include, maxDepth, currentDepth + 1);

                    break;
                }

                // ── String — apply pattern-based redaction ────────────────────
                case JTokenType.String:
                {
                    var str = token.Value<string>();
                    if (!string.IsNullOrEmpty(str))
                    {
                        var redacted = ApplyPatternRedaction(str);
                        if (!ReferenceEquals(redacted, str))
                            token.Replace(new JValue(redacted));
                    }
                    break;
                }
            }
        }

        private string ApplyPatternRedaction(string text)
        {
            var result = text;

            foreach (var (pattern, replacement) in _policy.RedactionPatterns)
                result = pattern.Replace(result, replacement);

            if (_policy.RedactEmails)
                result = Regex.Replace(result,
                    @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
                    "[EMAIL_REDACTED]");

            if (_policy.RedactPhoneNumbers)
                result = Regex.Replace(result,
                    @"\b\d{3}[.\-]?\d{3}[.\-]?\d{4}\b",
                    "[PHONE_REDACTED]");

            return result;
        }

        // ── [LogProperty] extraction ─────────────────────────────────────────

        /// <summary>
        /// Scans <paramref name="data"/> for <c>[LogProperty]</c>-decorated properties.
        /// If <paramref name="data"/> is a non-string <see cref="IEnumerable"/>, each
        /// element is scanned (first-level elements only).
        /// </summary>
        private static void ExtractFromObject(object data, Dictionary<string, object?> result)
        {
            var type = data.GetType();

            // Skip scalars — they can't carry [LogProperty] attributes.
            if (IsSimpleType(type)) return;

            // For non-string collections scan each element.
            if (data is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                    if (item is not null)
                        ExtractFromObject(item, result);
                return;
            }

            // Reflect over public instance properties.
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = prop.GetCustomAttribute<LogPropertyAttribute>();
                if (attr is null) continue;

                var key = attr.Name ?? prop.Name;
                try   { result.TryAdd(key, prop.GetValue(data)); }
                catch { result.TryAdd(key, "[UNREADABLE]"); }
            }
        }

        private static bool IsSimpleType(Type type)
        {
            if (type.IsPrimitive || type.IsEnum) return true;

            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying is not null) return IsSimpleType(underlying);

            return type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(Guid)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(TimeSpan);
        }
    }
}
