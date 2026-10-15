using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length != 3 || args[0] is not ("capture" or "check" or "self-test"))
{
    Console.Error.WriteLine("usage: Lazily.ApiCompat <capture|check|self-test> <manifest.json> <assembly.dll>");
    return 2;
}

var options = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
};
options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

var manifestPath = Path.GetFullPath(args[1]);
var assemblyPath = Path.GetFullPath(args[2]);
var manifest = JsonSerializer.Deserialize<ApiManifest>(File.ReadAllText(manifestPath), options)
    ?? throw new InvalidOperationException($"Could not parse {manifestPath}");

var errors = ValidateManifest(manifest, requireApi: args[0] != "capture");
if (errors.Count > 0)
{
    foreach (var error in errors) Console.Error.WriteLine($"manifest: {error}");
    return 1;
}

using var loadContext = new InspectionLoadContext(assemblyPath);
var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
var current = Capture(assembly, manifest.Types, out var captureErrors);
if (captureErrors.Count > 0)
{
    foreach (var error in captureErrors) Console.Error.WriteLine($"assembly: {error}");
    return 1;
}

var assemblyName = assembly.GetName().Name;
if (!StringComparer.Ordinal.Equals(assemblyName, manifest.Assembly))
{
    Console.Error.WriteLine($"assembly: expected '{manifest.Assembly}', got '{assemblyName}'");
    return 1;
}

var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .Where(attribute => attribute.Value is not null)
    .ToDictionary(attribute => attribute.Key, attribute => attribute.Value!, StringComparer.Ordinal);
foreach (var expected in manifest.AssemblyMetadata)
{
    if (!metadata.TryGetValue(expected.Key, out var actual) || actual != expected.Value)
    {
        Console.Error.WriteLine(
            $"assembly metadata: expected {expected.Key}={expected.Value}, got {actual ?? "<missing>"}");
        return 1;
    }
}

if (args[0] == "capture")
{
    manifest.Api = current;
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options) + Environment.NewLine);
    Console.WriteLine($"captured {manifest.Api.Count} normalized entries in {manifestPath}");
    return 0;
}

if (args[0] == "self-test")
{
    var mutated = manifest.Api.ToList();
    mutated[0] += " [negative-mutation]";
    var mutationErrors = Compare(current, mutated, manifest.Comparison);
    if (mutationErrors.Count == 0)
    {
        Console.Error.WriteLine("negative self-test FAILED: a mutated snapshot was accepted");
        return 1;
    }
    Console.WriteLine(
        $"negative self-test OK: {manifest.ProfileId} rejected a mutated API snapshot ({mutationErrors.Count} mismatch(es))");
    return 0;
}

var comparisonErrors = Compare(current, manifest.Api, manifest.Comparison);
if (comparisonErrors.Count > 0)
{
    foreach (var error in comparisonErrors) Console.Error.WriteLine(error);
    return 1;
}

Console.WriteLine(
    $"API compatibility OK: {manifest.ProfileId}; {manifest.Api.Count} normalized entries; {manifest.Comparison}");
return 0;

