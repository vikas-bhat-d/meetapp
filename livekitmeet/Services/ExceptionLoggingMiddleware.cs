namespace livekitmeet.Services;

public sealed class ExceptionLoggingMiddleware
{
    private readonly RequestDelegate next;
    private readonly ILogger<ExceptionLoggingMiddleware> logger;

    public ExceptionLoggingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionLoggingMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unhandled HTTP exception. Method={Method} Path={Path} TraceId={TraceId} User={User}",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier,
                context.User.Identity?.Name ?? "anonymous");
            throw;
        }
    }
}