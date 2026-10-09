using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Flotilla.UI;

namespace Flotilla.Text;

public static class BbDocument
{
    public static FlowDocument Build(string bbcode)
    {
        var document = new FlowDocument
        {
            FontFamily = Resource<FontFamily>("Body"),
            FontSize = 15,
            LineHeight = 24,
            Foreground = Resource<Brush>("Text"),
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };
        new Writer(document.Blocks, 12).Children(BBCode.Parse(bbcode));
        return document;
    }

    static T Resource<T>(string key) => (T)Application.Current.FindResource(key);

    sealed class Writer(BlockCollection blocks, double gap)
    {
        readonly Stack<Span> spans = new();
        Paragraph? paragraph;
        int newlines;

        public void Children(BbNode node)
        {
            foreach (var child in node.Children) Write(child);
        }

        void Write(BbNode node)
        {
            switch (node.Tag)
            {
                case "": Text(node.Value ?? ""); break;
                case "b": Wrap(new Bold(), node); break;
                case "i": Wrap(new Italic(), node); break;
                case "u": Wrap(new Underline(), node); break;
                case "strike": Wrap(new Span { TextDecorations = TextDecorations.Strikethrough }, node); break;
                case "spoiler": Wrap(new Span { Foreground = Resource<Brush>("Muted") }, node); break;
                case "url": Link(node); break;
                case "h1" or "h2" or "h3": Heading(node); break;
                case "list" or "olist": List(node); break;
                case "quote": Quote(node); break;
                case "code": Code(node); break;
                case "hr": Rule(); break;
                case "img": Picture(node.Value ?? node.PlainText); break;
                case "previewyoutube": Video(node); break;
                case "table": Table(node); break;
                default: Children(node); break;
            }
        }

        void Text(string text)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0 && paragraph is not null) newlines++;
                if (lines[i].Length > 0) Add(new Run(lines[i]));
            }
        }

        void Add(Inline inline)
        {
            if (newlines > 1) End();
            else if (newlines == 1) Inlines.Add(new LineBreak());
            newlines = 0;
            Inlines.Add(inline);
        }

        InlineCollection Inlines => spans.Count > 0 ? spans.Peek().Inlines : Open().Inlines;

        Paragraph Open()
        {
            if (paragraph is null)
            {
                paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, gap) };
                blocks.Add(paragraph);
            }
            return paragraph;
        }

        void End()
        {
            paragraph = null;
            spans.Clear();
            newlines = 0;
        }

        void Block(Block block)
        {
            End();
            blocks.Add(block);
        }

        void Wrap(Span span, BbNode node)
        {
            Add(span);
            var depth = spans.Count;
            spans.Push(span);
            Children(node);
            while (spans.Count > depth) spans.Pop();
        }

        void Link(BbNode node)
        {
            var target = (node.Value ?? node.PlainText).Trim();
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                Children(node);
                return;
            }

            var link = new Hyperlink { NavigateUri = uri, Foreground = Resource<Brush>("AccentText"), ToolTip = uri.ToString() };
            link.Click += (_, _) => Shell.Open(uri.ToString());
            Wrap(link, node);
        }

        void Heading(BbNode node)
        {
            var size = node.Tag switch { "h1" => 21.0, "h2" => 18.0, _ => 16.0 };
            var heading = new Paragraph
            {
                FontFamily = Resource<FontFamily>("Display"),
                FontSize = size,
                FontWeight = FontWeights.Bold,
                LineHeight = size * 1.3,
                Margin = new Thickness(0, 14, 0, 8),
            };
            Block(heading);
            paragraph = heading;
            Children(node);
            End();
        }

        void List(BbNode node)
        {
            var list = new List
            {
                MarkerStyle = node.Tag == "olist" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 0, 0, gap),
                Padding = new Thickness(22, 0, 0, 0),
            };
            foreach (var item in node.Children.Where(c => c.Tag == "*"))
            {
                var entry = new ListItem();
                new Writer(entry.Blocks, 4).Children(item);
                list.ListItems.Add(entry);
            }
            Block(list);
        }

        void Quote(BbNode node)
        {
            var section = new Section
            {
                BorderBrush = Resource<Brush>("Line"),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(16, 2, 0, 2),
                Margin = new Thickness(0, 0, 0, gap),
                Foreground = Resource<Brush>("Muted"),
            };
            Block(section);
            new Writer(section.Blocks, 8).Children(node);
        }

        void Code(BbNode node) => Block(new Paragraph(new Run(node.PlainText.Trim('\n')))
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Background = Resource<Brush>("Raised"),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, gap),
        });

        void Rule() => Block(new BlockUIContainer(new Border
        {
            Height = 1,
            Background = Resource<Brush>("Line"),
            Margin = new Thickness(0, 6, 0, 6),
        }));

        void Picture(string url)
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxHeight = 720,
            };
            Block(new BlockUIContainer(image) { Margin = new Thickness(0, 4, 0, gap) });
            _ = Show(image, url.Trim());
        }

        static async Task Show(Image image, string url) => image.Source = await Images.Load(url, 1200);

        void Video(BbNode node)
        {
            if (BBCode.YouTubeId(node) is not { } id) return;

            var watch = $"https://www.youtube.com/watch?v={id}";
            var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var play = new Border
            {
                Width = 68,
                Height = 48,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x14, 0x14)),
                BorderBrush = Resource<Brush>("AccentRing"),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,0 L16,9 L0,18 Z"),
                    Fill = Resource<Brush>("Accent"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                },
            };
            var frame = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Resource<Brush>("Raised"),
                ClipToBounds = true,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Watch on YouTube",
                Children = { image, play },
            };
            frame.MouseLeftButtonUp += (_, _) => Shell.Open(watch);

            var slot = new Border { Child = frame };
            slot.SizeChanged += (_, e) =>
            {
                frame.Width = Math.Min(640, e.NewSize.Width);
                frame.Height = frame.Width * 9 / 16;
            };

            Block(new BlockUIContainer(slot) { Margin = new Thickness(0, 4, 0, gap) });
            _ = Show(image, $"https://img.youtube.com/vi/{id}/hqdefault.jpg");
        }

        void Table(BbNode node)
        {
            var rows = node.Children.Where(c => c.Tag == "tr").ToList();
            var columns = rows.Select(r => r.Children.Count(c => c.Tag is "td" or "th")).DefaultIfEmpty(0).Max();
            if (columns == 0)
            {
                Children(node);
                return;
            }

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, gap) };
            for (var i = 0; i < columns; i++) table.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            foreach (var row in rows)
            {
                var line = new TableRow();
                foreach (var cell in row.Children.Where(c => c.Tag is "td" or "th"))
                {
                    var content = new TableCell
                    {
                        Padding = new Thickness(0, 6, 14, 6),
                        BorderBrush = Resource<Brush>("Line"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        FontWeight = cell.Tag == "th" ? FontWeights.SemiBold : FontWeights.Normal,
                    };
                    new Writer(content.Blocks, 0).Children(cell);
                    line.Cells.Add(content);
                }
                group.Rows.Add(line);
            }
            table.RowGroups.Add(group);
            Block(table);
        }
    }
}
