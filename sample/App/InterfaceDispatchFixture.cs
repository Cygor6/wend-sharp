namespace SampleApp;

/// <summary>Dispatch fixture for GetOverrides on an interface member: one implicit implementation
/// that is overridden further down, and one explicit implementation. All three can run when
/// IShape.Area() is called.</summary>
public interface IShape
{
    double Area();
}

/// <summary>Inherits Area from IShape without redeclaring it.</summary>
public interface INamedShape : IShape
{
    string Name { get; }
}

public class Square : IShape
{
    public virtual double Area() => 4.0;
}

public sealed class LargeSquare : Square
{
    public override double Area() => 16.0;
}

public class Circle : IShape
{
    double IShape.Area() => 3.14;
}
