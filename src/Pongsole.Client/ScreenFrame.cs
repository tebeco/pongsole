using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pongsole.Client;

internal sealed class ScreenFrame(int width, int height, bool styled = true) : IRenderable
{
    private readonly List<List<Segment>> rows = [];

    public void Add(params Segment[] parts) => rows.Add([.. parts]);

    public static ScreenFrame Plain(int width, int height, params string[] lines)
    {
        var frame = new ScreenFrame(width, height, false);
        foreach (var line in lines)
        {
            frame.Add(new Segment(new string(line.Where(character => !char.IsControl(character)).ToArray())));
        }

        return frame;
    }

    public string[] Lines => [.. RenderRows(width).Select(row => string.Concat(row.Select(segment => segment.Text)))];

    public Measurement Measure(RenderOptions options, int maxWidth)
    {
        var cells = RenderRows(maxWidth).Select(Segment.CellCount).DefaultIfEmpty().Max();
        return new Measurement(cells, cells);
    }

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var first = true;
        foreach (var row in RenderRows(maxWidth))
        {
            if (!first) yield return Segment.LineBreak;
            first = false;
            foreach (var segment in row)
                yield return styled ? segment : new Segment(segment.Text);
        }
    }

    private IEnumerable<List<Segment>> RenderRows(int maxWidth)
    {
        foreach (var row in rows.Take(Math.Max(0, height - 1)))
        {
            var remaining = Math.Max(0, Math.Min(width - 1, maxWidth));
            List<Segment> result = [];
            foreach (var segment in row)
            {
                if (remaining == 0) break;
                var cells = segment.CellCount();
                if (cells <= remaining)
                {
                    result.Add(segment);
                    remaining -= cells;
                    continue;
                }

                var elements = StringInfo.GetTextElementEnumerator(segment.Text);
                var characters = 0;
                while (elements.MoveNext())
                {
                    var element = elements.GetTextElement();
                    var length = new Segment(element).CellCount();
                    if (length > remaining) break;
                    remaining -= length;
                    characters += element.Length;
                }
                if (characters > 0) result.Add(new Segment(segment.Text[..characters], segment.Style));
                break;
            }
            yield return result;
        }
    }
}
