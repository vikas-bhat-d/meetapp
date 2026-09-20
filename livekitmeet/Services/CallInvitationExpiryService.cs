using Microsoft.Extensions.DependencyInjection;

namespace livekitmeet.Services;

public sealed class CallInvitationExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CallInvitationExpiryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ExpireInvitationsAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ExpireInvitationsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExpireInvitationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var callLogs = scope.ServiceProvider.GetRequiredService<ICallLogService>();
        await callLogs.ExpirePendingInvitationsAsync(cancellationToken);
    }
}