static List<string> ValidateManifest(ApiManifest manifest, bool requireApi)
{
    var errors = new List<string>();
    if (manifest.SchemaVersion != 1) errors.Add("schema_version must be 1");
    if (string.IsNullOrWhiteSpace(manifest.ProfileId)) errors.Add("profile_id is required");
    if (string.IsNullOrWhiteSpace(manifest.Assembly)) errors.Add("assembly is required");
    if (manifest.Comparison is not ("exact" or "subset")) errors.Add("comparison must be exact or subset");
    if (manifest.Types.Count == 0) errors.Add("types must not be empty");
    if (manifest.Types.Count != manifest.Types.Distinct(StringComparer.Ordinal).Count())
        errors.Add("types must be unique");
    if (requireApi && manifest.Api.Count == 0) errors.Add("api must not be empty");
    if (!manifest.Api.SequenceEqual(manifest.Api.Order(StringComparer.Ordinal)))
        errors.Add("api entries must be ordinal-sorted");
    if (manifest.Api.Count != manifest.Api.Distinct(StringComparer.Ordinal).Count())
        errors.Add("api entries must be unique");

    var policy = manifest.DeprecationPolicy;
    if (policy.MinimumCompatibleGenerations < 2)
        errors.Add("deprecation_policy.minimum_compatible_generations must be at least 2");
    if (policy.MinimumDays < 90)
        errors.Add("deprecation_policy.minimum_days must be at least 90");
    var requiredFields = new[]
    {
        "introduced_generation", "deprecated_since_generation", "replacement",
        "deprecated_on", "earliest_removal_generation", "earliest_removal_on",
    };
    foreach (var field in requiredFields)
    {
        if (!policy.RequiredFields.Contains(field, StringComparer.Ordinal))
            errors.Add($"deprecation_policy.required_fields is missing {field}");
    }

    if (manifest.Symbols.Count == 0) errors.Add("symbols must not be empty");
    if (manifest.Symbols.Select(symbol => symbol.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Symbols.Count)
        errors.Add("symbol ids must be unique");
    foreach (var symbol in manifest.Symbols)
    {
        var lifecycle = symbol.Lifecycle;
        if (string.IsNullOrWhiteSpace(symbol.Id)) errors.Add("symbol id is required");
        if (lifecycle.IntroducedGeneration < 1)
            errors.Add($"{symbol.Id}: introduced_generation must be positive");
        if (lifecycle.DeprecatedSinceGeneration is null)
        {
            if (lifecycle.Replacement is not null || lifecycle.DeprecatedOn is not null ||
                lifecycle.EarliestRemovalGeneration is not null || lifecycle.EarliestRemovalOn is not null)
                errors.Add($"{symbol.Id}: active symbols must keep all deprecation fields null");
            continue;
        }

        if (string.IsNullOrWhiteSpace(lifecycle.Replacement))
            errors.Add($"{symbol.Id}: deprecated symbols require replacement");
        if (lifecycle.DeprecatedOn is null) errors.Add($"{symbol.Id}: deprecated symbols require deprecated_on");
        if (lifecycle.EarliestRemovalGeneration is null)
            errors.Add($"{symbol.Id}: deprecated symbols require earliest_removal_generation");
        else if (lifecycle.EarliestRemovalGeneration <
                 lifecycle.DeprecatedSinceGeneration + policy.MinimumCompatibleGenerations)
            errors.Add($"{symbol.Id}: earliest removal violates the generation compatibility window");
        if (lifecycle.EarliestRemovalOn is null)
            errors.Add($"{symbol.Id}: deprecated symbols require earliest_removal_on");
        else if (lifecycle.DeprecatedOn is not null &&
                 lifecycle.EarliestRemovalOn.Value.DayNumber - lifecycle.DeprecatedOn.Value.DayNumber < policy.MinimumDays)
            errors.Add($"{symbol.Id}: earliest removal violates the {policy.MinimumDays}-day compatibility window");
    }
    return errors;
}

static List<string> Capture(Assembly assembly, IReadOnlyList<string> typeNames, out List<string> errors)
{
    errors = [];
    var entries = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var typeName in typeNames)
    {
        var type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (type is null)
        {
            errors.Add($"required public type '{typeName}' is missing");
            continue;
        }
        if (!(type.IsPublic || type.IsNestedPublic))
        {
            errors.Add($"required type '{typeName}' is not public");
            continue;
        }

        entries.Add(TypeDeclaration(type));
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                                   BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var constructor in type.GetConstructors(flags)) entries.Add(ConstructorSignature(constructor));
        foreach (var method in type.GetMethods(flags))
        {
            if (method.IsSpecialName &&
                (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
                 method.Name.StartsWith("set_", StringComparison.Ordinal) ||
                 method.Name.StartsWith("add_", StringComparison.Ordinal) ||
                 method.Name.StartsWith("remove_", StringComparison.Ordinal))) continue;
            entries.Add(MethodSignature(method));
        }
        foreach (var property in type.GetProperties(flags)) entries.Add(PropertySignature(property));
        foreach (var field in type.GetFields(flags)) entries.Add(FieldSignature(field));
        foreach (var @event in type.GetEvents(flags)) entries.Add(EventSignature(@event));
    }
    return entries.ToList();
}

