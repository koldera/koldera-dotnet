using System.Text.Json.Nodes;

namespace Koldera.Tests;

public sealed class DocumentTests
{
    private static readonly Guid Upload = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static JsonObject Json(StructuredDocument document) => (JsonObject)JsonNode.Parse(document.ToJson())!;

    [Fact]
    public void AMinimalDocumentHasOnlyTitleLanguageAndBody()
    {
        var json = Json(new StructuredDocument("T", "uk", [new HeadingBlock(1, "H")]));
        Assert.Equal(["body", "language", "title"], json.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("""{"type":"heading","level":1,"text":"H"}""", json["body"]![0]!.ToJsonString());
    }

    [Fact]
    public void PageThemeHeaderAndFooterAreWrittenOnlyWhenSet()
    {
        var json = Json(new StructuredDocument("T", "en", [new PageBreakBlock()])
        {
            Page = new DocumentPage { Size = "A4", Margins = "20mm" },
            Theme = new DocumentTheme { Font = "Noto Serif", FontSize = 11.5, Accent = "#112233" },
            Header = [new ParagraphBlock("top")],
            Footer = [new ParagraphBlock("bottom"), new PageNumberBlock()],
        });
        Assert.Equal("""{"size":"A4","margins":"20mm"}""", json["page"]!.ToJsonString());
        Assert.Equal("""{"font":"Noto Serif","accent":"#112233","fontSize":11.5}""", json["theme"]!.ToJsonString());
        Assert.Equal("""[{"type":"paragraph","content":["top"]}]""", json["header"]!.ToJsonString());
        Assert.Equal("pageNumber", (string?)json["footer"]![1]!["type"]);
        Assert.Equal("pageBreak", (string?)json["body"]![0]!["type"]);
    }

    [Fact]
    public void InlinesAreWrittenAsTheSchemaDemands()
    {
        var json = Json(new StructuredDocument("T", "en",
        [
            new ParagraphBlock(["plain ", new StrongInline("s"), new EmphasisInline("e"), new LinkInline("docs", "https://example.com/")]) { Lang = "de", Font = "Noto Sans" },
        ]));
        Assert.Equal(
            """{"type":"paragraph","content":["plain ",{"strong":"s"},{"emphasis":"e"},{"link":{"text":"docs","url":"https://example.com/"}}],"lang":"de","font":"Noto Sans"}""",
            json["body"]![0]!.ToJsonString());
    }

    [Fact]
    public void ListsNestLastAndWriteOrderedOnlyWhenSet()
    {
        var json = Json(new StructuredDocument("T", "en",
        [
            new ListBlock([
                new ListItem("one"),
                new ListItem(["two ", new StrongInline("bold")]) { Nested = new ListBody([new ListItem("inner")]) { Ordered = true } },
            ]) { Ordered = false },
            new ListBlock([new ListItem("x")]),
        ]));
        Assert.Equal(
            """{"type":"list","ordered":false,"items":[["one"],["two ",{"strong":"bold"},{"list":{"ordered":true,"items":[["inner"]]}}]]}""",
            json["body"]![0]!.ToJsonString());
        Assert.Equal("""{"type":"list","items":[["x"]]}""", json["body"]![1]!.ToJsonString());
    }

    [Fact]
    public void TablesImagesLinksSpacersAndKeyValuesMatchTheSchema()
    {
        var json = Json(new StructuredDocument("T", "en",
        [
            new TableBlock("Cap", [["A", "B"]], [["1", "2"]]) { RowHeaders = true, ColumnAlign = ["left", "right"] },
            ImageBlock.Described(Upload, "a blue square"),
            ImageBlock.AsDecoration(Upload),
            new ImageBlock(Upload),
            new LinkBlock("home", "mailto:a@example.com"),
            new SpacerBlock("8mm"),
            new KeyValueBlock([new KeyValueRow("Total", "10")]),
        ]));
        var body = json["body"]!.AsArray();
        Assert.Equal("""{"type":"table","caption":"Cap","header":[["A","B"]],"rows":[["1","2"]],"rowHeaders":true,"columnAlign":["left","right"]}""", body[0]!.ToJsonString());
        Assert.Equal("""{"type":"image","uploadId":"33333333-3333-4333-8333-333333333333","alt":"a blue square"}""", body[1]!.ToJsonString());
        Assert.Equal("""{"type":"image","uploadId":"33333333-3333-4333-8333-333333333333","decorative":true}""", body[2]!.ToJsonString());
        Assert.Equal("""{"type":"image","uploadId":"33333333-3333-4333-8333-333333333333"}""", body[3]!.ToJsonString());
        Assert.Equal("""{"type":"link","text":"home","url":"mailto:a@example.com"}""", body[4]!.ToJsonString());
        Assert.Equal("""{"type":"spacer","height":"8mm"}""", body[5]!.ToJsonString());
        Assert.Equal("""{"type":"keyValue","rows":[["Total","10"]]}""", body[6]!.ToJsonString());
    }

    [Fact]
    public void AnEmptyAltIsSentSoTheServerCanReportIt()
    {
        var json = Json(new StructuredDocument("T", "en", [ImageBlock.Described(Upload, "")]));
        Assert.Equal("", (string?)json["body"]![0]!["alt"]);
    }

    [Fact]
    public void NonAsciiTextIsWrittenAsItIs()
    {
        var text = new StructuredDocument("Звіт", "uk", [new ParagraphBlock("Привіт, світе")]).ToJson();
        Assert.Contains("Звіт", text, StringComparison.Ordinal);
        Assert.Contains("Привіт, світе", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ImpossibleCombinationsAreRefusedLocally()
    {
        Assert.Throws<ConfigurationException>(() => new StructuredDocument("T", "en", [new ImageBlock(Upload) { Alt = "x", Decorative = true }]).ToJson());
        Assert.Throws<ConfigurationException>(() => new StructuredDocument("T", "en", [new PageNumberBlock()]).ToJson());
        Assert.Throws<ConfigurationException>(() => new StructuredDocument("T", "en", [new ParagraphBlock("x")]) { Header = [new PageNumberBlock()] }.ToJson());
    }

    [Fact]
    public async Task ARawJsonDocumentIsSentAsItIs()
    {
        using var env = new Env();
        env.Handler.Expect("document-job-accepted");
        var document = (JsonObject)JsonNode.Parse(GoldenHandler.Get("document-job-accepted").Request.Body!.ToJsonString())!;
        var job = await env.Client.Documents.CreateJobAsync(document);
        Assert.Equal(JobKinds.DocumentToPdf, job.Kind);
    }
}
