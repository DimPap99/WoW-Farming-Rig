using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceHosting;

/// <summary>How a service answers what went wrong: problem details, and 400 for a request it cannot read.</summary>
public static class ServiceProblems
{
    /// <summary>
    /// Problem details, and 400 with why for a request the service cannot read (a body that is not the
    /// contract's JSON, a query that is not a number): the caller's mistake. Minimal APIs answer so
    /// themselves in Production, but in Development, where every <c>run.sh</c> runs a service, they throw,
    /// and the exception handler answered 500, which a client takes for the service's own failure.
    /// </summary>
    public static IServiceCollection AddServiceProblems(this IServiceCollection services)
    {
        services.AddExceptionHandler(options => options.StatusCodeSelector = ex =>
            ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError);
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            if (context.Exception is BadHttpRequestException bad)
            {
                context.ProblemDetails.Detail = bad.Message;
            }
        });
        return services;
    }
}
