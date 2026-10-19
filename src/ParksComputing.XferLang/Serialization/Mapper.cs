// The object mapper is reflection over the caller's types by design (DESIGN.md section 10.8). Every way
// into this file is marked [RequiresUnreferencedCode] and [RequiresDynamicCode], on XferSerializer's
// methods and on the internal types, so a caller who trims or compiles ahead of time is warned there;
// the analysis warnings inside the implementation are the same warning repeated, and are silenced.
#pragma warning disable IL2026, IL2060, IL2067, IL2070, IL2072, IL2075, IL2077, IL2080, IL3050

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ParksComputing.XferLang.Model;

namespace ParksComputing.XferLang.Serialization;

/// <summary>
/// Maps values onto .NET types and back (DESIGN.md section 10), without recursion: each direction
/// keeps an explicit stack of the containers it is filling, so nesting depth is bounded by the
/// limits and by memory, never by the call stack.
/// </summary>
[RequiresUnreferencedCode(XferSerializer.TrimmingMessage)]
[RequiresDynamicCode(XferSerializer.AotMessage)]
internal static class Mapper
{
    // Reading.

    private abstract class ReadFrame
    {
        protected ReadFrame(TypeMap map, XferValue source)
        {
            Map = map;
            Source = source;
        }

        public TypeMap Map { get; }

        public XferValue Source { get; }

        /// <summary>The pointer step of the member being read, for messages.</summary>
        public abstract string? Step { get; }

        /// <summary>The next value to read and the type to read it as, or false when there is none left.</summary>
        public abstract bool TryNext(Func<Type, TypeMap> resolve, out XferValue value, out TypeMap target);

        public abstract void Accept(object? value);

        public abstract object? Finish();
    }

    private sealed class CollectionFrame : ReadFrame
    {
        private readonly XferSequence items;
        private readonly IList list;
        private int next;

        public CollectionFrame(TypeMap map, XferSequence items)
            : base(map, items)
        {
            this.items = items;
            list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(map.Element!), items.Count)!;
        }

        public override string? Step => next == 0 ? null : (next - 1).ToString(CultureInfo.InvariantCulture);

        public override bool TryNext(Func<Type, TypeMap> resolve, out XferValue value, out TypeMap target)
        {
            if (next < items.Count)
            {
                value = items[next++];
                target = resolve(Map.Element!);
                return true;
            }

            value = null!;
            target = null!;
            return false;
        }

        public override void Accept(object? value) => list.Add(value);

