using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LiveKitMeet.Tray;

public sealed class WebSessionChangedEventArgs : EventArgs
{
    public WebSessionChangedEventArgs(string? refreshToken)
    {
        RefreshToken = refreshToken;
    }

    public string? RefreshToken { get; }
}

public sealed class MeetWindow : Window
{
    private const string RefreshTokenCookieName = "livekit_refresh_token";
    private readonly string _serverUrl;
    private readonly string _cookieUri;
    private readonly WebView2 _webView = new();
    private string _pendingUrl;
    private Task? _initializationTask;
    private bool _allowClose;

    public MeetWindow(string serverUrl)
    {
        _serverUrl = AuthClient.NormalizeServerUrl(serverUrl);
        _cookieUri = new Uri(_serverUrl).GetLeftPart(UriPartial.Authority);
        _pendingUrl = _serverUrl;

        Title = "LiveKit Meet";
        Width = 1200;
        Height = 800;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Content = _webView;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public event EventHandler<WebSessionChangedEventArgs>? WebSessionChanged;

    public void NavigateTo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        _pendingUrl = uri.ToString();
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.Navigate(_pendingUrl);
        }
    }

    public Task ClearSessionAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            return Task.CompletedTask;
        }

        _webView.CoreWebView2.CookieManager.DeleteAllCookies();
        NavigateTo($"{_serverUrl}/login");
        return Task.CompletedTask;
    }

    public void CloseForApplicationExit()
    {
        _allowClose = true;
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _initializationTask ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveKitMeet",
                "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.Navigate(_pendingUrl);
        }
        catch (Exception ex)
        {
            TrayDiagnosticLog.Write($"WebView2 initialization failed error={ex.Message}");
            MessageBox.Show(
                $"The meeting window could not be initialized.\n\n{ex.Message}",
                "LiveKit Meet",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        Title = string.IsNullOrWhiteSpace(_webView.CoreWebView2.DocumentTitle)
            ? "LiveKit Meet"
            : _webView.CoreWebView2.DocumentTitle;

        try
        {
            var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(_cookieUri);
            var refreshToken = cookies
                .FirstOrDefault(cookie => cookie.Name == RefreshTokenCookieName)
                ?.Value;
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                WebSessionChanged?.Invoke(this, new WebSessionChangedEventArgs(refreshToken));
            }
            else if (IsLoginPage(_webView.CoreWebView2.Source))
            {
                WebSessionChanged?.Invoke(this, new WebSessionChangedEventArgs(null));
            }
        }
        catch (Exception ex)
        {
            TrayDiagnosticLog.Write($"WebView2 cookie inspection failed error={ex.Message}");
        }
    }

    private bool IsLoginPage(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Host, new Uri(_serverUrl).Host, StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