static List<string> Compare(IReadOnlyList<string> current, IReadOnlyList<string> expected, string comparison)
{
    var currentSet = current.ToHashSet(StringComparer.Ordinal);
    var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
    var errors = expectedSet.Except(currentSet, StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Select(entry => $"MISSING: {entry}")
        .ToList();
    if (comparison == "exact")
    {
        errors.AddRange(currentSet.Except(expectedSet, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(entry => $"UNEXPECTED: {entry}"));
    }
    return errors;
}

static string TypeDeclaration(Type type)
{
    var kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" :
        type.BaseType is not null && typeof(MulticastDelegate).IsAssignableFrom(type.BaseType) ? "delegate" :
        type.IsValueType ? "struct" : "class";
    var modifiers = type.IsAbstract && type.IsSealed ? "static " :
        type.IsSealed ? "sealed " : type.IsAbstract && !type.IsInterface ? "abstract " : "";
    var inherited = new List<string>();
    if (type.BaseType is not null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType) &&
        type.BaseType != typeof(Enum) && type.BaseType != typeof(MulticastDelegate))
        inherited.Add(TypeName(type.BaseType));
    inherited.AddRange(type.GetInterfaces().Select(TypeName).Order(StringComparer.Ordinal));
    var suffix = inherited.Count == 0 ? "" : " : " + string.Join(", ", inherited);
    return $"type public {modifiers}{kind} {TypeName(type)}{suffix}{GenericConstraints(type.GetGenericArguments())}{ObsoleteSuffix(type)}";
}

static string ConstructorSignature(ConstructorInfo constructor) =>
    $"constructor {TypeName(constructor.DeclaringType!)}({Parameters(constructor.GetParameters())}){ObsoleteSuffix(constructor)}";

static string MethodSignature(MethodInfo method)
{
    var modifiers = method.IsStatic ? "static " : method.IsAbstract ? "abstract " :
        method.IsVirtual && method.GetBaseDefinition() != method ? "override " :
        method.IsVirtual && !method.IsFinal ? "virtual " : "";
    if (method.IsDefined(typeof(ExtensionAttribute), inherit: false)) modifiers = "extension " + modifiers;
    var generic = method.IsGenericMethodDefinition
        ? "<" + string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name)) + ">"
        : "";
    return $"method {modifiers}{TypeName(method.ReturnType)} {TypeName(method.DeclaringType!)}.{method.Name}{generic}" +
           $"({Parameters(method.GetParameters())}){GenericConstraints(method.GetGenericArguments())}{ObsoleteSuffix(method)}";
}

static string PropertySignature(PropertyInfo property)
{
    var accessor = property.GetMethod ?? property.SetMethod!;
    var modifiers = accessor.IsStatic ? "static " : "";
    var index = property.GetIndexParameters();
    var name = index.Length == 0 ? property.Name : $"{property.Name}[{Parameters(index)}]";
    var accessors = new List<string>();
    if (property.GetMethod?.IsPublic == true) accessors.Add("get;");
    if (property.SetMethod?.IsPublic == true) accessors.Add("set;");
    return $"property {modifiers}{TypeName(property.PropertyType)} {TypeName(property.DeclaringType!)}.{name} " +
           $"{{ {string.Join(" ", accessors)} }}{ObsoleteSuffix(property)}";
}

static string FieldSignature(FieldInfo field)
{
    var modifiers = field.IsLiteral ? "const " : field.IsStatic ? "static " : "";
    var value = field.IsLiteral ? " = " + FormatDefault(field.GetRawConstantValue()) : "";
    return $"field {modifiers}{TypeName(field.FieldType)} {TypeName(field.DeclaringType!)}.{field.Name}{value}{ObsoleteSuffix(field)}";
}

static string EventSignature(EventInfo @event)
{
    var accessor = @event.AddMethod ?? @event.RemoveMethod!;
    var modifiers = accessor.IsStatic ? "static " : "";
    return $"event {modifiers}{TypeName(@event.EventHandlerType!)} {TypeName(@event.DeclaringType!)}.{@event.Name}{ObsoleteSuffix(@event)}";
}

static string Parameters(IEnumerable<ParameterInfo> parameters) => string.Join(", ", parameters.Select(parameter =>
{
    var modifier = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef && parameter.IsIn ? "in " :
        parameter.ParameterType.IsByRef ? "ref " : "";
    var type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
    var optional = parameter.HasDefaultValue ? " = " + FormatDefault(parameter.DefaultValue) : "";
    return $"{modifier}{TypeName(type)} {parameter.Name}{optional}";
}));

