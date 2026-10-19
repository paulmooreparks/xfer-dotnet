using System;
using System.Collections.Generic;
using ParksComputing.XferLang.Serialization;

namespace ParksComputing.XferLang.Tests.Engine;

// Copies of the parkscomputing.com engine's NavNode (Pages/Services/NavNode.cs), the first consumer
// of this library: one with the legacy 0.x library's attributes, one with this library's, as the
// engine will have it after the migration (DESIGN.md section 15, step 3). The two are compared on
// the live site's documents.

#pragma warning disable CA1819 // properties return arrays: the engine's type does
#pragma warning disable CA2227 // collection properties have setters: the engine's type does

public class LegacyNavNode
{
    public LegacyNavNode()
    {
        Nav = [];
    }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("slug")] public string? Slug { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("title")] public string? Title { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("description")] public string? Description { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("excerpt")] public string? Excerpt { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("url")] public string? Url { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("link")]
    public string? Link
    {
        get => Url;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Url = value;
            }
        }
    }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("target")] public string? Target { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("date")] public DateTime? Date { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("updated")] public DateTime? Updated { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("order")] public int? Order { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("external")] public bool External { get; set; }

    public bool DerivedUrl { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("tags")] public string[]? Tags { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("win")] public string? Win { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("min")] public string? Min { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("max")] public string? Max { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("frame")] public bool Frame { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("icon")] public string? Icon { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("nav")] public LegacyNavNode[]? Nav { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("links")]
    public LegacyNavNode[]? Links
    {
        get => Nav;
        set
        {
            if (value != null)
            {
                Nav = value;
            }
        }
    }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("feed")] public bool Feed { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("menu")] public LegacyNavNode[]? Menu { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("menu-id")] public string? MenuId { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("window-commands")] public bool WindowCommands { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("heading")] public string? Heading { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("separator")] public bool Separator { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("command")] public string? Command { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("args")] public Dictionary<string, string>? Args { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("when")] public string? When { get; set; }

    [ParksComputing.Xfer.Lang.Attributes.XferProperty("from")] public string? From { get; set; }
}

public class NavNode
{
    public NavNode()
    {
        Nav = [];
    }

    [XferPropertyName("slug")] public string? Slug { get; set; }

    [XferPropertyName("title")] public string? Title { get; set; }

    [XferPropertyName("description")] public string? Description { get; set; }

    [XferPropertyName("excerpt")] public string? Excerpt { get; set; }

    // The legacy "link" key is accepted on reading and never written.
    [XferPropertyName("url", Aliases = ["link"])] public string? Url { get; set; }

    [XferIgnore]
    public string? Link
    {
        get => Url;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Url = value;
            }
        }
    }

    [XferPropertyName("target")] public string? Target { get; set; }

    [XferPropertyName("date")] public DateTime? Date { get; set; }

    [XferPropertyName("updated")] public DateTime? Updated { get; set; }

    [XferPropertyName("order")] public int? Order { get; set; }

    [XferPropertyName("external")] public bool External { get; set; }

    [XferIgnore] public bool DerivedUrl { get; set; }

    [XferPropertyName("tags")] public string[]? Tags { get; set; }

    [XferPropertyName("win")] public string? Win { get; set; }

    [XferPropertyName("min")] public string? Min { get; set; }

    [XferPropertyName("max")] public string? Max { get; set; }

    [XferPropertyName("frame")] public bool Frame { get; set; }

    [XferPropertyName("icon")] public string? Icon { get; set; }

    [XferPropertyName("nav", Aliases = ["links"])] public NavNode[]? Nav { get; set; }

    [XferIgnore]
    public NavNode[]? Links
    {
        get => Nav;
        set
        {
            if (value != null)
            {
                Nav = value;
            }
        }
    }

    [XferPropertyName("feed")] public bool Feed { get; set; }

    [XferPropertyName("menu")] public NavNode[]? Menu { get; set; }

    [XferPropertyName("menu-id")] public string? MenuId { get; set; }

    [XferPropertyName("window-commands")] public bool WindowCommands { get; set; }

    [XferPropertyName("heading")] public string? Heading { get; set; }

    [XferPropertyName("separator")] public bool Separator { get; set; }

    [XferPropertyName("command")] public string? Command { get; set; }

    [XferPropertyName("args")] public Dictionary<string, string>? Args { get; set; }

    [XferPropertyName("when")] public string? When { get; set; }

    [XferPropertyName("from")] public string? From { get; set; }
}
