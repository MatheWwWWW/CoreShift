using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = System.Windows.Documents.Block;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;
using MarkdownTable = Markdig.Extensions.Tables.Table;
using MarkdownTableRow = Markdig.Extensions.Tables.TableRow;
using MarkdownTableCell = Markdig.Extensions.Tables.TableCell;

namespace CoreShift.Desktop;

internal static class ReleaseNotesMarkdown
{
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder { MaximumNestingDepth = 24 }
        .UseAutoLinks().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).UsePipeTables().UseTaskLists().Build();

    internal static void Render(FlowDocument document, string markdown) =>
        AddBlocks(document.Blocks, Markdown.Parse(markdown, Pipeline));

    private static void AddBlocks(BlockCollection destination, ContainerBlock source)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = new Paragraph
                    {
                        FontSize = heading.Level switch { 1 => 24, 2 => 19, _ => 16 },
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    AddInlines(title.Inlines, heading.Inline);
                    destination.Add(title);
                    break;
                case ParagraphBlock body:
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                    AddInlines(paragraph.Inlines, body.Inline);
                    if (paragraph.Inlines.Count > 0)
                        destination.Add(paragraph);
                    break;
                case ListBlock list:
                    destination.Add(RenderList(list));
                    break;
                case QuoteBlock quote:
                    var section = new Section
                    {
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        Padding = new Thickness(12, 0, 0, 0),
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    section.SetResourceReference(Block.BorderBrushProperty, "Violet");
                    section.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                    AddBlocks(section.Blocks, quote);
                    destination.Add(section);
                    break;
                case CodeBlock code:
                    var codeBlock = new Paragraph(new Run(code.Lines.ToString()))
                    {
                        FontFamily = CodeFont,
                        FontSize = 13,
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 0, 0, 12)
                    };
                    codeBlock.SetResourceReference(TextElement.BackgroundProperty, "Raised");
                    destination.Add(codeBlock);
                    break;
                case ThematicBreakBlock:
                    var rule = new Paragraph
                    {
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        LineHeight = 1,
                        Margin = new Thickness(0, 4, 0, 12)
                    };
                    rule.SetResourceReference(Block.BorderBrushProperty, "Line");
                    destination.Add(rule);
                    break;
                case MarkdownTable table:
                    destination.Add(RenderTable(table));
                    break;
            }
        }
    }

    private static List RenderList(ListBlock source)
    {
        var list = new List
        {
            MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Padding = new Thickness(24, 0, 0, 0),
            Margin = new Thickness(0)
        };
        if (source.IsOrdered && int.TryParse(source.OrderedStart, out int start))
            list.StartIndex = Math.Max(1, start);

        foreach (ListItemBlock item in source)
        {
            var listItem = new ListItem();
            AddBlocks(listItem.Blocks, item);
            list.ListItems.Add(listItem);
        }
        return list;
    }

    private static Table RenderTable(MarkdownTable source)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12) };
        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);
        foreach (MarkdownTableRow sourceRow in source)
        {
            var row = new TableRow();
            if (sourceRow.IsHeader)
            {
                row.FontWeight = FontWeights.SemiBold;
                row.SetResourceReference(TextElement.BackgroundProperty, "Raised");
            }
            foreach (MarkdownTableCell sourceCell in sourceRow)
            {
                var cell = new TableCell
                {
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 4, 8, 4)
                };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Line");
                AddBlocks(cell.Blocks, sourceCell);
                foreach (Block block in cell.Blocks)
                    block.Margin = new Thickness(0);
                row.Cells.Add(cell);
            }
            rows.Rows.Add(row);
        }
        return table;
    }

    private static void AddInlines(InlineCollection destination, ContainerInline? source)
    {
        for (MarkdownInline? inline = source?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    destination.Add(new Run(literal.Content.ToString()));
                    break;
                case HtmlEntityInline entity:
                    destination.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case LineBreakInline lineBreak:
                    destination.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case CodeInline code:
                    var codeRun = new Run(code.Content) { FontFamily = CodeFont, FontSize = 13 };
                    codeRun.SetResourceReference(TextElement.BackgroundProperty, "Raised");
                    destination.Add(codeRun);
                    break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterChar switch
                    {
                        '~' => new Span { TextDecorations = TextDecorations.Strikethrough },
                        _ when emphasis.DelimiterCount == 2 => new Bold(),
                        _ => new Italic()
                    };
                    AddInlines(span.Inlines, emphasis);
                    destination.Add(span);
                    break;
                case LinkInline link:
                    if (!link.IsImage && WebUri(link.Url) is { } uri)
                    {
                        var hyperlink = CreateLink(uri);
                        AddInlines(hyperlink.Inlines, link);
                        destination.Add(hyperlink);
                    }
                    else
                    {
                        AddInlines(destination, link);
                    }
                    break;
                case AutolinkInline autoLink:
                    var label = new Run(autoLink.Url);
                    if (WebUri(autoLink.Url) is { } autoUri)
                    {
                        var hyperlink = CreateLink(autoUri);
                        hyperlink.Inlines.Add(label);
                        destination.Add(hyperlink);
                    }
                    else
                    {
                        destination.Add(label);
                    }
                    break;
                case TaskList task:
                    destination.Add(new Run(task.Checked ? "☑ " : "☐ "));
                    break;
            }
        }
    }

    private static Uri? WebUri(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;

    private static Hyperlink CreateLink(Uri uri)
    {
        var hyperlink = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Violet");
        hyperlink.RequestNavigate += OpenLink;
        return hyperlink;
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
