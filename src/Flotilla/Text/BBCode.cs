using System.Text.RegularExpressions;

namespace Flotilla.Text;

public sealed class BbNode(string tag, string? value = null)
{
    public string Tag { get; } = tag;
    public string? Value { get; set; } = value;
    public List<BbNode> Children { get; } = [];

    public bool IsText => Tag.Length == 0;
    public string PlainText => IsText ? Value ?? "" : string.Concat(Children.Select(c => c.PlainText));

    public string Prose => Tag switch
    {
        "" => Value ?? "",
        "img" or "hr" or "code" or "table" or "h1" or "h2" or "h3" => " ",
        "url" when Value is null => " ",
        "*" => string.Concat(Children.Select(c => c.Prose)) + " ",
        _ => string.Concat(Children.Select(c => c.Prose)),
    };
}

public static class BBCode
{
    static readonly Regex Tag = new(@"\[(/?)(\*|[a-z][a-z0-9]*)(?:=([^\]\n]*))?\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly HashSet<string> Known =
    [
        "b", "i", "u", "strike", "h1", "h2", "h3", "url", "img", "list", "olist", "*",
        "quote", "code", "hr", "spoiler", "noparse", "table", "tr", "td", "th",
    ];

    public static BbNode Parse(string source)
    {
        source = source.Replace("\r\n", "\n");
        var root = new BbNode("root");
        var open = new List<BbNode> { root };
        var at = 0;

        foreach (Match match in Tag.Matches(source))
        {
            if (match.Index < at) continue;

            Append(open[^1], source[at..match.Index]);
            at = match.Index + match.Length;

            var name = match.Groups[2].Value.ToLowerInvariant();
            if (name == "s") name = "strike";
            var closing = match.Groups[1].Length > 0;

            if (!Known.Contains(name))
            {
                Append(open[^1], match.Value);
                continue;
            }

            switch (name)
            {
                case "noparse":
                    if (closing) break;
                    var end = source.IndexOf("[/noparse]", at, StringComparison.OrdinalIgnoreCase);
                    Append(open[^1], end < 0 ? source[at..] : source[at..end]);
                    at = end < 0 ? source.Length : end + "[/noparse]".Length;
                    break;

                case "hr":
                    if (!closing) open[^1].Children.Add(new BbNode("hr"));
                    break;

                case "*":
                    if (closing) break;
                    var list = open.FindLastIndex(n => n.Tag is "list" or "olist");
                    if (list < 0)
                    {
                        Append(open[^1], match.Value);
                        break;
                    }
                    open.RemoveRange(list + 1, open.Count - list - 1);
                    Push(open, new BbNode("*"));
                    break;

                default:
                    if (!closing)
                    {
                        Push(open, new BbNode(name, match.Groups[3].Success ? match.Groups[3].Value.Trim(' ', '"', '\'') : null));
                        break;
                    }
                    var index = open.FindLastIndex(n => n.Tag == name);
                    if (index > 0) open.RemoveRange(index, open.Count - index);
                    else Append(open[^1], match.Value);
                    break;
            }
        }

        Append(open[^1], source[at..]);
        return root;
    }

    static void Push(List<BbNode> open, BbNode node)
    {
        open[^1].Children.Add(node);
        open.Add(node);
    }

    static void Append(BbNode parent, string text)
    {
        if (text.Length == 0) return;

        if (parent.Children.Count > 0 && parent.Children[^1].IsText) parent.Children[^1].Value += text;
        else parent.Children.Add(new BbNode("", text));
    }
}
