namespace SampleApp;

public class PropertyFlowExample
{
    private int _backing;

    public int BlockProperty
    {
        get { return _backing; }
        set { _backing = value; }
    }

    public int InitOnlyProperty
    {
        init { _backing = value; }
    }

    public int AutoProperty { get; set; }
}
