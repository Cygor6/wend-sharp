namespace SampleApp;

public class MemberKindFixture
{
    private int _field;

    public int BodiedProperty
    {
        get { return _field; }
        set { _field = value; }
    }

    public int ExpressionBodiedProperty => Compute();

    public int InitializedProperty { get; } = 5;

    public int this[int index]
    {
        get { return _field + index; }
        set { _field = value + index; }
    }

    public static MemberKindFixture operator +(MemberKindFixture a, MemberKindFixture b) =>
        new MemberKindFixture { _field = a._field + b._field };

    public static implicit operator int(MemberKindFixture f) => f._field;

    ~MemberKindFixture()
    {
        _field = 0;
    }

    private int Compute() => _field * 2;
}
