namespace SampleApp;

public readonly record struct Point(int X, int Y);

public sealed record Tag(string Key, string Value)
{
    public string Upper => Key.ToUpperInvariant();
}

public class Counter
{
    public Counter(int start) { Value = start; }
    public int Value { get; set; }
    public void Increment() => Value++;
}
