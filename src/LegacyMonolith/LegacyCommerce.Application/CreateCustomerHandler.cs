using System.ComponentModel.DataAnnotations;

public sealed class CreateCustomerHandler(ICommerceStore store)
{
    public async Task<UseCaseResult<Customer>> CreateCustomerAsync(string name, string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
            return UseCaseResult<Customer>.Fail(UseCaseFailure.Invalid, "Valid name and email are required.");
        var normalized = email.Trim().ToLowerInvariant();
        if (await store.CustomerEmailExistsAsync(normalized, ct))
            return UseCaseResult<Customer>.Fail(UseCaseFailure.Conflict, "Email already exists.");
        var customer = Customer.Create(name, normalized);
        store.AddCustomer(customer);
        await store.SaveChangesAsync(ct);
        return UseCaseResult<Customer>.Success(customer);
    }
}
