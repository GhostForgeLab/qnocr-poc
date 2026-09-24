namespace QnocrPoc.Models;

public sealed record OcrBounds(int X, int Y, int Width, int Height);
public sealed record OcrLine(int Number, string Text, float? Confidence, OcrBounds Bounds);

public sealed record OcrResult(
    string ImagePath,
    string Model,
    IReadOnlyList<OcrLine> Lines,
    int DiscardedEmptyRegions,
    double? Confidence,
    double InitializationMilliseconds,
    double RecognitionMilliseconds,
    double TotalMilliseconds,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long ProcessPeakWorkingSetBytes)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(line => line.Text));
}
