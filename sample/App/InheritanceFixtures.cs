namespace SampleApp;

public interface IComputable
{
    int Compute(int a, int b);
}

public class ComputableImpl : IComputable
{
    public int Compute(int a, int b) => a + b;
}

public abstract class PricingService
{
    public abstract decimal CalculateTotal(decimal subtotal);
}

public class DiscountPricingService : PricingService
{
    public override decimal CalculateTotal(decimal subtotal) => subtotal * 0.9m;
}

public class SeasonalDiscountPricingService : DiscountPricingService
{
    public override decimal CalculateTotal(decimal subtotal) => base.CalculateTotal(subtotal) * 0.95m;
}
