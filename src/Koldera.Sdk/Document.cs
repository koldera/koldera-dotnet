using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Koldera;

/// <summary>A running piece of text: plain, strong, emphasised or a link. A string converts to plain text.</summary>
public abstract record Inline
{
    private protected Inline()
    {
    }

    /// <summary>Plain text.</summary>
    public static implicit operator Inline(string text) => new TextInline(text);
}

/// <summary>Plain text.</summary>
/// <param name="Text">The text.</param>
public sealed record TextInline(string Text) : Inline;

/// <summary>Strong text.</summary>
/// <param name="Text">The text to set strong.</param>
public sealed record StrongInline(string Text) : Inline;

/// <summary>Emphasised text.</summary>
/// <param name="Text">The text to emphasise.</param>
public sealed record EmphasisInline(string Text) : Inline;

/// <summary>An inline hyperlink. The text must describe the target and must not be blank.</summary>
/// <param name="Text">Visible link text.</param>
/// <param name="Url">Link target (http, https or mailto).</param>
public sealed record LinkInline(string Text, string Url) : Inline;

/// <summary>The items of a list, used for a nested list.</summary>
/// <param name="Items">List items in order.</param>
public sealed record ListBody(IReadOnlyList<ListItem> Items)
{
    /// <summary>True for a numbered list; omitted (bulleted) when null.</summary>
    public bool? Ordered { get; init; }
}

/// <summary>One list item: inline pieces of text, optionally followed by one nested list.</summary>
/// <param name="Content">Inline pieces of text.</param>
public sealed record ListItem(IReadOnlyList<Inline> Content)
{
    /// <summary>A text-only item.</summary>
    public ListItem(string text)
        : this([new TextInline(text)])
    {
    }

    /// <summary>A nested list placed after the text.</summary>
    public ListBody? Nested { get; init; }
}

/// <summary>A block of a structured document.</summary>
public abstract record Block
{
    private protected Block()
    {
    }
}

/// <summary>A heading. The first heading in the body is level 1 or 2; each later one is at most one level deeper.</summary>
/// <param name="Level">Heading level, 1 to 6.</param>
/// <param name="Text">Heading text.</param>
public sealed record HeadingBlock(int Level, string Text) : Block;

/// <summary>A paragraph of running text.</summary>
/// <param name="Content">Inline pieces of text in order.</param>
public sealed record ParagraphBlock(IReadOnlyList<Inline> Content) : Block
{
    /// <summary>A paragraph of plain text.</summary>
    public ParagraphBlock(string text)
        : this([new TextInline(text)])
    {
    }

    /// <summary>BCP 47 tag when the paragraph differs from the document language.</summary>
    public string? Lang { get; init; }

    /// <summary>A bundled font name when it differs from the theme font.</summary>
    public string? Font { get; init; }
}

/// <summary>A bulleted or numbered list.</summary>
/// <param name="Items">List items in order.</param>
public sealed record ListBlock(IReadOnlyList<ListItem> Items) : Block
{
    /// <summary>True for a numbered list; omitted (bulleted) when null.</summary>
    public bool? Ordered { get; init; }
}

