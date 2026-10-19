using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace ParksComputing.XferLang.Serialization;

/// <summary>What the mapper does with a key that matches no member.</summary>
public enum XferUnmappedMemberHandling
{
    /// <summary>Skip it (the default).</summary>
    Skip,

    /// <summary>Refuse it with <c>XFER0802</c>.</summary>
    Disallow,
}

/// <summary>When a member is left out on writing.</summary>
public enum XferIgnoreCondition
{
    /// <summary>Always write it (the default).</summary>
    Never,

    /// <summary>Leave it out when it is null.</summary>
    WhenWritingNull,

    /// <summary>Leave it out when it is null or the default of its type.</summary>
    WhenWritingDefault,
}

/// <summary>
/// Options for <see cref="XferSerializer"/>. The names follow <c>System.Text.Json</c> where the meaning
/// is the same. An options instance caches what it learns about types, so reuse one; it cannot be
/// changed after it has first been used.
/// </summary>
public sealed class XferSerializerOptions
{
    private XferNamingPolicy? propertyNamingPolicy;
    private bool propertyNameCaseInsensitive;
    private XferUnmappedMemberHandling unmappedMemberHandling;
    private XferIgnoreCondition defaultIgnoreCondition;
    private XferLimits limits = XferLimits.Default;
    private readonly ConverterList converters;
    private volatile bool frozen;

    /// <summary>Creates options with the defaults.</summary>
    public XferSerializerOptions()
    {
        converters = new ConverterList(this);
    }

    /// <summary>How member names become keys; null (the default) uses the name as declared.</summary>
    public XferNamingPolicy? PropertyNamingPolicy
    {
        get => propertyNamingPolicy;
        set => Set(ref propertyNamingPolicy, value);
    }

    /// <summary>Whether a key matches a member regardless of case (false by default).</summary>
    public bool PropertyNameCaseInsensitive
    {
        get => propertyNameCaseInsensitive;
        set => Set(ref propertyNameCaseInsensitive, value);
    }

    /// <summary>What to do with a key that matches no member.</summary>
    public XferUnmappedMemberHandling UnmappedMemberHandling
    {
        get => unmappedMemberHandling;
        set => Set(ref unmappedMemberHandling, value);
    }

    /// <summary>When a member is left out on writing.</summary>
    public XferIgnoreCondition DefaultIgnoreCondition
    {
        get => defaultIgnoreCondition;
        set => Set(ref defaultIgnoreCondition, value);
    }

    /// <summary>The limits for parsing and writing. Defaults to the specification's minimums.</summary>
    public XferLimits Limits
    {
        get => limits;
        set => Set(ref limits, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Converters for types the mapper does not handle, or handles differently. The first that can convert a type wins.</summary>
    public IList<XferConverter> Converters => converters;

    internal Serialization.TypeInfoCache Cache { get; } = new();

    /// <summary>Makes the options read-only; called the first time they are used.</summary>
    internal void Freeze() => frozen = true;

    private void Set<T>(ref T field, T value)
    {
        if (frozen)
        {
            throw new InvalidOperationException("these options have been used and can no longer be changed");
        }

        field = value;
    }

    private sealed class ConverterList : Collection<XferConverter>
    {
        private readonly XferSerializerOptions owner;

        public ConverterList(XferSerializerOptions owner)
        {
            this.owner = owner;
        }

        protected override void InsertItem(int index, XferConverter item)
        {
            Check(item);
            base.InsertItem(index, item);
        }

        protected override void SetItem(int index, XferConverter item)
        {
            Check(item);
            base.SetItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            Check(null);
            base.RemoveItem(index);
        }

        protected override void ClearItems()
        {
            Check(null);
            base.ClearItems();
        }

        private void Check(XferConverter? item)
        {
            if (owner.frozen)
            {
                throw new InvalidOperationException("these options have been used and can no longer be changed");
            }

            if (item is null && Count >= 0)
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(item);
        }
    }
}

/// <summary>How a member name becomes a key, as <c>System.Text.Json</c>'s <c>JsonNamingPolicy</c> does.</summary>
public abstract class XferNamingPolicy
{
    /// <summary><c>FirstName</c> becomes <c>firstName</c>.</summary>
    public static XferNamingPolicy CamelCase { get; } = new CamelCasePolicy();

    /// <summary><c>FirstName</c> becomes <c>first-name</c>, the usual spelling of an XferLang key.</summary>
    public static XferNamingPolicy KebabCaseLower { get; } = new SeparatedPolicy('-');

    /// <summary><c>FirstName</c> becomes <c>first_name</c>.</summary>
    public static XferNamingPolicy SnakeCaseLower { get; } = new SeparatedPolicy('_');

    /// <summary>The key for a member name.</summary>
    public abstract string ConvertName(string name);

    private sealed class CamelCasePolicy : XferNamingPolicy
    {
        public override string ConvertName(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (name.Length == 0 || !char.IsUpper(name[0]))
            {
                return name;
            }

            // A leading run of capitals is lowered up to the last one before a lower-case letter,
            // so URLValue becomes urlValue, and ID becomes id.
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (i > 0 && i + 1 < chars.Length && !char.IsUpper(chars[i + 1]) && char.IsUpper(chars[i]))
                {
                    break;
                }

                if (!char.IsUpper(chars[i]))
                {
                    break;
                }

                chars[i] = char.ToLowerInvariant(chars[i]);
            }

            return new string(chars);
        }
    }

    private sealed class SeparatedPolicy : XferNamingPolicy
    {
        private readonly char separator;

        public SeparatedPolicy(char separator)
        {
            this.separator = separator;
        }

        public override string ConvertName(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            // Words begin at a capital that follows a lower-case letter or a digit, and at the last
            // capital of a run that is followed by a lower-case letter (XMLHttpRequest is
            // xml-http-request). Existing separators, spaces, hyphens and underscores, end a word.
            var output = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c is ' ' or '-' or '_')
                {
                    if (output.Length > 0 && output[^1] != separator)
                    {
                        output.Append(separator);
                    }

                    continue;
                }

                if (char.IsUpper(c) && i > 0 && output.Length > 0 && output[^1] != separator)
                {
                    char before = name[i - 1];
                    bool afterLower = char.IsLower(before) || char.IsDigit(before);
                    bool endsRun = char.IsUpper(before) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if (afterLower || endsRun)
                    {
                        output.Append(separator);
                    }
                }

                output.Append(char.ToLowerInvariant(c));
            }

            return output.ToString().TrimEnd(separator);
        }
    }
}
