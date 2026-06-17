namespace SampleApp;

public class OverloadExample
{
    public int Calculate(int x) => x * 2;

    public int Calculate(string s) => s.Length;
}