        public override object? Finish()
        {
            Type element = Map.Element!;
            switch (Map.CollectionKind)
            {
                case CollectionKind.Array:
                    var array = Array.CreateInstance(element, list.Count);
                    list.CopyTo(array, 0);
                    return array;
                case CollectionKind.HashSet:
                    return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(element), list)!;
                case CollectionKind.ImmutableArray:
                    MethodInfo create = typeof(ImmutableArray).GetMethods()
                        .First(m => m.Name == nameof(ImmutableArray.CreateRange) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsGenericType
                            && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                        .MakeGenericMethod(element);
                    return create.Invoke(null, [list]);
                default:
                    return list;
            }
        }
    }

    private sealed class DictionaryFrame : ReadFrame
    {
        private readonly XferObject source;
        private readonly IDictionary dictionary;
        private int next;

        public DictionaryFrame(TypeMap map, XferObject source)
            : base(map, source)
        {
            this.source = source;
            dictionary = (IDictionary)Activator.CreateInstance(map.DictionaryBuildType!)!;
        }

        public override string? Step => next == 0 ? null : source.GetAt(next - 1).Key;

        public override bool TryNext(Func<Type, TypeMap> resolve, out XferValue value, out TypeMap target)
        {
            if (next < source.Count)
            {
                value = source.GetAt(next++).Value;
                target = resolve(Map.Element!);
                return true;
            }

            value = null!;
            target = null!;
            return false;
        }

        public override void Accept(object? value) => dictionary.Add(source.GetAt(next - 1).Key, value);

        public override object? Finish() => dictionary;
    }

    /// <summary>For <c>object</c>: any value as plain .NET data.</summary>
    private sealed class PlainFrame : ReadFrame
    {
        private readonly XferObject? obj;
        private readonly XferSequence? seq;
        private readonly OrderedDictionary<string, object?>? dictionary;
        private readonly List<object?>? list;
        private int next;

        public PlainFrame(TypeMap map, XferValue source)
            : base(map, source)
        {
            obj = source as XferObject;
            seq = source as XferSequence;
            if (obj is not null)
            {
                dictionary = new OrderedDictionary<string, object?>(obj.Count, StringComparer.Ordinal);
            }
            else
            {
                list = new List<object?>(seq!.Count);
            }
        }

        public override string? Step => next == 0 ? null : obj is not null ? obj.GetAt(next - 1).Key : (next - 1).ToString(CultureInfo.InvariantCulture);

        public override bool TryNext(Func<Type, TypeMap> resolve, out XferValue value, out TypeMap target)
        {
            int count = obj?.Count ?? seq!.Count;
            if (next < count)
            {
                value = obj is not null ? obj.GetAt(next++).Value : seq![next++];
                target = Map;
                return true;
            }

            value = null!;
            target = null!;
            return false;
        }

        public override void Accept(object? value)
        {
            if (dictionary is not null)
            {
                dictionary.Add(obj!.GetAt(next - 1).Key, value);
            }
            else
            {
                list!.Add(value);
            }
        }

        public override object? Finish() => dictionary is not null ? dictionary : list;
    }

    private sealed class ObjectFrame : ReadFrame
    {
        private readonly XferObject source;
        private readonly XferSerializerOptions options;
        private readonly object?[] slots;
        private readonly bool[] assigned;
        private int next;
        private MemberMap? current;

        public ObjectFrame(TypeMap map, XferObject source, XferSerializerOptions options)
            : base(map, source)
        {
            // A type that cannot be built is a fault of the program, not of the document, so it
            // is not a diagnostic. It is raised here, when an object is to be read into the type,
            // because the same type may be written, or read from null, without trouble.
            if (map.CannotConstruct is { } why)
            {
                throw new InvalidOperationException($"{why}, so it cannot be read from an object");
            }

            this.source = source;
            this.options = options;
            slots = new object?[map.Members.Length];
            assigned = new bool[map.Members.Length];
        }

        public override string? Step => next == 0 ? null : source.GetAt(next - 1).Key;

        public override bool TryNext(Func<Type, TypeMap> resolve, out XferValue value, out TypeMap target)
        {
            while (next < source.Count)
            {
                int index = next++;
                KeyValuePair<string, XferValue> entry = source.GetAt(index);
                if (!Map.ByKey.TryGetValue(entry.Key, out MemberMap? member) || !member.Readable)
                {
                    if (options.UnmappedMemberHandling == XferUnmappedMemberHandling.Disallow)
                    {
                        throw new MappingFault(XferDiagnosticCode.XFER0802, $"the key \"{Syntax.Messages.Shorten(entry.Key)}\" matches no member of {Scalars.TypeName(Map.Type)}", null)
                        {
                            KeyOwner = source,
                            KeyIndex = index,
                        };
                    }

                    continue;
                }

                if (assigned[member.Index])
                {
                    throw new MappingFault(XferDiagnosticCode.XFER0804, $"the key \"{Syntax.Messages.Shorten(entry.Key)}\" is the second for the member {member.Name}", null)
                    {
                        KeyOwner = source,
                        KeyIndex = index,
                    };
                }

                current = member;
                value = entry.Value;
                target = member.Converter is { } converter ? ConverterMap(member.Type, converter) : resolve(member.Type);
                return true;
            }

            value = null!;
            target = null!;
            return false;
        }

        public override void Accept(object? value)
        {
            slots[current!.Index] = value;
            assigned[current.Index] = true;
        }

        public override object? Finish()
        {
            foreach (MemberMap m in Map.Members)
            {
                if (m.Required && !assigned[m.Index])
                {
                    throw new MappingFault(XferDiagnosticCode.XFER0803, $"the required member {m.Name} (key \"{m.Key}\") of {Scalars.TypeName(Map.Type)} is missing", source);
                }
            }

            object instance;
            if (Map.Parameters.Length > 0)
            {
                object?[] args = new object?[Map.Parameters.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    ParameterInfo p = Map.Parameters[i];
                    args[i] = p.HasDefaultValue ? p.DefaultValue : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                }

                foreach (MemberMap m in Map.Members)
                {
                    if (m.ParameterIndex >= 0 && assigned[m.Index])
                    {
                        args[m.ParameterIndex] = slots[m.Index];
                    }
                }

                instance = Map.Constructor!.Invoke(args);
            }
            else
            {
                instance = Map.Constructor is { } ctor ? ctor.Invoke(null) : Activator.CreateInstance(Map.Type)!;
            }

            foreach (MemberMap m in Map.Members)
            {
                if (assigned[m.Index] && m.ParameterIndex < 0)
                {
                    m.Set!(instance, slots[m.Index]);
                }
            }

            return instance;
        }
    }

    private static TypeMap ConverterMap(Type type, XferConverter converter) => new(type) { Shape = Shape.Converter, Converter = converter };

    public static object? Read(XferValue root, Type type, XferSerializerOptions options, SourceMap? positions)
    {
        options.Freeze();
        TypeMap Resolve(Type t) => options.Cache.Get(t, options);
        var stack = new Stack<ReadFrame>();
        XferValue? pendingValue = root;
        TypeMap pendingMap = Resolve(type);
        object? produced = null;
        bool haveProduced = false;

        try
        {
            while (true)
            {
                if (pendingValue is not null)
                {
                    XferValue value = pendingValue;
                    TypeMap map = pendingMap;
                    pendingValue = null;

                    // Nullable<T> is T, or null.
                    if (map.Shape == Shape.Nullable)
                    {
                        if (value is XferNull)
                        {
                            produced = null;
                            haveProduced = true;
                            goto delivered;
                        }

                        map = Resolve(map.Element!);
                    }

                    if (value is XferNull && map.Shape != Shape.XferValue && map.Shape != Shape.Converter)
                    {
                        if (map.Type.IsValueType)
                        {
                            throw Scalars.Mismatch(value, map.Type, "a value, not null");
                        }

                        produced = null;
                        haveProduced = true;
                        goto delivered;
                    }

                    switch (map.Shape)
                    {
                        case Shape.Converter:
                            try
                            {
                                produced = map.Converter!.ReadObject(value, map.Type, new XferReadContext(options, Pointer(stack)));
                            }
                            catch (XferException e)
                            {
                                throw new MappingFault(e.Diagnostics.Count > 0 ? e.Diagnostics[0].Code : XferDiagnosticCode.XFER0801, e.Message, value);
                            }

                            haveProduced = true;
                            break;
                        case Shape.XferValue:
                            produced = map.Type.IsInstanceOfType(value) ? value : throw Scalars.Mismatch(value, map.Type, $"a {Scalars.TypeName(map.Type)}");
                            haveProduced = true;
                            break;
                        case Shape.Scalar:
                            produced = Scalars.Read(value, map.Type, options);
                            haveProduced = true;
                            break;
                        case Shape.Collection:
                            stack.Push(value is XferSequence seq ? new CollectionFrame(map, seq) : throw Scalars.Mismatch(value, map.Type, "a sequence"));
                            break;
                        case Shape.Dictionary:
                            stack.Push(value is XferObject dict ? new DictionaryFrame(map, dict) : throw Scalars.Mismatch(value, map.Type, "an object"));
                            break;
                        case Shape.Object:
                            stack.Push(value is XferObject obj ? new ObjectFrame(map, obj, options) : throw Scalars.Mismatch(value, map.Type, "an object"));
                            break;
                        case Shape.Plain:
                            if (value is XferObject or XferSequence)
                            {
                                stack.Push(new PlainFrame(map, value));
                            }
                            else
                            {
                                produced = Plain(value);
                                haveProduced = true;
                            }

                            break;
                        default:
                            throw new InvalidOperationException($"no mapping for {map.Type}");
                    }
                }

            delivered:
                if (haveProduced)
                {
                    haveProduced = false;
                    if (stack.Count == 0)
                    {
                        return produced;
                    }

                    stack.Peek().Accept(produced);
                }

                ReadFrame top = stack.Peek();
                if (top.TryNext(Resolve, out XferValue nextValue, out TypeMap nextMap))
                {
                    pendingValue = nextValue;
                    pendingMap = nextMap;
                }
                else
                {
                    stack.Pop();
                    produced = top.Finish();
                    haveProduced = true;
                    goto delivered;
                }
            }
        }
        catch (MappingFault fault)
        {
            fault.Pointer ??= Pointer(stack);
            throw ToException(fault, positions);
        }
    }

    /// <summary>A scalar as plain .NET data, for an <c>object</c> target.</summary>
    private static object? Plain(XferValue value) => value switch
    {
        XferString s => s.Value,
        XferSymbol s => s.Name,
        XferBool b => b.Value,
        XferNull => null,
        XferInteger i => i.TryGetInt64(out long l) ? (object)l : i.Value,
        XferFractional f => f.ToDouble(out _),
        // A temporal no .NET type can hold (a leap second, the year 0000) stays the parsed value:
        // it is exact, keeps its kind, and is written back as it was. Its text would change its
        // kind, and refusing it would refuse a valid document.
        XferTemporal t => t.TemporalKind switch
        {
            XferTemporalKind.Date => t.TryGetDateOnly(out DateOnly d) ? d : t,
            XferTemporalKind.Time => t.TryGetTimeOnly(out TimeOnly time) ? time : t,
            XferTemporalKind.LocalDateTime => t.TryGetDateTime(out DateTime dt) ? dt : t,
            _ => t.TryGetDateTimeOffset(out DateTimeOffset dto) ? dto : t,
        },
        _ => throw new InvalidOperationException($"no plain form for {value.Kind}"),
    };

    private static string Pointer(Stack<ReadFrame> stack)
    {
        var steps = new List<string>();
        foreach (ReadFrame f in stack)
        {
            if (f.Step is { } step)
            {
                steps.Add(step.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            }
        }

        steps.Reverse();
        return steps.Count == 0 ? string.Empty : "/" + string.Join('/', steps);
    }

    private static XferException ToException(MappingFault fault, SourceMap? positions)
    {
        XferPosition at = XferPosition.Origin;
        if (positions is not null)
        {
            if (fault.KeyOwner is not null)
            {
                at = positions.PositionOfKey(fault.KeyOwner, fault.KeyIndex) ?? at;
            }
            else if (fault.At is not null)
            {
                at = positions.PositionOf(fault.At) ?? at;
            }
        }

        string where = string.IsNullOrEmpty(fault.Pointer) ? "the root" : fault.Pointer;
        return new XferException([XferDiagnostics.Create(fault.Code, at, null, $"{fault.Message} (at {where})")]);
    }

    // Writing.

    private sealed class WriteFrame
    {
        public WriteFrame(bool isObject, IEnumerator<(string? Key, object? Value, MemberMap? Member)> items)
        {
            IsObject = isObject;
            Items = items;
        }

        public bool IsObject { get; }

        public IEnumerator<(string? Key, object? Value, MemberMap? Member)> Items { get; }

        public List<KeyValuePair<string, XferValue>> Members { get; } = [];

        public List<XferValue> Values { get; } = [];

        public string? CurrentKey { get; set; }
    }

    public static XferValue Write(object? root, XferSerializerOptions options)
    {
        options.Freeze();
        TypeMap Resolve(Type t) => options.Cache.Get(t, options);
        var stack = new Stack<WriteFrame>();
        object? pending = root;
        MemberMap? pendingMember = null;
        bool havePending = true;
        XferValue? produced = null;

        while (true)
        {
            if (havePending)
            {
                havePending = false;
                produced = null;
                object? value = pending;
                MemberMap? member = pendingMember;
                if (value is null)
                {
                    produced = new XferNull();
                }
                else if (member?.Converter is { } memberConverter)
                {
                    produced = memberConverter.WriteObject(value, new XferWriteContext(options));
                }
                else
                {
                    TypeMap map = Resolve(value.GetType());
                    if (map.Shape == Shape.Nullable)
                    {
                        map = Resolve(map.Element!);
                    }

                    switch (map.Shape)
                    {
                        case Shape.Converter:
                            produced = map.Converter!.WriteObject(value, new XferWriteContext(options));
                            break;
                        case Shape.XferValue:
                            produced = (XferValue)value;
                            break;
                        case Shape.Scalar:
                            produced = Scalars.Write(value, options, member?.Symbol == true);
                            break;
                        default:
                            if (stack.Count >= options.Limits.Depth)
                            {
                                throw new XferException(
                                    [XferDiagnostics.Create(XferDiagnosticCode.XFER0602, XferPosition.Origin, null, $"the value is nested more than {options.Limits.Depth} levels deep, or refers to itself")]);
                            }

                            stack.Push(Open(value, map, options));
                            break;
                    }
                }
            }

            if (produced is not null)
            {
                if (stack.Count == 0)
                {
                    return produced;
                }

                WriteFrame frame = stack.Peek();
                if (frame.IsObject)
                {
                    frame.Members.Add(new KeyValuePair<string, XferValue>(frame.CurrentKey!, produced));
                }
                else
                {
                    frame.Values.Add(produced);
                }

                produced = null;
            }

            WriteFrame top = stack.Peek();
            if (top.Items.MoveNext())
            {
                (string? key, object? value, MemberMap? member) = top.Items.Current;
                top.CurrentKey = key;
                pending = value;
                pendingMember = member;
                havePending = true;
            }
            else
            {
                stack.Pop();
                top.Items.Dispose();
                produced = top.IsObject ? new XferObject(top.Members) : new XferSequence(top.Values);
            }
        }
    }

    private static WriteFrame Open(object value, TypeMap map, XferSerializerOptions options)
    {
        if (map.Shape is Shape.Object && !IsDictionary(value))
        {
            return new WriteFrame(true, Members(value, map, options).GetEnumerator());
        }

        if (value is IDictionary dictionary)
        {
            return new WriteFrame(true, DictionaryEntries(dictionary).GetEnumerator());
        }

        if (GenericDictionaryEntries(value) is { } entries)
        {
            return new WriteFrame(true, entries.GetEnumerator());
        }

        if (value is IEnumerable sequence)
        {
            return new WriteFrame(false, Items(sequence).GetEnumerator());
        }

        return new WriteFrame(true, Members(value, map, options).GetEnumerator());
    }

    private static bool IsDictionary(object value) => value is IDictionary || GenericDictionaryValueType(value.GetType()) is not null;

    private static IEnumerable<(string?, object?, MemberMap?)> Members(object value, TypeMap map, XferSerializerOptions options)
    {
        foreach (MemberMap m in map.WriteOrder)
        {
            object? memberValue = m.Get(value);
            if (options.DefaultIgnoreCondition != XferIgnoreCondition.Never && memberValue is null)
            {
                continue;
            }

            if (options.DefaultIgnoreCondition == XferIgnoreCondition.WhenWritingDefault && m.Type.IsValueType && Equals(memberValue, Activator.CreateInstance(m.Type)))
            {
                continue;
            }

            yield return (m.Key, memberValue, m);
        }
    }

    private static IEnumerable<(string?, object?, MemberMap?)> DictionaryEntries(IDictionary dictionary)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            yield return (entry.Key as string ?? throw new InvalidOperationException("a dictionary is written as an object only when its keys are strings"), entry.Value, null);
        }
    }

    private static Type? GenericDictionaryValueType(Type type)
    {
        foreach (Type i in type.GetInterfaces().Prepend(type))
        {
            if (i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
            {
                Type[] args = i.GetGenericArguments();
                return args[0] == typeof(string) ? args[1] : null;
            }
        }

        return null;
    }

    private static IEnumerable<(string?, object?, MemberMap?)>? GenericDictionaryEntries(object value)
    {
        Type? valueType = GenericDictionaryValueType(value.GetType());
        if (valueType is null)
        {
            return null;
        }

        Type pair = typeof(KeyValuePair<,>).MakeGenericType(typeof(string), valueType);
        PropertyInfo key = pair.GetProperty(nameof(KeyValuePair<string, object>.Key))!;
        PropertyInfo item = pair.GetProperty(nameof(KeyValuePair<string, object>.Value))!;
        return Pairs((IEnumerable)value, key, item);

        static IEnumerable<(string?, object?, MemberMap?)> Pairs(IEnumerable entries, PropertyInfo key, PropertyInfo item)
        {
            foreach (object entry in entries)
            {
                yield return ((string?)key.GetValue(entry), item.GetValue(entry), null);
            }
        }
    }

    private static IEnumerable<(string?, object?, MemberMap?)> Items(IEnumerable sequence)
    {
        foreach (object? item in sequence)
        {
            yield return (null, item, null);
        }
    }
}
