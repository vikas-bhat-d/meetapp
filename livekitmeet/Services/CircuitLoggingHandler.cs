using Microsoft.AspNetCore.Components.Server.Circuits;

namespace livekitmeet.Services;

public sealed class CircuitLoggingHandler : CircuitHandler
{
    private readonly ILogger<CircuitLoggingHandler> logger;

    public CircuitLoggingHandler(ILogger<CircuitLoggingHandler> logger)
    {
        this.logger = logger;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogInformation("Blazor circuit opened. CircuitId={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogWarning("Blazor circuit closed. CircuitId={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogInformation("Blazor circuit connection restored. CircuitId={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogWarning("Blazor circuit connection lost. CircuitId={CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }
}