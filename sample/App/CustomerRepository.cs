using System.Threading.Tasks;

namespace SampleApp
{
    public class CustomerRepository
    {
        public Task<Domain.Customer> Load(Billing.Order[] orders) => Task.FromResult<Domain.Customer>(null!);
    }

    namespace Domain
    {
        public class Customer { }
    }

    namespace Billing
    {
        public class Order { }
    }
}
