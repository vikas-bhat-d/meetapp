using System.IO;
using System.IO.Pipes;
using System.Text;

namespace LiveKitMeet.Tray;

internal sealed class TrayUrlActivationChannel : IDisposable
{
    private const string PipeName = "WinCall.UrlActivation";
    private readonly Action<string> _urlReceived;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenTask;

    public TrayUrlActivationChannel(Action<string> urlReceived)
    {
        _urlReceived = urlReceived;
    }

    public void Start()
    {
        _listenTask = ListenAsync();
    }

    public static bool TryForward(string url)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                client.Connect(500);

                using var writer = new StreamWriter(
                    client,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    1024,
                    leaveOpen: true)
                {
                    AutoFlush = true
                };
                writer.WriteLine(url);
                return true;
            }
            catch (TimeoutException) when (attempt < 4)
            {
                Task.Delay(100).GetAwaiter().GetResult();
            }
            catch (IOException) when (attempt < 4)
            {
                Task.Delay(100).GetAwaiter().GetResult();
            }
        }

        return false;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_shutdown.Token);

                using var reader = new StreamReader(
                    server,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                var url = await reader.ReadLineAsync(_shutdown.Token);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _urlReceived(url.Trim());
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                TrayDiagnosticLog.Write($"Tray URL activation listener failed error={ex.Message}");
                try
                {
                    await Task.Delay(250, _shutdown.Token);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}