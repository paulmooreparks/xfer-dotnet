// The object mapper is reflection over the caller's types by design (DESIGN.md section 10.8). Every way
// into this file is marked [RequiresUnreferencedCode] and [RequiresDynamicCode], on XferSerializer's
// methods and on the internal types, so a caller who trims or compiles ahead of time is warned there;
// the analysis warnings inside the implementation are the same warning repeated, and are silenced.
#pragma warning disable IL2026, IL2060, IL2067, IL2070, IL2072, IL2075, IL2077, IL2080, IL3050

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ParksComputing.XferLang.Serialization;

/// <summary>What a type is to the mapper.</summary>
internal enum Shape
{
    /// <summary>A value the mapper converts directly (numbers, strings, dates, enums and so on).</summary>
    Scalar,

    /// <summary><c>Nullable&lt;T&gt;</c>.</summary>
    Nullable,

    /// <summary>An <see cref="XferValue"/> type: the value is passed through as it is.</summary>
    XferValue,

    /// <summary>A sequence target: an array, a list, a set, an immutable array, or an interface a list satisfies.</summary>
    Collection,

    /// <summary>An object target with string keys.</summary>
    Dictionary,

    /// <summary><c>object</c>: any value, as plain .NET data.</summary>
    Plain,

    /// <summary>A class, struct or record mapped member by member.</summary>
    Object,

    /// <summary>A type a converter handles.</summary>
    Converter,

    /// <summary>
    /// A collection or dictionary the mapper cannot fill (no public parameterless constructor, or
    /// keys that are not strings). It is written by what it enumerates; reading an object or a
    /// sequence into it is an <see cref="InvalidOperationException"/>, never an empty value.
    /// </summary>
    Unsupported,
}

/// <summary>How a collection target is built from the list of its items.</summary>
internal enum CollectionKind
{
    Array,
    List,
    HashSet,
    ImmutableArray,

    /// <summary>Any other concrete <c>ICollection&lt;T&gt;</c> with a public parameterless constructor, filled through its <c>Add</c>.</summary>
    Add,
}

/// <summary>A member of an object type.</summary>
internal sealed class MemberMap
{
    public required string Name { get; init; }

    public required string Key { get; init; }

    public required string[] Aliases { get; init; }

    public required Type Type { get; init; }

    public required Func<object, object?> Get { get; init; }

    /// <summary>Null for a member that is written but not read (a get-only property with no constructor parameter).</summary>
    public Action<object, object?>? Set { get; init; }

    /// <summary>Marked required, a C# <c>required</c> member, or a constructor parameter without a default value.</summary>
    public required bool Required { get; set; }

    public required bool Symbol { get; init; }

    public required int Order { get; init; }

    public XferConverter? Converter { get; init; }

    /// <summary>The position of the member among the constructor's parameters, or -1.</summary>
    public int ParameterIndex { get; set; } = -1;

    public int Index { get; set; }

    public bool Readable => Set is not null || ParameterIndex >= 0;
}

/// <summary>What the mapper knows about one type, built once per options instance.</summary>
[RequiresUnreferencedCode(XferSerializer.TrimmingMessage)]
[RequiresDynamicCode(XferSerializer.AotMessage)]
internal sealed class TypeMap
{
    public TypeMap(Type type)
    {
        Type = type;
    }

    public Type Type { get; }

    public Shape Shape { get; set; }

    /// <summary>The element type of a collection, the value type of a dictionary, or the underlying type of a nullable.</summary>
    public Type? Element { get; set; }

    public CollectionKind CollectionKind { get; set; }

    /// <summary>For a dictionary target: whether it is an interface (built as an ordered dictionary) or a concrete type.</summary>
    public Type? DictionaryBuildType { get; set; }

    public XferConverter? Converter { get; set; }

    public MemberMap[] Members { get; set; } = [];

    /// <summary>Members in writing order: by <see cref="XferPropertyOrderAttribute"/>, then base class first, then as reflection gives them.</summary>
    public MemberMap[] WriteOrder { get; set; } = [];

    public Dictionary<string, MemberMap> ByKey { get; set; } = new(StringComparer.Ordinal);

    public ConstructorInfo? Constructor { get; set; }

