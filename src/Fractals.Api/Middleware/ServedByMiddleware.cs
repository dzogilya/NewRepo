namespace Fractals.Api.Middleware;

public class ServedByMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _instanceId;

    public ServedByMiddleware(RequestDelegate _next, IConfiguration configuration)
    {
        this._next = _next;
        _instanceId = configuration["INSTANCE_ID"] ?? Environment.MachineName;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Append("X-Served-By", _instanceId);
            return Task.CompletedTask;
        });

        await _next(context);
    }
}
