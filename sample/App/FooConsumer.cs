namespace SampleApp;

public static class FooConsumer
{
    public static string Describe()
    {
        var f = new Foo { Value = 1 };
        return f.Value.ToString();
    }
}
