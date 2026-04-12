#nullable enable
// This file IS the source generator, not generated output.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Dragonfire.Logging.Generator
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Well-known type name constants used for symbol look-up.
    // ─────────────────────────────────────────────────────────────────────────────
    internal static class WellKnown
    {
        internal const string ILoggable          = "Dragonfire.Logging.Abstractions.ILoggable";
        internal const string LogAttribute       = "Dragonfire.Logging.Attributes.LogAttribute";
        internal const string LogIgnoreAttribute = "Dragonfire.Logging.Attributes.LogIgnoreAttribute";
        internal const string LogProperty        = "Dragonfire.Logging.Attributes.LogPropertyAttribute";

        // Fully-qualified generated type prefixes used in emitted source
        internal const string LogEntry           = "global::Dragonfire.Logging.Models.LogEntry";
        internal const string LoggingService     = "global::Dragonfire.Logging.Services.IDragonfireLoggingService";
        internal const string FilterService      = "global::Dragonfire.Logging.Services.ILogFilterService";
        internal const string Options            = "global::Dragonfire.Logging.Configuration.DragonfireLoggingOptions";
        internal const string LogLevel           = "global::Microsoft.Extensions.Logging.LogLevel";
        internal const string IServiceCollection = "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";
        internal const string ServiceDescriptor  = "global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor";
        internal const string ActivatorUtilities = "global::Microsoft.Extensions.DependencyInjection.ActivatorUtilities";
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Data models (immutable; safe for incremental caching)
    // ─────────────────────────────────────────────────────────────────────────────

    internal enum MethodKind { Void, SyncReturn, Task, TaskOfT }

    internal sealed class AttributeModel : IEquatable<AttributeModel>
    {
        public string  Level             { get; }
        public string  ExcludeProperties { get; }  // comma-separated, for codegen
        public string  IncludeProperties { get; }
        public int     MaxContentLength  { get; }
        public int     MaxDepth          { get; }
        public bool    LogRequest        { get; }
        public bool    LogResponse       { get; }

        public AttributeModel(string level, string excludeProps, string includeProps,
            int maxLen, int maxDepth, bool logRequest, bool logResponse)
        {
            Level             = level;
            ExcludeProperties = excludeProps;
            IncludeProperties = includeProps;
            MaxContentLength  = maxLen;
            MaxDepth          = maxDepth;
            LogRequest        = logRequest;
            LogResponse       = logResponse;
        }

        public bool Equals(AttributeModel? other) =>
            other is not null
            && Level == other.Level
            && ExcludeProperties == other.ExcludeProperties
            && MaxDepth == other.MaxDepth;

        public override bool Equals(object? obj) => Equals(obj as AttributeModel);
        public override int GetHashCode() => (Level?.GetHashCode() ?? 0) ^ MaxDepth;
    }

    internal sealed class ParameterModel : IEquatable<ParameterModel>
    {
        public string  Name            { get; }
        public string  TypeFQN         { get; }
        public RefKind RefKind         { get; }
        public bool    IsParams        { get; }
        public bool    HasLogProperty  { get; }
        public string? LogPropertyName { get; }  // null → use Name

        public ParameterModel(string name, string typeFQN, RefKind refKind,
            bool isParams, bool hasLogProperty, string? logPropertyName)
        {
            Name            = name;
            TypeFQN         = typeFQN;
            RefKind         = refKind;
            IsParams        = isParams;
            HasLogProperty  = hasLogProperty;
            LogPropertyName = logPropertyName;
        }

        public bool Equals(ParameterModel? other) =>
            other is not null && Name == other.Name && TypeFQN == other.TypeFQN;

        public override bool Equals(object? obj) => Equals(obj as ParameterModel);
        public override int GetHashCode() => (Name?.GetHashCode() ?? 0) ^ (TypeFQN?.GetHashCode() ?? 0);
    }

    internal sealed class MethodModel : IEquatable<MethodModel>
    {
        public string                        Name              { get; }
        public MethodKind                    Kind              { get; }
        public string                        ReturnTypeFQN     { get; }
        public string?                       TaskResultTypeFQN { get; }   // set when Kind == TaskOfT
        public ImmutableArray<ParameterModel> Parameters       { get; }
        public ImmutableArray<string>        TypeParameters    { get; }   // names of generic type params
        public bool                          HasLogIgnore      { get; }
        public AttributeModel?               LogAttr           { get; }

        public MethodModel(string name, MethodKind kind, string returnTypeFQN,
            string? taskResultFQN, ImmutableArray<ParameterModel> parameters,
            ImmutableArray<string> typeParameters, bool hasLogIgnore, AttributeModel? logAttr)
        {
            Name              = name;
            Kind              = kind;
            ReturnTypeFQN     = returnTypeFQN;
            TaskResultTypeFQN = taskResultFQN;
            Parameters        = parameters;
            TypeParameters    = typeParameters;
            HasLogIgnore      = hasLogIgnore;
            LogAttr           = logAttr;
        }

        public bool Equals(MethodModel? other) =>
            other is not null
            && Name == other.Name
            && ReturnTypeFQN == other.ReturnTypeFQN
            && Parameters.Length == other.Parameters.Length;

        public override bool Equals(object? obj) => Equals(obj as MethodModel);
        public override int GetHashCode() => (Name?.GetHashCode() ?? 0) ^ (ReturnTypeFQN?.GetHashCode() ?? 0);
    }

    internal sealed class ServiceModel : IEquatable<ServiceModel>
    {
        public string                      ClassName      { get; }
        public string                      ClassNamespace { get; }
        /// <summary>All non-ILoggable service interfaces (FQN, global:: qualified).</summary>
        public ImmutableArray<string>      InterfacesFQN  { get; }
        /// <summary>Primary interface — first in InterfacesFQN.</summary>
        public string                      PrimaryIfaceFQN => InterfacesFQN[0];
        public ImmutableArray<MethodModel> Methods        { get; }

        public ServiceModel(string className, string classNs,
            ImmutableArray<string> ifacesFQN, ImmutableArray<MethodModel> methods)
        {
            ClassName      = className;
            ClassNamespace = classNs;
            InterfacesFQN  = ifacesFQN;
            Methods        = methods;
        }

        public string ProxyClassName  => $"{ClassName}LoggingProxy";
        public string ProxyNamespace  => $"{ClassNamespace}.Generated";
        public string ProxyFQN        => $"global::{ProxyNamespace}.{ProxyClassName}";

        public bool Equals(ServiceModel? other) =>
            other is not null
            && ClassName == other.ClassName
            && ClassNamespace == other.ClassNamespace;

        public override bool Equals(object? obj) => Equals(obj as ServiceModel);
        public override int GetHashCode() => (ClassName?.GetHashCode() ?? 0) ^ (ClassNamespace?.GetHashCode() ?? 0);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Incremental source generator
    // ─────────────────────────────────────────────────────────────────────────────

    [Generator]
    public sealed class LoggingProxyGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var services = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                    transform: static (ctx, ct)  => ModelExtractor.Extract(ctx, ct))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!);

            context.RegisterSourceOutput(services.Collect(), static (spc, models) =>
            {
                foreach (var model in models)
                {
                    var source = ProxyEmitter.Emit(model);
                    spc.AddSource(
                        $"{model.ClassName}LoggingProxy.g.cs",
                        SourceText.From(source, Encoding.UTF8));
                }

                if (models.Length > 0)
                {
                    var reg = RegistrationEmitter.Emit(models);
                    spc.AddSource(
                        "DragonfireGeneratedExtensions.g.cs",
                        SourceText.From(reg, Encoding.UTF8));
                }
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Model extraction — walks the Roslyn semantic model
    // ─────────────────────────────────────────────────────────────────────────────

    internal static class ModelExtractor
    {
        public static ServiceModel? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
        {
            var classDecl  = (ClassDeclarationSyntax)ctx.Node;
            var classSymbol = ctx.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;

            if (classSymbol is null || classSymbol.IsAbstract || classSymbol.IsGenericType)
                return null;

            // Must implement ILoggable
            if (!ImplementsILoggable(classSymbol)) return null;

            // Collect service interfaces (skip ILoggable itself and system/DI interfaces)
            var serviceIfaces = classSymbol.AllInterfaces
                .Where(i => !IsILoggable(i) && !IsFrameworkInterface(i))
                .ToImmutableArray();

            if (serviceIfaces.IsEmpty) return null;

            // Resolve class-level [Log] attribute
            var classLogAttr = ReadLogAttribute(classSymbol);

            // Collect all unique methods across all service interfaces
            var seen    = new HashSet<string>(StringComparer.Ordinal);
            var methods = new List<MethodModel>();

            foreach (var iface in serviceIfaces)
            {
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    // Skip property accessors, event accessors, static members
                    if (member.MethodKind != Microsoft.CodeAnalysis.MethodKind.Ordinary) continue;

                    // Unique key: name + parameter types (avoids duplicates from multiple interfaces)
                    var key = $"{member.Name}({string.Join(",", member.Parameters.Select(p => p.Type.ToDisplayString()))})";
                    if (!seen.Add(key)) continue;

                    ct.ThrowIfCancellationRequested();

                    // Resolve method-level attribute overrides; fall back to class-level
                    var hasIgnore  = HasAttribute(member, WellKnown.LogIgnoreAttribute);
                    var methodAttr = ReadLogAttribute(member) ?? classLogAttr;

                    var methodModel = BuildMethod(
                        member,
                        ctx.SemanticModel.Compilation,
                        hasIgnore,
                        methodAttr);

                    methods.Add(methodModel);
                }
            }

            var ifacesFQN = serviceIfaces
                .Select(i => i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .ToImmutableArray();

            return new ServiceModel(
                classSymbol.Name,
                classSymbol.ContainingNamespace.ToDisplayString(),
                ifacesFQN,
                methods.ToImmutableArray());
        }

        // ── Symbol helpers ────────────────────────────────────────────────────

        private static bool ImplementsILoggable(INamedTypeSymbol symbol)
            => symbol.AllInterfaces.Any(IsILoggable);

        private static bool IsILoggable(INamedTypeSymbol i)
            => $"{i.ContainingNamespace}.{i.Name}" == WellKnown.ILoggable;

        private static bool IsFrameworkInterface(INamedTypeSymbol i)
        {
            var ns = i.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return ns.StartsWith("System", StringComparison.Ordinal)
                || ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || ns.StartsWith("Dragonfire.Logging", StringComparison.Ordinal);
        }

        private static bool HasAttribute(ISymbol symbol, string fullName)
            => symbol.GetAttributes().Any(a =>
                $"{a.AttributeClass?.ContainingNamespace}.{a.AttributeClass?.Name}Attribute" == fullName
                || $"{a.AttributeClass?.ContainingNamespace}.{a.AttributeClass?.Name}" == fullName);

        // ── [Log] attribute reader ────────────────────────────────────────────

        private static AttributeModel? ReadLogAttribute(ISymbol symbol)
        {
            var attr = symbol.GetAttributes().FirstOrDefault(a =>
                $"{a.AttributeClass?.ContainingNamespace}.{a.AttributeClass?.Name}" == WellKnown.LogAttribute
                || $"{a.AttributeClass?.ContainingNamespace}.{a.AttributeClass?.Name}Attribute" == WellKnown.LogAttribute);

            if (attr is null) return null;

            string level             = "Information";
            string excludeProperties = "null";
            string includeProperties = "null";
            int    maxLen            = 0;
            int    maxDepth          = -1;
            bool   logRequest        = true;
            bool   logResponse       = true;

            foreach (var arg in attr.NamedArguments)
            {
                switch (arg.Key)
                {
                    case "Level":
                        // TypedConstant for enum — use the integer value mapped to name
                        level = arg.Value.Value?.ToString() switch
                        {
                            "0" => "Trace",
                            "1" => "Debug",
                            "2" => "Information",
                            "3" => "Warning",
                            "4" => "Error",
                            "5" => "Critical",
                            _   => "Information"
                        };
                        break;
                    case "MaxContentLength": maxLen      = (int)(arg.Value.Value ?? 0);      break;
                    case "MaxDepth":         maxDepth    = (int)(arg.Value.Value ?? -1);     break;
                    case "LogRequest":       logRequest  = (bool)(arg.Value.Value ?? true);  break;
                    case "LogResponse":      logResponse = (bool)(arg.Value.Value ?? true);  break;
                    case "ExcludeProperties":
                        excludeProperties = BuildStringArrayLiteral(arg.Value);
                        break;
                    case "IncludeProperties":
                        includeProperties = BuildStringArrayLiteral(arg.Value);
                        break;
                }
            }

            return new AttributeModel(level, excludeProperties, includeProperties,
                maxLen, maxDepth, logRequest, logResponse);
        }

        private static string BuildStringArrayLiteral(TypedConstant tc)
        {
            if (tc.IsNull || tc.Values.IsEmpty) return "null";
            var items = tc.Values.Select(v => $"\"{v.Value}\"");
            return $"new string[] {{ {string.Join(", ", items)} }}";
        }

        // ── Method model builder ──────────────────────────────────────────────

        private static MethodModel BuildMethod(
            IMethodSymbol method,
            Compilation   compilation,
            bool          hasIgnore,
            AttributeModel? logAttr)
        {
            var kind              = GetMethodKind(method, compilation);
            var returnTypeFQN     = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string? taskResultFQN = null;

            if (kind == MethodKind.TaskOfT
                && method.ReturnType is INamedTypeSymbol named
                && !named.TypeArguments.IsEmpty)
            {
                taskResultFQN = named.TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }

            var typeParams = method.TypeParameters
                .Select(tp => tp.Name)
                .ToImmutableArray();

            var parameters = method.Parameters
                .Select(BuildParameter)
                .ToImmutableArray();

            return new MethodModel(
                method.Name, kind, returnTypeFQN, taskResultFQN,
                parameters, typeParams, hasIgnore, logAttr);
        }

        private static MethodKind GetMethodKind(IMethodSymbol method, Compilation compilation)
        {
            if (method.ReturnsVoid) return MethodKind.Void;

            var taskSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            var taskOfT    = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");

            if (SymbolEqualityComparer.Default.Equals(method.ReturnType, taskSymbol))
                return MethodKind.Task;

            if (method.ReturnType is INamedTypeSymbol nt && nt.IsGenericType
                && SymbolEqualityComparer.Default.Equals(nt.OriginalDefinition, taskOfT))
                return MethodKind.TaskOfT;

            return MethodKind.SyncReturn;
        }

        private static ParameterModel BuildParameter(IParameterSymbol param)
        {
            var lpAttr = param.GetAttributes().FirstOrDefault(a =>
                $"{a.AttributeClass?.ContainingNamespace}.{a.AttributeClass?.Name}" == WellKnown.LogProperty
                || $"{a.AttributeClass?.ContainingNamespace}.{a.AttributeClass?.Name}Attribute" == WellKnown.LogProperty);

            bool   hasLogProp = lpAttr is not null;
            string? lpName    = null;

            if (hasLogProp && lpAttr!.ConstructorArguments.Length > 0)
                lpName = lpAttr.ConstructorArguments[0].Value as string;

            return new ParameterModel(
                param.Name,
                param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                param.RefKind,
                param.IsParams,
                hasLogProp,
                lpName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Proxy class emitter
    // ─────────────────────────────────────────────────────────────────────────────

    internal static class ProxyEmitter
    {
        public static string Emit(ServiceModel model)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Generated by Dragonfire.Logging.Generator");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine($"namespace {model.ProxyNamespace}");
            sb.AppendLine("{");

            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// Compile-time logging proxy for <see cref=\"{model.ClassName}\"/>.");
            sb.AppendLine($"    /// Generated by Dragonfire.Logging.Generator — zero reflection at runtime.");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    [global::System.CodeDom.Compiler.GeneratedCode(\"Dragonfire.Logging.Generator\", \"1.0.0\")]");
            sb.AppendLine($"    [global::System.Diagnostics.DebuggerNonUserCode]");

            // Implements all service interfaces
            var ifaceList = string.Join(", ", model.InterfacesFQN);
            sb.AppendLine($"    internal sealed class {model.ProxyClassName} : {ifaceList}");
            sb.AppendLine("    {");

            EmitFields(sb);
            EmitConstructor(sb, model);

            foreach (var method in model.Methods)
                EmitMethod(sb, model, method);

            sb.AppendLine("    }"); // class
            sb.AppendLine("}");     // namespace

            return sb.ToString();
        }

        private static void EmitFields(StringBuilder sb)
        {
            sb.AppendLine($"        private readonly {WellKnown.LoggingService} _loggingService;");
            sb.AppendLine($"        private readonly {WellKnown.FilterService} _filterService;");
            sb.AppendLine($"        private readonly {WellKnown.Options} _options;");

            // _inner fields for each interface
            sb.AppendLine();
        }

        private static void EmitConstructor(StringBuilder sb, ServiceModel model)
        {
            // One _inner field per unique interface
            foreach (var iface in model.InterfacesFQN)
            {
                var fieldName = InnerFieldName(iface);
                sb.AppendLine($"        private readonly {iface} {fieldName};");
            }

            sb.AppendLine();

            // Constructor — accepts primary interface (all others cast from it when needed,
            // but for simplicity we take one parameter and cast to each interface)
            // If a class implements multiple interfaces they all come from the same object.
            var primaryIface = model.PrimaryIfaceFQN;
            sb.AppendLine($"        public {model.ProxyClassName}(");
            sb.AppendLine($"            {primaryIface} inner,");
            sb.AppendLine($"            {WellKnown.LoggingService} loggingService,");
            sb.AppendLine($"            {WellKnown.FilterService} filterService,");
            sb.AppendLine($"            {WellKnown.Options} options)");
            sb.AppendLine("        {");

            foreach (var iface in model.InterfacesFQN)
            {
                var fieldName = InnerFieldName(iface);
                sb.AppendLine($"            {fieldName} = ({iface})inner;");
            }

            sb.AppendLine($"            _loggingService = loggingService ?? throw new global::System.ArgumentNullException(nameof(loggingService));");
            sb.AppendLine($"            _filterService  = filterService  ?? throw new global::System.ArgumentNullException(nameof(filterService));");
            sb.AppendLine($"            _options        = options        ?? throw new global::System.ArgumentNullException(nameof(options));");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // ── Method emitter ────────────────────────────────────────────────────

        private static void EmitMethod(StringBuilder sb, ServiceModel model, MethodModel method)
        {
            // Determine which _inner field to call (the one for the interface owning this method)
            // For simplicity, use the primary _inner field (all interfaces share the same object)
            var innerField = InnerFieldName(model.PrimaryIfaceFQN);

            // Signature
            var asyncKeyword = method.Kind is MethodKind.Task or MethodKind.TaskOfT ? "async " : "";
            var typeParams   = method.TypeParameters.IsEmpty
                ? ""
                : $"<{string.Join(", ", method.TypeParameters)}>";

            var paramList = string.Join(", ", method.Parameters.Select(FormatParam));

            sb.AppendLine($"        public {asyncKeyword}{method.ReturnTypeFQN} {method.Name}{typeParams}({paramList})");
            sb.AppendLine("        {");

            // Generic methods or [LogIgnore] → simple pass-through
            if (method.HasLogIgnore || !method.TypeParameters.IsEmpty)
            {
                EmitPassThrough(sb, method, innerField);
                sb.AppendLine("        }");
                sb.AppendLine();
                return;
            }

            // Full interception
            switch (method.Kind)
            {
                case MethodKind.Void:       EmitVoidMethod(sb, model, method, innerField);       break;
                case MethodKind.SyncReturn: EmitSyncReturnMethod(sb, model, method, innerField); break;
                case MethodKind.Task:       EmitTaskMethod(sb, model, method, innerField);       break;
                case MethodKind.TaskOfT:    EmitTaskOfTMethod(sb, model, method, innerField);    break;
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void EmitPassThrough(StringBuilder sb, MethodModel method, string innerField)
        {
            var args    = CallArgs(method.Parameters);
            var awaitKw = method.Kind is MethodKind.Task or MethodKind.TaskOfT ? "await " : "";
            var retKw   = method.Kind == MethodKind.Void ? "" : "return ";
            sb.AppendLine($"            {retKw}{awaitKw}{innerField}.{method.Name}({args});");
        }

        private static void EmitVoidMethod(StringBuilder sb, ServiceModel model, MethodModel method, string innerField)
        {
            EmitEntryInit(sb, model, method, "            ");
            EmitArgLogging(sb, method, "            ");

            sb.AppendLine("            var __sw = global::System.Diagnostics.Stopwatch.StartNew();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine($"                {innerField}.{method.Name}({CallArgs(method.Parameters)});");
            sb.AppendLine("            }");
            EmitCatchBlock(sb, "            ");
            EmitFinallySync(sb, "            ");
        }

        private static void EmitSyncReturnMethod(StringBuilder sb, ServiceModel model, MethodModel method, string innerField)
        {
            EmitEntryInit(sb, model, method, "            ");
            EmitArgLogging(sb, method, "            ");

            sb.AppendLine($"            {method.ReturnTypeFQN} __result = default!;");
            sb.AppendLine("            var __sw = global::System.Diagnostics.Stopwatch.StartNew();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine($"                __result = {innerField}.{method.Name}({CallArgs(method.Parameters)});");
            EmitResultCapture(sb, method, "                ");
            sb.AppendLine("            }");
            EmitCatchBlock(sb, "            ");
            EmitFinallySync(sb, "            ");
            sb.AppendLine("            return __result;");
        }

        private static void EmitTaskMethod(StringBuilder sb, ServiceModel model, MethodModel method, string innerField)
        {
            EmitEntryInit(sb, model, method, "            ");
            EmitArgLogging(sb, method, "            ");

            sb.AppendLine("            var __sw = global::System.Diagnostics.Stopwatch.StartNew();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine($"                await {innerField}.{method.Name}({CallArgs(method.Parameters)}).ConfigureAwait(false);");
            sb.AppendLine("            }");
            EmitCatchBlock(sb, "            ");
            EmitFinallyAsync(sb, "            ");
        }

        private static void EmitTaskOfTMethod(StringBuilder sb, ServiceModel model, MethodModel method, string innerField)
        {
            EmitEntryInit(sb, model, method, "            ");
            EmitArgLogging(sb, method, "            ");

            sb.AppendLine($"            {method.TaskResultTypeFQN} __result = default!;");
            sb.AppendLine("            var __sw = global::System.Diagnostics.Stopwatch.StartNew();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine($"                __result = await {innerField}.{method.Name}({CallArgs(method.Parameters)}).ConfigureAwait(false);");
            EmitResultCapture(sb, method, "                ");
            sb.AppendLine("            }");
            EmitCatchBlock(sb, "            ");
            EmitFinallyAsync(sb, "            ");
            sb.AppendLine("            return __result;");
        }

        // ── Entry initialisation ──────────────────────────────────────────────

        private static void EmitEntryInit(StringBuilder sb, ServiceModel model, MethodModel method, string indent)
        {
            var level = method.LogAttr is not null
                ? $"{WellKnown.LogLevel}.{method.LogAttr.Level}"
                : "_options.DefaultLogLevel";

            sb.AppendLine($"{indent}var __entry = new {WellKnown.LogEntry}");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    ServiceName = \"{model.ClassName}\",");
            sb.AppendLine($"{indent}    MethodName  = \"{method.Name}\",");
            sb.AppendLine($"{indent}    Level       = {level},");
            sb.AppendLine($"{indent}}};");
        }

        // ── Argument & named-property logging ─────────────────────────────────

        private static void EmitArgLogging(StringBuilder sb, MethodModel method, string indent)
        {
            if (method.Parameters.IsEmpty) return;

            // Resolve effective maxDepth
            var maxDepthExpr = method.LogAttr is not null && method.LogAttr.MaxDepth >= 0
                ? method.LogAttr.MaxDepth.ToString()
                : "_options.DefaultMaxDepth";

            var excl = method.LogAttr?.ExcludeProperties ?? "null";
            var incl = method.LogAttr?.IncludeProperties ?? "null";
            var maxLen = method.LogAttr?.MaxContentLength is int ml && ml > 0
                ? ml.ToString()
                : "_options.DefaultMaxContentLength";

            // Build argument array literal (skip ref/out — use null placeholder)
            var argItems = method.Parameters.Select(p =>
                p.RefKind is RefKind.Out ? "null" : $"(object?){p.Name}");
            var argArray = $"new object?[] {{ {string.Join(", ", argItems)} }}";

            // Capture LogRequest
            var shouldLogArgs = method.LogAttr?.LogRequest ?? true;
            if (!shouldLogArgs)
            {
                // Still collect named properties even if args not logged
            }
            else
            {
                sb.AppendLine($"{indent}try");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    __entry.MethodArguments = _filterService.FilterData(");
                sb.AppendLine($"{indent}        {argArray},");
                sb.AppendLine($"{indent}        {excl}, {incl}, {maxLen}, {maxDepthExpr});");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}catch {{ }}");
            }

            // Collect [LogProperty] named properties — always, regardless of LogRequest flag
            var hasParamLogProps = method.Parameters.Any(p => p.HasLogProperty);
            var hasObjectLogProps = method.Parameters
                .Any(p => p.RefKind == RefKind.None && !IsSimplePrimitive(p.TypeFQN));

            if (hasParamLogProps || hasObjectLogProps)
            {
                sb.AppendLine($"{indent}// Promote [LogProperty] values to named structured-log properties");
                sb.AppendLine($"{indent}try");
                sb.AppendLine($"{indent}{{");

                if (hasParamLogProps)
                {
                    sb.AppendLine($"{indent}    __entry.NamedProperties ??= new global::System.Collections.Generic.Dictionary<string, object?>(global::System.StringComparer.OrdinalIgnoreCase);");
                    foreach (var p in method.Parameters.Where(p => p.HasLogProperty))
                    {
                        var key = p.LogPropertyName ?? p.Name;
                        sb.AppendLine($"{indent}    __entry.NamedProperties.TryAdd(\"{key}\", {p.Name});");
                    }
                }

                // Extract from non-primitive argument objects via ILogFilterService
                foreach (var p in method.Parameters
                    .Where(p => !p.HasLogProperty && p.RefKind == RefKind.None && !IsSimplePrimitive(p.TypeFQN)))
                {
                    sb.AppendLine($"{indent}    {{");
                    sb.AppendLine($"{indent}        var __n = _filterService.ExtractNamedProperties({p.Name});");
                    sb.AppendLine($"{indent}        if (__n.Count > 0)");
                    sb.AppendLine($"{indent}        {{");
                    sb.AppendLine($"{indent}            __entry.NamedProperties ??= new global::System.Collections.Generic.Dictionary<string, object?>(global::System.StringComparer.OrdinalIgnoreCase);");
                    sb.AppendLine($"{indent}            foreach (var __kv in __n)");
                    sb.AppendLine($"{indent}                __entry.NamedProperties.TryAdd(__kv.Key, __kv.Value);");
                    sb.AppendLine($"{indent}        }}");
                    sb.AppendLine($"{indent}    }}");
                }

                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}catch {{ }}");
            }
        }

        // ── Result capture ────────────────────────────────────────────────────

        private static void EmitResultCapture(StringBuilder sb, MethodModel method, string indent)
        {
            var shouldLogResult = method.LogAttr?.LogResponse ?? true;
            if (!shouldLogResult) return;

            var maxDepthExpr = method.LogAttr is not null && method.LogAttr.MaxDepth >= 0
                ? method.LogAttr.MaxDepth.ToString()
                : "_options.DefaultMaxDepth";

            var excl   = method.LogAttr?.ExcludeProperties ?? "null";
            var incl   = method.LogAttr?.IncludeProperties ?? "null";
            var maxLen = method.LogAttr?.MaxContentLength is int ml && ml > 0
                ? ml.ToString()
                : "_options.DefaultMaxContentLength";

            sb.AppendLine($"{indent}try");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    __entry.MethodResult = _filterService.FilterData(");
            sb.AppendLine($"{indent}        __result, {excl}, {incl}, {maxLen}, {maxDepthExpr});");

            // Extract [LogProperty] from result object
            if (!IsSimplePrimitive(method.TaskResultTypeFQN ?? method.ReturnTypeFQN))
            {
                sb.AppendLine($"{indent}    var __rn = _filterService.ExtractNamedProperties(__result);");
                sb.AppendLine($"{indent}    if (__rn.Count > 0)");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        __entry.NamedProperties ??= new global::System.Collections.Generic.Dictionary<string, object?>(global::System.StringComparer.OrdinalIgnoreCase);");
                sb.AppendLine($"{indent}        foreach (var __kv in __rn)");
                sb.AppendLine($"{indent}            __entry.NamedProperties.TryAdd(__kv.Key, __kv.Value);");
                sb.AppendLine($"{indent}    }}");
            }

            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}catch {{ }}");
        }

        // ── Catch / finally ───────────────────────────────────────────────────

        private static void EmitCatchBlock(StringBuilder sb, string indent)
        {
            sb.AppendLine($"{indent}catch (global::System.Exception __ex)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    __entry.IsError      = true;");
            sb.AppendLine($"{indent}    __entry.Level        = {WellKnown.LogLevel}.Error;");
            sb.AppendLine($"{indent}    __entry.ErrorMessage = __ex.Message;");
            sb.AppendLine($"{indent}    if (_options.IncludeStackTraceOnError)");
            sb.AppendLine($"{indent}        __entry.StackTrace = __ex.StackTrace;");
            sb.AppendLine($"{indent}    throw;");
            sb.AppendLine($"{indent}}}");
        }

        private static void EmitFinallySync(StringBuilder sb, string indent)
        {
            sb.AppendLine($"{indent}finally");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    __sw.Stop();");
            sb.AppendLine($"{indent}    __entry.ElapsedMilliseconds = __sw.ElapsedMilliseconds;");
            sb.AppendLine($"{indent}    _loggingService.Log(__entry);");
            sb.AppendLine($"{indent}}}");
        }

        private static void EmitFinallyAsync(StringBuilder sb, string indent)
        {
            sb.AppendLine($"{indent}finally");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    __sw.Stop();");
            sb.AppendLine($"{indent}    __entry.ElapsedMilliseconds = __sw.ElapsedMilliseconds;");
            sb.AppendLine($"{indent}    await _loggingService.LogAsync(__entry).ConfigureAwait(false);");
            sb.AppendLine($"{indent}}}");
        }

        // ── Formatting helpers ────────────────────────────────────────────────

        private static string FormatParam(ParameterModel p)
        {
            var refMod = p.RefKind switch
            {
                RefKind.Ref  => "ref ",
                RefKind.Out  => "out ",
                RefKind.In   => "in ",
                _            => ""
            };
            var paramsMod = p.IsParams ? "params " : "";
            return $"{paramsMod}{refMod}{p.TypeFQN} {p.Name}";
        }

        private static string CallArgs(ImmutableArray<ParameterModel> parameters)
        {
            return string.Join(", ", parameters.Select(p =>
            {
                var refMod = p.RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.In  => "in ",
                    _           => ""
                };
                return $"{refMod}{p.Name}";
            }));
        }

        private static string InnerFieldName(string ifaceFQN)
        {
            // e.g. "global::MyApp.IOrderService" → "_innerIOrderService"
            var parts = ifaceFQN.Split('.');
            return "_inner" + parts[parts.Length - 1].Replace(">", "").Replace("<", "");
        }

        /// <summary>
        /// Returns true for types that cannot possibly carry [LogProperty] attributes
        /// (primitives, strings, Guids, date/time types) — skip ExtractNamedProperties call.
        /// </summary>
        private static bool IsSimplePrimitive(string typeFQN)
        {
            return typeFQN is "global::System.String" or "string"
                or "global::System.Int32"  or "int"
                or "global::System.Int64"  or "long"
                or "global::System.Boolean" or "bool"
                or "global::System.Guid"
                or "global::System.DateTime"
                or "global::System.DateTimeOffset"
                or "global::System.Decimal" or "decimal"
                or "global::System.Double"  or "double"
                or "global::System.Single"  or "float"
                or "global::System.Threading.CancellationToken";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // DI registration extension emitter
    // ─────────────────────────────────────────────────────────────────────────────

    internal static class RegistrationEmitter
    {
        public static string Emit(ImmutableArray<ServiceModel> models)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Generated by Dragonfire.Logging.Generator");
            sb.AppendLine("#nullable enable");
            // GetRequiredService<T> is an extension method — needs the namespace in scope
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine();
            sb.AppendLine("namespace Dragonfire.Logging.Generated");
            sb.AppendLine("{");

            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Registers compile-time logging proxies for all <c>ILoggable</c> services");
            sb.AppendLine("    /// discovered in this compilation unit.");
            sb.AppendLine("    /// Call <c>services.AddDragonfireGeneratedLogging()</c> in your startup");
            sb.AppendLine("    /// <b>after</b> registering all services and <b>after</b>");
            sb.AppendLine("    /// <c>AddDragonfireLogging()</c>.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"Dragonfire.Logging.Generator\", \"1.0.0\")]");
            sb.AppendLine($"    public static class DragonfireGeneratedExtensions");
            sb.AppendLine("    {");

            // ── AddDragonfireGeneratedLogging ────────────────────────────────
            sb.AppendLine($"        public static {WellKnown.IServiceCollection} AddDragonfireGeneratedLogging(");
            sb.AppendLine($"            this {WellKnown.IServiceCollection} services)");
            sb.AppendLine("        {");

            foreach (var model in models)
            {
                var primaryIface = model.PrimaryIfaceFQN;

                // One Decorate call per interface the service implements
                foreach (var iface in model.InterfacesFQN)
                {
                    sb.AppendLine($"            // {model.ClassName} → {model.ProxyClassName}");
                    sb.AppendLine($"            DecorateService(");
                    sb.AppendLine($"                services,");
                    sb.AppendLine($"                typeof({iface}),");
                    sb.AppendLine($"                (inner, sp) => new {model.ProxyFQN}(");
                    sb.AppendLine($"                    ({primaryIface})inner,");
                    sb.AppendLine($"                    sp.GetRequiredService<{WellKnown.LoggingService}>(),");
                    sb.AppendLine($"                    sp.GetRequiredService<{WellKnown.FilterService}>(),");
                    sb.AppendLine($"                    sp.GetRequiredService<{WellKnown.Options}>()));");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("            return services;");
            sb.AppendLine("        }");
            sb.AppendLine();

            // ── Inline Decorate helper (no Scrutor dependency) ───────────────
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Inline decoration helper — replaces the last registered descriptor");
            sb.AppendLine("        /// for <paramref name=\"serviceType\"/> with a factory that wraps it.");
            sb.AppendLine("        /// Original lifetime (Singleton/Scoped/Transient) is preserved.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        private static void DecorateService(");
            sb.AppendLine($"            {WellKnown.IServiceCollection} services,");
            sb.AppendLine("            global::System.Type serviceType,");
            sb.AppendLine("            global::System.Func<object, global::System.IServiceProvider, object> decorator)");
            sb.AppendLine("        {");
            sb.AppendLine("            for (int i = services.Count - 1; i >= 0; i--)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (services[i].ServiceType != serviceType) continue;");
            sb.AppendLine();
            sb.AppendLine("                var d = services[i];");
            sb.AppendLine($"                services[i] = {WellKnown.ServiceDescriptor}.Describe(");
            sb.AppendLine("                    serviceType,");
            sb.AppendLine("                    sp => decorator(ResolveInner(sp, d), sp),");
            sb.AppendLine("                    d.Lifetime);");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
            sb.AppendLine("            // Service not found — silently skip (may not be registered yet)");
            sb.AppendLine("        }");
            sb.AppendLine();

            // ── ResolveInner ─────────────────────────────────────────────────
            sb.AppendLine("        private static object ResolveInner(");
            sb.AppendLine("            global::System.IServiceProvider sp,");
            sb.AppendLine($"            {WellKnown.ServiceDescriptor} d)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (d.ImplementationInstance != null) return d.ImplementationInstance;");
            sb.AppendLine("            if (d.ImplementationFactory != null) return d.ImplementationFactory(sp);");
            sb.AppendLine($"            return {WellKnown.ActivatorUtilities}.CreateInstance(sp, d.ImplementationType!);");
            sb.AppendLine("        }");

            sb.AppendLine("    }"); // class
            sb.AppendLine("}");     // namespace

            return sb.ToString();
        }
    }
}
