using System;
using System.Linq;
using System.Text.RegularExpressions;
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
            MaxDepth = 10,
            NullValueHandling = NullValueHandling.Ignore
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
            int maxLength = 0)
        {
            if (data is null) return null;

            try
            {
                var json = JsonConvert.SerializeObject(data, SerializerSettings);
                var token = JToken.Parse(json);

                FilterToken(token,
                    excludeProperties ?? Array.Empty<string>(),
                    includeProperties ?? Array.Empty<string>());

                var result = token.ToString(Formatting.None);

                if (maxLength > 0 && result.Length > maxLength)
                    result = result[..maxLength] + "...[TRUNCATED]";

                // Deserialise without a target type — returns JObject/JArray/primitive.
                // This avoids type-cast failures for anonymous types, object[], etc.
                // and lets structured-logging sinks (Serilog, etc.) handle it natively.
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

        // ── Private helpers ──────────────────────────────────────────────────

        private void FilterToken(JToken token, string[] exclude, string[] include)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    var obj = (JObject)token;

                    // Remove properties that fail the include/exclude check or are sensitive fields.
                    var toRemove = obj.Properties()
                        .Where(p => !ShouldLogProperty(p.Name, exclude, include)
                                    || _policy.SensitiveFields.Contains(p.Name))
                        .ToList();

                    foreach (var prop in toRemove)
                        prop.Remove();

                    // Recurse into remaining properties.
                    foreach (var prop in obj.Properties())
                        FilterToken(prop.Value, exclude, include);

                    break;

                case JTokenType.Array:
                    foreach (var item in token.Children())
                        FilterToken(item, exclude, include);
                    break;

                case JTokenType.String:
                    var str = token.Value<string>();
                    if (!string.IsNullOrEmpty(str))
                    {
                        var redacted = ApplyPatternRedaction(str);
                        if (redacted != str)
                            token.Replace(redacted);
                    }
                    break;
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
    }
}
