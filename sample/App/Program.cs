namespace SampleApp;

public static class Program
{
    public static void Main()
    {
        var sum = Calculator.Add(2, 3);
        var diff = Calculator.Subtract(10, 4);
        var total = Calculator.Add(sum, diff);
        Console.WriteLine(total);
    }
}