    public ParameterInfo[] Parameters { get; set; } = [];

    /// <summary>Why the type cannot be built, when it cannot; reading it is then an error.</summary>
    public string? CannotConstruct { get; set; }

    public bool IsValueType => Type.IsValueType;
}

/// <summary>The type maps of one options instance.</summary>
internal sealed class TypeInfoCache
{
    private readonly ConcurrentDictionary<Type, object> maps = new();

    [RequiresUnreferencedCode(XferSerializer.TrimmingMessage)]
    [RequiresDynamicCode(XferSerializer.AotMessage)]
    public TypeMap Get(Type type, XferSerializerOptions options) => (TypeMap)maps.GetOrAdd(type, t => TypeResolver.Build(t, options));
}

/// <summary>The reflection, all in one place (DESIGN.md section 10.8): turns a type into a <see cref="TypeMap"/>.</summary>
[RequiresUnreferencedCode(XferSerializer.TrimmingMessage)]
[RequiresDynamicCode(XferSerializer.AotMessage)]
internal static class TypeResolver
{
    private static readonly HashSet<Type> Scalars =
    [
        typeof(string), typeof(char), typeof(bool),
        typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(nint), typeof(nuint), typeof(Int128), typeof(UInt128), typeof(BigInteger),
        typeof(double), typeof(float), typeof(Half), typeof(decimal),
        typeof(DateOnly), typeof(TimeOnly), typeof(DateTime), typeof(DateTimeOffset),
        typeof(TimeSpan), typeof(Guid), typeof(Uri), typeof(Version),
    ];

    public static TypeMap Build(Type type, XferSerializerOptions options)
    {
        var map = new TypeMap(type);
        XferConverter? converter = FindConverter(type, options, type.GetCustomAttribute<XferConverterAttribute>());
        if (converter is not null)
        {
            map.Shape = Shape.Converter;
            map.Converter = converter;
            return map;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            map.Shape = Shape.Nullable;
            map.Element = underlying;
            return map;
        }

        if (Scalars.Contains(type) || type.IsEnum)
        {
            map.Shape = Shape.Scalar;
            return map;
        }

        if (typeof(XferValue).IsAssignableFrom(type))
        {
            map.Shape = Shape.XferValue;
            return map;
        }

        if (type == typeof(object))
        {
            map.Shape = Shape.Plain;
            return map;
        }

        if (DictionaryValueType(type) is { } valueType)
        {
            map.Shape = Shape.Dictionary;
            map.Element = valueType;
            map.DictionaryBuildType = type.IsInterface || type.IsAbstract
                ? typeof(OrderedDictionary<,>).MakeGenericType(typeof(string), valueType)
                : type;
            return map;
        }

        if (CollectionElementType(type, out CollectionKind kind) is { } element)
        {
            map.Shape = Shape.Collection;
            map.Element = element;
            map.CollectionKind = kind;
            return map;
        }

        // Anything else that enumerates is a collection the mapper cannot fill. Mapping it member
        // by member would read its items as an empty object, so it is refused instead.
        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            map.Shape = Shape.Unsupported;
            map.CannotConstruct = GenericDictionaryArguments(type) is { Key: var key } && key != typeof(string)
                ? $"{type} has {key} keys, and a dictionary is read from an object only when its keys are strings"
                : type.IsArray
                    ? $"{type} is a multidimensional array, which has no sequence form; use a jagged array"
                    : $"{type} is a collection without a public parameterless constructor and an Add the mapper can call; use an array, a list, a set or a dictionary type of DESIGN.md section 10.3";
            return map;
        }

