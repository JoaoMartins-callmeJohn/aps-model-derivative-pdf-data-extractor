using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace ApsMdExtractor.Services;

/// <summary>One piece of text found on a 2D view, in page coordinates.</summary>
public record TextRun(int Page, string Content, double X, double Y, double Width, double Height, double Rotation, double FontSize, string Source);

/// <summary>Extracts text lines, with position and size, from a PDF using PdfPig.</summary>
public static class PdfTextExtractor
{
    public const string Source = "pdf-derivative";

    /// <param name="pdf">The PDF bytes.</param>
    /// <param name="pageNumber">1-based page to read, or null for all pages.</param>
    /// <param name="pageCount">Number of pages in the PDF.</param>
    public static List<TextRun> Extract(byte[] pdf, int? pageNumber, out int pageCount)
    {
        var runs = new List<TextRun>();
        using var document = PdfDocument.Open(pdf);
        pageCount = document.NumberOfPages;
        IEnumerable<Page> pages = pageNumber is int n && n >= 1 && n <= pageCount
            ? [document.GetPage(n)]
            : document.GetPages();

        foreach (var page in pages)
        {
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
            if (words.Count == 0)
            {
                continue;
            }
            // Group words into lines so each text run reads like the label on the drawing.
            foreach (var block in DocstrumBoundingBoxes.Instance.GetBlocks(words))
            {
                foreach (var line in block.TextLines)
                {
                    var text = line.Text?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }
                    var box = line.BoundingBox;
                    var letters = line.Words.SelectMany(w => w.Letters).ToList();
                    var fontSize = letters.Count > 0 ? letters.Average(l => l.PointSize) : 0;
                    runs.Add(new TextRun(page.Number, text, box.Left, box.Bottom, box.Width, box.Height,
                        Math.Round(box.Rotation, 3), Math.Round(fontSize, 3), Source));
                }
            }
        }
        return runs;
    }
}
