using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using PathingAPI.Pathing;

using System;
using System.Threading.Tasks;

namespace PathingAPI.RateLimit;

/// <summary>
/// Holds the <see cref="PatherGate"/> for the whole action, or answers 429 when
/// another caller - HTTP, AnTCP or the web UI - has it. The V1 client turns a 429
/// into an empty route, so this is one-at-a-time, not a queue. It used to be a
/// static bool tested and set in two steps, which two requests could pass together.
/// </summary>
public sealed class RateLimitFilter(PatherGate gate) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!gate.TryEnter(out IDisposable lease))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
            return;
        }

        using (lease)
        {
            await next();
        }
    }
}
