using Microsoft.Extensions.DependencyInjection;
using Pixelz.Application.Common;

namespace Pixelz.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            cfg.AddOpenBehavior(typeof(LoggingPipelineBehavior<,>));
        });

        return services;
    }
}
