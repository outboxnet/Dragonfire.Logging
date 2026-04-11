using Dragonfire.Logging.Models;

namespace Dragonfire.Logging.Services
{
    /// <summary>
    /// Sanitises objects before they are stored in a <see cref="LogEntry"/>.
    /// Applies property exclusion/inclusion rules, sensitive-field redaction,
    /// pattern-based redaction (credit cards, SSNs, JWTs, emails, phones),
    /// and optional length truncation.
    /// </summary>
    public interface ILogFilterService
    {
        /// <summary>
        /// Serialise <paramref name="data"/>, apply all filtering and redaction rules,
        /// then return the sanitised value as a plain <c>object</c> suitable for structured logging.
        /// Returns <c>null</c> when <paramref name="data"/> is <c>null</c>.
        /// </summary>
        object? FilterData(
            object? data,
            string[]? excludeProperties = null,
            string[]? includeProperties = null,
            int maxLength = 0);

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
    }
}