        map.Shape = Shape.Object;
        BuildObject(map, options);
        return map;
    }

    public static XferConverter? FindConverter(Type type, XferSerializerOptions options, XferConverterAttribute? attribute)
    {
        foreach (XferConverter c in options.Converters)
        {
            if (c.CanConvert(type))
            {
                return c;
            }
        }

        return attribute is null ? null : Instantiate(attribute.ConverterType, type);
    }

    public static XferConverter Instantiate(Type converterType, Type forType)
    {
        if (!typeof(XferConverter).IsAssignableFrom(converterType) || converterType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException($"{converterType} is not an XferConverter with a public parameterless constructor");
        }

        var converter = (XferConverter)Activator.CreateInstance(converterType)!;
        return converter.CanConvert(forType) ? converter : throw new InvalidOperationException($"{converterType} cannot convert {forType}");
    }

    /// <summary>The value type of a dictionary with string keys that the mapper can fill, or null.</summary>
    private static Type? DictionaryValueType(Type type)
    {
        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            Type[] args = type.GetGenericArguments();
            if (args.Length == 2 && args[0] == typeof(string)
                && (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)
                    || definition == typeof(OrderedDictionary<,>) || definition == typeof(SortedDictionary<,>)))
            {
                return args[1];
            }
        }

        // Any other concrete dictionary with string keys that can be made empty and filled
        // (a subclass of Dictionary, ConcurrentDictionary, SortedList).
        if (!type.IsAbstract && !type.IsInterface && typeof(IDictionary).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null
            && GenericDictionaryArguments(type) is { Key: var key, Value: var value } && key == typeof(string))
        {
            return value;
        }

        return null;
    }

    /// <summary>The key and value types of the first generic dictionary interface a type implements, or null.</summary>
    internal static (Type Key, Type Value)? GenericDictionaryArguments(Type type)
    {
        foreach (Type i in type.GetInterfaces().Prepend(type))
        {
            if (i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
            {
                Type[] args = i.GetGenericArguments();
                return (args[0], args[1]);
            }
        }

        return null;
    }

    /// <summary>The element type of a sequence target the mapper can fill, or null.</summary>
    private static Type? CollectionElementType(Type type, out CollectionKind kind)
    {
        kind = CollectionKind.List;
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            kind = CollectionKind.Array;
            return type.GetElementType();
        }

        if (!type.IsGenericType)
        {
            return AddableElementType(type, ref kind);
        }

        Type definition = type.GetGenericTypeDefinition();
        Type element = type.GetGenericArguments()[0];
        if (definition == typeof(List<>) || definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>)
            || definition == typeof(IReadOnlyCollection<>) || definition == typeof(ICollection<>) || definition == typeof(IList<>))
        {
            kind = CollectionKind.List;
            return element;
        }

        if (definition == typeof(HashSet<>) || definition == typeof(ISet<>) || definition == typeof(IReadOnlySet<>))
        {
            kind = CollectionKind.HashSet;
            return element;
        }

        if (definition == typeof(ImmutableArray<>))
        {
            kind = CollectionKind.ImmutableArray;
            return element;
        }

        return AddableElementType(type, ref kind);
    }

    /// <summary>The element type of a concrete <c>ICollection&lt;T&gt;</c> with a public parameterless constructor (a subclass of List, a LinkedList, a SortedSet), or null.</summary>
    private static Type? AddableElementType(Type type, ref CollectionKind kind)
    {
        if (type.IsAbstract || type.IsInterface || type.IsArray || type.GetConstructor(Type.EmptyTypes) is null || GenericDictionaryArguments(type) is not null)
        {
            return null;
        }

        foreach (Type i in type.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>))
            {
                kind = CollectionKind.Add;
                return i.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static void BuildObject(TypeMap map, XferSerializerOptions options)
    {
        Type type = map.Type;
        var members = new List<(MemberMap Member, int Depth, int Position)>();

        // Walk from the most derived type to the base, so a property hidden with "new" is taken
        // once, from the most derived declaration; then order base class members first.
        var chain = new List<Type>();
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            chain.Add(t);
        }

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        for (int depth = 0; depth < chain.Count; depth++)
        {
            Type level = chain[depth];
            int position = 0;
            foreach (PropertyInfo p in level.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length > 0 || p.GetMethod is not { IsPublic: true } || !seenNames.Add(p.Name) || p.IsDefined(typeof(XferIgnoreAttribute)))
                {
                    continue;
                }

                members.Add((Describe(p, p.PropertyType, options, o => p.GetValue(o), p.SetMethod is { IsPublic: true } ? (o, v) => p.SetValue(o, v) : null), depth, position++));
            }

            foreach (FieldInfo f in level.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!seenNames.Add(f.Name) || f.IsDefined(typeof(XferIgnoreAttribute)))
                {
                    continue;
                }

                members.Add((Describe(f, f.FieldType, options, o => f.GetValue(o), f.IsInitOnly ? null : (o, v) => f.SetValue(o, v)), depth, position++));
            }
        }

        MemberMap[] all = [.. members.Select(m => m.Member)];
        for (int i = 0; i < all.Length; i++)
        {
            all[i].Index = i;
        }

        map.Members = all;
        map.WriteOrder = [.. members.OrderBy(m => m.Member.Order).ThenByDescending(m => m.Depth).ThenBy(m => m.Position).Select(m => m.Member)];

        StringComparer comparer = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var byKey = new Dictionary<string, MemberMap>(comparer);
        foreach (MemberMap m in all)
        {
            foreach (string key in m.Aliases.Prepend(m.Key))
            {
                if (!byKey.TryAdd(key, m))
                {
                    throw new InvalidOperationException($"{type}: the members {byKey[key].Name} and {m.Name} both use the key \"{key}\"");
                }
            }
        }

        map.ByKey = byKey;
        ChooseConstructor(map);
    }

    private static MemberMap Describe(MemberInfo member, Type memberType, XferSerializerOptions options, Func<object, object?> get, Action<object, object?>? set)
    {
        XferPropertyNameAttribute? named = member.GetCustomAttribute<XferPropertyNameAttribute>();
        string key = named?.Name ?? options.PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name;
        XferConverterAttribute? converter = member.GetCustomAttribute<XferConverterAttribute>();
        return new MemberMap
        {
            Name = member.Name,
            Key = key,
            Aliases = named?.Aliases ?? [],
            Type = memberType,
            Get = get,
            Set = set,
            Required = member.IsDefined(typeof(XferRequiredAttribute)) || member.IsDefined(typeof(RequiredMemberAttribute)),
            Symbol = member.IsDefined(typeof(XferSymbolAttribute)),
            Order = member.GetCustomAttribute<XferPropertyOrderAttribute>()?.Order ?? 0,
            Converter = converter is null ? null : Instantiate(converter.ConverterType, memberType),
        };
    }

    /// <summary>
    /// The constructor: the one marked <see cref="XferConstructorAttribute"/>; else the public
    /// parameterless one; else the only public one, whose parameters must match members by name.
    /// </summary>
    private static void ChooseConstructor(TypeMap map)
    {
        Type type = map.Type;
        if (type.IsAbstract || type.IsInterface)
        {
            map.CannotConstruct = $"{type} is abstract or an interface";
            return;
        }

        ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        ConstructorInfo? chosen = constructors.FirstOrDefault(c => c.IsDefined(typeof(XferConstructorAttribute)));
        if (chosen is null)
        {
            // A struct always has a default value, but when its author gave it exactly one
            // constructor and no parameterless one, that constructor is how it is meant to be
            // built: taking the default instead would leave get-only members at zero.
            ConstructorInfo[] withParameters = [.. constructors.Where(c => c.GetParameters().Length > 0)];
            bool parameterless = constructors.Length > withParameters.Length;
            if (parameterless || (type.IsValueType && withParameters.Length != 1))
            {
                map.Constructor = constructors.FirstOrDefault(c => c.GetParameters().Length == 0);
                return;
            }

            if (constructors.Length != 1)
            {
                map.CannotConstruct = $"{type} has no public parameterless constructor and {constructors.Length} others; mark one with [XferConstructor]";
                return;
            }

            chosen = constructors[0];
        }

        map.Constructor = chosen;
        map.Parameters = chosen.GetParameters();
        for (int i = 0; i < map.Parameters.Length; i++)
        {
            ParameterInfo parameter = map.Parameters[i];
            MemberMap? member = map.Members.FirstOrDefault(m => string.Equals(m.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (member is null)
            {
                map.CannotConstruct = $"{type}: the constructor parameter {parameter.Name} matches no member by name";
                return;
            }

            member.ParameterIndex = i;

            // The constructor cannot be called without a value for this parameter, and inventing
            // default(T) for it would build an object its author never allowed.
            member.Required |= !parameter.HasDefaultValue;
        }
    }
}
