namespace SampleApp;

public class OrderService
{
    readonly decimal _taxRate = 0.2m;

    public decimal CalculateTotal(decimal orderAmount)
    {
        var tax = orderAmount * _taxRate;
        return orderAmount + tax;
    }

    public Func<int, int> MakeScaler()
    {
        int multiplier = 3;
        return x => x * multiplier;
    }
}
