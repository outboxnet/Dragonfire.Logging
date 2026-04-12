using System.Collections.Generic;
using Dragonfire.Logging.Models;

namespace Dragonfire.Logging.Services
{
    /// <summary>
    /// Sanitises objects before they are stored in a <see cref="LogEntry"/>.
    /// Applies property exclusion/inclusion rules, sensitive-field redaction,
    /// pattern-based redaction (credit cards, SSNs, JWTs, emails, phones),
    /// depth-based truncation, and optional length truncation.
    /// </summary>
    public interface ILogFilterService
    {
        /// <summary>
        /// Serialise <paramref name="data"/>, apply all filtering and redaction rules,
        /// then return the sanitised value as a plain <c>object</c> suitable for structured logging.
        /// Returns <c>null</c> when <paramref name="data"/> is <c>null</c>.
        /// </summary>
        /// <param name="data">Object to serialise and filter.</param>
        /// <param name="excludeProperties">Property names to suppress.</param>
        /// <param name="includeProperties">When non-empty only these properties survive.</param>
        /// <param name="maxLength">Truncate the final JSON string to this many characters (0 = unlimited).</param>
        /// <param name="maxDepth">
        /// Maximum object nesting depth to preserve.
        /// <c>0</c> = unlimited; <c>1</c> = top-level scalars only (nested objects become
        /// <c>[N fields]</c>, arrays become <c>[N items]</c>).
        /// The caller is responsible for resolving the effective depth from
        /// <c>[LogAttribute].MaxDepth</c> and <c>DragonfireLoggingOptions.DefaultMaxDepth</c>.
        /// </param>
        object? FilterData(
            object? data,
            string[]? excludeProperties = null,
            string[]? includeProperties = null,
            int maxLength = 0,
            int maxDepth = 0);

        /// <summary>
        /// Apply pattern-based redaction and optional length truncation to a raw string
        /// (e.g. a serialised request body). Returns the input unchanged when it is null or empty.
        /// </summary>
        string? FilterString(string? data, int maxLength = 0);

        /// <summary>
        /// Decides whether a property named <paramref name="propertyName"/> should be
        /// included in the log output given the active include/exclude lists.
        /// </summary>
        bool ShouldLogProperty(string propertyName, string[]? excludeProperties, string[]? includeProperties);

        /// <summary>
        /// Scans <paramref name="data"/> (and its elements if it is a collection) for
        /// properties decorated with <c>[LogProperty]</c> and returns them as a
        /// name → value dictionary. Values are raw (not serialised).
        ///
        /// Use this to promote individual DTO fields to first-class structured-log
        /// properties without burying them inside the serialised payload.
        /// </summary>
        Dictionary<string, object?> ExtractNamedProperties(object? data);
    }
}
