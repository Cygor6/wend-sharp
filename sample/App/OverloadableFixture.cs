namespace SampleApp;

public abstract class OverloadableBase
{
    public abstract int Compute(int x);
    public abstract string Compute(string s);
}

public class OverloadableDerived : OverloadableBase
{
    public override int Compute(int x) => x * 2;
    public override string Compute(string s) => s.ToUpper();
}