/// <summary>A data table with a caption and at least one header row.</summary>
/// <param name="Caption">What the table shows.</param>
/// <param name="Header">Header rows; the first one sets the column count.</param>
/// <param name="Rows">Body rows.</param>
public sealed record TableBlock(string Caption, IReadOnlyList<IReadOnlyList<string>> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : Block
{
    /// <summary>True when the first cell of every body row is a row header.</summary>
    public bool? RowHeaders { get; init; }

    /// <summary>Horizontal alignment per column: left, center or right.</summary>
    public IReadOnlyList<string>? ColumnAlign { get; init; }
}

/// <summary>An image uploaded earlier with <see cref="IDocumentJobs.UploadImageAsync"/>; described by <see cref="Alt"/> or marked decorative.</summary>
/// <param name="UploadId">Id of the uploaded image.</param>
public sealed record ImageBlock(Guid UploadId) : Block
{
    /// <summary>Description for people who cannot see the image. Mutually exclusive with <see cref="Decorative"/>.</summary>
    public string? Alt { get; init; }

    /// <summary>True when the image carries no information. Mutually exclusive with <see cref="Alt"/>.</summary>
    public bool Decorative { get; init; }

    /// <summary>An image with a description.</summary>
    public static ImageBlock Described(Guid uploadId, string alt) => new(uploadId) { Alt = alt };

    /// <summary>An image that is hidden from assistive technology.</summary>
    public static ImageBlock AsDecoration(Guid uploadId) => new(uploadId) { Decorative = true };
}

/// <summary>A paragraph that consists of one link.</summary>
/// <param name="Text">Visible link text.</param>
/// <param name="Url">Link target (http, https or mailto).</param>
public sealed record LinkBlock(string Text, string Url) : Block;

/// <summary>A page break.</summary>
public sealed record PageBreakBlock : Block;

/// <summary>Vertical space, not announced by assistive technology.</summary>
/// <param name="Height">A length with a unit: mm, cm, in or pt, for example 8mm.</param>
public sealed record SpacerBlock(string Height) : Block;

/// <summary>A label and a value.</summary>
/// <param name="Label">The label.</param>
/// <param name="Value">The value.</param>
public sealed record KeyValueRow(string Label, string Value);

/// <summary>Label and value pairs, for example the parties of an invoice.</summary>
/// <param name="Rows">The pairs.</param>
public sealed record KeyValueBlock(IReadOnlyList<KeyValueRow> Rows) : Block;

/// <summary>The current page number, filled in by the renderer on every page. Only valid in a footer.</summary>
public sealed record PageNumberBlock : Block;

/// <summary>Page setup; every property is optional.</summary>
public sealed record DocumentPage
{
    /// <summary>A4, A5, Letter or Legal (default A4).</summary>
    public string? Size { get; init; }

    /// <summary>portrait or landscape (default portrait).</summary>
    public string? Orientation { get; init; }

    /// <summary>Margin on all four sides with a unit, for example 20mm.</summary>
    public string? Margins { get; init; }
}

/// <summary>Typography and colour; every property is optional.</summary>
public sealed record DocumentTheme
{
    /// <summary>One of the bundled fonts, for example Noto Sans.</summary>
    public string? Font { get; init; }

    /// <summary>Body font size in points, 6 to 24.</summary>
    public double? FontSize { get; init; }

    /// <summary>Accent colour as #rrggbb.</summary>
    public string? Accent { get; init; }
}

/// <summary>
/// The structured document of POST /v1/documents/jobs. Structure is declared, never inferred: headings,
/// table headers, image descriptions and languages are stated by the author.
/// </summary>
/// <param name="Title">Document title; at least one non-whitespace character.</param>
/// <param name="Language">Primary language as a BCP 47 tag, for example en or uk.</param>
/// <param name="Body">The content in reading order.</param>
public sealed record StructuredDocument(string Title, string Language, IReadOnlyList<Block> Body)
{
    /// <summary>Page setup.</summary>
    public DocumentPage? Page { get; init; }

    /// <summary>Typography and colour.</summary>
    public DocumentTheme? Theme { get; init; }

    /// <summary>Blocks repeated at the top of every page.</summary>
    public IReadOnlyList<Block>? Header { get; init; }

    /// <summary>Blocks repeated at the bottom of every page; may contain a <see cref="PageNumberBlock"/>.</summary>
    public IReadOnlyList<Block>? Footer { get; init; }

    internal string ToJson() => DocumentJson.Serialize(this);
}

internal static class DocumentJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(StructuredDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = new JsonObject
        {
            ["title"] = document.Title,
            ["language"] = document.Language,
        };
        if (document.Page is { } page)
        {
            root["page"] = Compact(("size", page.Size), ("orientation", page.Orientation), ("margins", page.Margins));
        }

        if (document.Theme is { } theme)
        {
            var node = Compact(("font", theme.Font), ("accent", theme.Accent));
            if (theme.FontSize is { } size)
            {
                node["fontSize"] = size;
            }

            root["theme"] = node;
        }

        if (document.Header is { } header)
        {
            root["header"] = Blocks(header, allowPageNumber: false);
        }

        if (document.Footer is { } footer)
        {
            root["footer"] = Blocks(footer, allowPageNumber: true);
        }

        root["body"] = Blocks(document.Body ?? throw new ConfigurationException("document.Body is required"), allowPageNumber: false);
        return Write(root);
    }

    public static string Write(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            node.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonObject Compact(params (string Name, string? Value)[] members)
    {
        var node = new JsonObject();
        foreach (var (name, value) in members)
        {
            if (value is not null)
            {
                node[name] = value;
            }
        }

        return node;
    }

    private static JsonArray Blocks(IReadOnlyList<Block> blocks, bool allowPageNumber)
    {
        var array = new JsonArray();
        foreach (var block in blocks)
        {
            Push(array, BlockNode(block, allowPageNumber));
        }

        return array;
    }

    private static JsonObject BlockNode(Block block, bool allowPageNumber)
    {
        switch (block)
        {
            case HeadingBlock h:
                return new JsonObject { ["type"] = "heading", ["level"] = h.Level, ["text"] = h.Text };
            case ParagraphBlock p:
                {
                    var node = new JsonObject { ["type"] = "paragraph", ["content"] = Inlines(p.Content) };
                    Set(node, "lang", p.Lang);
                    Set(node, "font", p.Font);
                    return node;
                }

            case ListBlock l:
                {
                    var node = new JsonObject { ["type"] = "list" };
                    if (l.Ordered is { } ordered)
                    {
                        node["ordered"] = ordered;
                    }

                    node["items"] = Items(l.Items);
                    return node;
                }

            case TableBlock t:
                {
                    var node = new JsonObject { ["type"] = "table", ["caption"] = t.Caption, ["header"] = Rows(t.Header), ["rows"] = Rows(t.Rows) };
                    if (t.RowHeaders is { } rowHeaders)
                    {
                        node["rowHeaders"] = rowHeaders;
                    }

                    if (t.ColumnAlign is { } align)
                    {
                        node["columnAlign"] = Strings(align);
                    }

                    return node;
                }

            case ImageBlock i:
                {
                    if (i.Alt is not null && i.Decorative)
                    {
                        throw new ConfigurationException("an image has either Alt or Decorative, not both");
                    }

                    var node = new JsonObject { ["type"] = "image", ["uploadId"] = i.UploadId.ToString("D") };
                    Set(node, "alt", i.Alt);
                    if (i.Decorative)
                    {
                        node["decorative"] = true;
                    }

                    return node;
                }

            case LinkBlock k:
                return new JsonObject { ["type"] = "link", ["text"] = k.Text, ["url"] = k.Url };
            case PageBreakBlock:
                return new JsonObject { ["type"] = "pageBreak" };
            case SpacerBlock s:
                return new JsonObject { ["type"] = "spacer", ["height"] = s.Height };
            case KeyValueBlock kv:
                {
                    var rows = new JsonArray();
                    foreach (var row in kv.Rows)
                    {
                        Push(rows, new JsonArray(JsonValue.Create(row.Label), JsonValue.Create(row.Value)));
                    }

                    return new JsonObject { ["type"] = "keyValue", ["rows"] = rows };
                }

            case PageNumberBlock when allowPageNumber:
                return new JsonObject { ["type"] = "pageNumber" };
            case PageNumberBlock:
                throw new ConfigurationException("a page number block is only valid in the footer");
            case null:
                throw new ConfigurationException("a block must not be null");
            default:
                throw new ConfigurationException($"unknown block type {block.GetType().Name}");
        }
    }

    private static void Set(JsonObject node, string name, string? value)
    {
        if (value is not null)
        {
            node[name] = value;
        }
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            Push(array, JsonValue.Create(value));
        }

        return array;
    }

    private static JsonArray Rows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var array = new JsonArray();
        foreach (var row in rows)
        {
            Push(array, Strings(row));
        }

        return array;
    }

    private static JsonArray Inlines(IReadOnlyList<Inline> inlines)
    {
        var array = new JsonArray();
        foreach (var inline in inlines)
        {
            Push(array, InlineNode(inline));
        }

        return array;
    }

    private static JsonNode InlineNode(Inline inline) => inline switch
    {
        TextInline t => JsonValue.Create(t.Text)!,
        StrongInline s => new JsonObject { ["strong"] = s.Text },
        EmphasisInline e => new JsonObject { ["emphasis"] = e.Text },
        LinkInline l => new JsonObject { ["link"] = new JsonObject { ["text"] = l.Text, ["url"] = l.Url } },
        null => throw new ConfigurationException("an inline must not be null"),
        _ => throw new ConfigurationException($"unknown inline type {inline.GetType().Name}"),
    };

    private static JsonArray Items(IReadOnlyList<ListItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var node = Inlines(item.Content);
            if (item.Nested is { } nested)
            {
                var body = new JsonObject();
                if (nested.Ordered is { } ordered)
                {
                    body["ordered"] = ordered;
                }

                body["items"] = Items(nested.Items);
                Push(node, new JsonObject { ["list"] = body });
            }

            Push(array, node);
        }

        return array;
    }

    private static void Push(JsonArray array, JsonNode? node) => array.Add(node);
}
