using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public static class CommerceInfrastructureRegistration
{
    public static IServiceCollection AddCommerceInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<CommerceDb>(options => options.UseSqlServer(connectionString));
        services.AddScoped<ICommerceStore>(provider => provider.GetRequiredService<CommerceDb>());
        return services;
    }
}