static string GenericConstraints(IEnumerable<Type> genericArguments)
{
    var clauses = new List<string>();
    foreach (var argument in genericArguments.Where(argument => argument.IsGenericParameter))
    {
        var parts = new List<string>();
        var attrs = argument.GenericParameterAttributes;
        if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0) parts.Add("class");
        if ((attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) parts.Add("struct");
        parts.AddRange(argument.GetGenericParameterConstraints().Select(TypeName).Order(StringComparer.Ordinal));
        if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && !parts.Contains("struct"))
            parts.Add("new()");
        if (parts.Count > 0) clauses.Add($" where {argument.Name} : {string.Join(", ", parts)}");
    }
    return string.Concat(clauses);
}

static string TypeName(Type type)
{
    if (type.IsGenericParameter) return type.Name;
    if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
    if (type.IsPointer) return TypeName(type.GetElementType()!) + "*";
    if (type.IsByRef) return TypeName(type.GetElementType()!) + "&";
    if (!type.IsGenericType) return (type.FullName ?? type.Name).Replace('+', '.');
    var definition = type.GetGenericTypeDefinition();
    var fullName = (definition.FullName ?? definition.Name).Replace('+', '.');
    var tick = fullName.IndexOf('`');
    if (tick >= 0) fullName = fullName[..tick];
    return fullName + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
}

static string FormatDefault(object? value) => value switch
{
    null => "null",
    string text => JsonSerializer.Serialize(text),
    char character => "'" + character.ToString().Replace("'", "\\'", StringComparison.Ordinal) + "'",
    bool boolean => boolean ? "true" : "false",
    float single => single.ToString("R", CultureInfo.InvariantCulture),
    double number => number.ToString("R", CultureInfo.InvariantCulture),
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "null",
    _ => value.ToString() ?? "null",
};

static string ObsoleteSuffix(MemberInfo member)
{
    var obsolete = member.GetCustomAttribute<ObsoleteAttribute>();
    if (obsolete is null) return "";
    return $" [obsolete message={JsonSerializer.Serialize(obsolete.Message)} error={obsolete.IsError.ToString().ToLowerInvariant()}" +
           $" diagnostic_id={JsonSerializer.Serialize(obsolete.DiagnosticId)} url_format={JsonSerializer.Serialize(obsolete.UrlFormat)}]";
}

sealed class InspectionLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true), IDisposable
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null) return LoadFromAssemblyPath(path);

        // SDK class-library outputs do not copy every NuGet dependency beside
        // the DLL. Resolve those packages from the standard global cache so
        // reflection can normalize signatures that mention dependency types.
        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", assemblyName.Name!.ToLowerInvariant());
        if (!Directory.Exists(packageRoot)) return null;
        var candidate = Directory.EnumerateFiles(
                packageRoot, assemblyName.Name + ".dll", SearchOption.AllDirectories)
            .OrderByDescending(file => file.Contains("/net10.0/", StringComparison.Ordinal))
            .ThenByDescending(file => file.Contains("/net8.0/", StringComparison.Ordinal))
            .FirstOrDefault(file => AssemblyName.GetAssemblyName(file).Version == assemblyName.Version);
        return candidate is null ? null : LoadFromAssemblyPath(candidate);
    }

    public void Dispose() => Unload();
}

sealed class ApiManifest
{
    public int SchemaVersion { get; set; }
    public string ProfileId { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string Comparison { get; set; } = "exact";
    public Dictionary<string, string> AssemblyMetadata { get; set; } = [];
    public List<string> Types { get; set; } = [];
    public DeprecationPolicy DeprecationPolicy { get; set; } = new();
    public List<SymbolLifecycle> Symbols { get; set; } = [];
    public List<string> Api { get; set; } = [];
}

sealed class DeprecationPolicy
{
    public int MinimumCompatibleGenerations { get; set; }
    public int MinimumDays { get; set; }
    public List<string> RequiredFields { get; set; } = [];
}

sealed class SymbolLifecycle
{
    public string Id { get; set; } = "";
    public List<string> PublicApiOwners { get; set; } = [];
    public Lifecycle Lifecycle { get; set; } = new();
}

sealed class Lifecycle
{
    public int IntroducedGeneration { get; set; }
    public int? DeprecatedSinceGeneration { get; set; }
    public string? Replacement { get; set; }
    public DateOnly? DeprecatedOn { get; set; }
    public int? EarliestRemovalGeneration { get; set; }
    public DateOnly? EarliestRemovalOn { get; set; }
}
