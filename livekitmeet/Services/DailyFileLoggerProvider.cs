using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace livekitmeet.Services;

public sealed class DailyFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly string logDirectory;
    private readonly string fileName;
    private readonly int retainedDays;
    private readonly bool enableNormalLogs;
    private readonly object writeLock = new();
    private readonly ConcurrentDictionary<string, DailyFileLogger> loggers = new(StringComparer.Ordinal);
    private IExternalScopeProvider scopeProvider = new LoggerExternalScopeProvider();
    private DateTime lastCleanupDate = DateTime.MinValue;

    public DailyFileLoggerProvider(
        string contentRootPath,
        string? configuredDirectory,
        string? configuredFileName,
        int configuredRetainedDays,
        bool configuredEnableNormalLogs)
    {
        var directory = string.IsNullOrWhiteSpace(configuredDirectory) ? "logs" : configuredDirectory.Trim();
        logDirectory = Path.GetFullPath(
            Path.IsPathRooted(directory) ? directory : Path.Combine(contentRootPath, directory));
        fileName = SanitizeFileName(configuredFileName);
        retainedDays = configuredRetainedDays > 0 ? configuredRetainedDays : 14;
        enableNormalLogs = configuredEnableNormalLogs;
    }

    public ILogger CreateLogger(string categoryName) =>
        loggers.GetOrAdd(categoryName, category => new DailyFileLogger(category, this));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        this.scopeProvider = scopeProvider ?? new LoggerExternalScopeProvider();
    }

    public void Dispose()
    {
        loggers.Clear();
    }

    internal IDisposable BeginScope(object state) => scopeProvider.Push(state);

    internal void Write(
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        string message,
        Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("O", CultureInfo.InvariantCulture))
            .Append(" [")
            .Append(logLevel.ToString())
            .Append("] ")
            .Append(categoryName);

        if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
        {
            line.Append(" EventId=").Append(eventId.Id);
            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                line.Append(" (").Append(eventId.Name).Append(')');
            }
        }

        line.Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        line.AppendLine();

        lock (writeLock)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                var logDate = now.Date;
                var path = Path.Combine(logDirectory, $"{fileName}-{logDate:yyyy-MM-dd}.log");
                File.AppendAllText(path, line.ToString(), Encoding.UTF8);

                if (lastCleanupDate != logDate)
                {
                    RemoveExpiredLogs(logDate);
                    lastCleanupDate = logDate;
                }
            }
            catch (Exception loggingException)
            {
                Console.Error.WriteLine(
                    $"Daily file logging failed for '{categoryName}': {loggingException}");
            }
        }
    }

    private void RemoveExpiredLogs(DateTime currentDate)
    {
        var oldestRetainedDate = currentDate.AddDays(-(retainedDays - 1));
        foreach (var path in Directory.EnumerateFiles(logDirectory, $"{fileName}-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var dateText = name[$"{fileName}-".Length..];
            if (DateTime.TryParseExact(
                    dateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var fileDate) &&
                fileDate.Date < oldestRetainedDate)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A locked old file can be retried during the next cleanup.
                }
                catch (UnauthorizedAccessException)
                {
                    // A permissions problem must not stop application logging.
                }
            }
        }
    }

    private static string SanitizeFileName(string? configuredFileName)
    {
        var value = string.IsNullOrWhiteSpace(configuredFileName) ? "livekitmeet" : configuredFileName.Trim();
        value = Path.GetFileNameWithoutExtension(value);
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "livekitmeet" : value;
    }

    private sealed class DailyFileLogger : ILogger
    {
        private readonly string categoryName;
        private readonly DailyFileLoggerProvider provider;

        public DailyFileLogger(string categoryName, DailyFileLoggerProvider provider)
        {
            this.categoryName = categoryName;
            this.provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None &&
            (logLevel >= LogLevel.Warning || provider.enableNormalLogs);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message;
            try
            {
                message = formatter(state, exception);
            }
            catch (Exception formatterException)
            {
                message = state?.ToString() ?? string.Empty;
                exception ??= formatterException;
            }

            provider.Write(categoryName, logLevel, eventId, message, exception);
        }
    }
}