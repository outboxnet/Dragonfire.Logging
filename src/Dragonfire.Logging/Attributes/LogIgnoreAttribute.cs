using System;

namespace Dragonfire.Logging.Attributes
{
    /// <summary>
    /// Decorates a property to exclude it from all log output, regardless of
    /// any <see cref="LogAttribute"/> configuration on the enclosing type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class LogIgnoreAttribute : Attribute
    {
        /// <summary>Human-readable reason recorded in source code (not at runtime).</summary>
        public string Reason { get; }

        public LogIgnoreAttribute(string reason = "Sensitive data")
        {
            Reason = reason;
        }
    }
}
