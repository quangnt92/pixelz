using Pixelz.Domain.Common;

namespace Pixelz.Domain.Customers;

public class Customer : AggregateRoot<Guid>
{
    public string Email { get; private set; } = null!;
    public string FullName { get; private set; } = null!;
    public string? Company { get; private set; }
    public string? Phone { get; private set; }
    public CustomerStatus Status { get; private set; }

    // EF Core
    private Customer() { }

    public static Customer Create(string email, string fullName, string? company = null, string? phone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);

        var customer = new Customer
        {
            Id       = Guid.NewGuid(),
            Email    = email.Trim().ToLowerInvariant(),
            FullName = fullName.Trim(),
            Company  = company?.Trim(),
            Phone    = phone?.Trim(),
            Status   = CustomerStatus.Active
        };
        customer.SetTimestamps();
        return customer;
    }

    public void Deactivate()
    {
        Status = CustomerStatus.Inactive;
        Touch();
    }

    public void Update(string fullName, string? company, string? phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        FullName = fullName.Trim();
        Company  = company?.Trim();
        Phone    = phone?.Trim();
        Touch();
    }
}

public enum CustomerStatus : byte
{
    Active   = 1,
    Inactive = 0
}