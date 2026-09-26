using System;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace meetwrapper
{
    public partial class Form1 : Form
    {
        private const string HomeUrl = "https://192.168.1.6:3000/rooms/temp/vikt";

        public Form1()
        {
            InitializeComponent();
            this.Load += Form1_Load;
        }

        private async void Form1_Load(object sender, EventArgs e)
        {
            // Allow ws:// WebSocket from https:// page (mixed content) + local network access
            var options = new CoreWebView2EnvironmentOptions(
                "--allow-running-insecure-content " +
                "--disable-features=BlockInsecurePrivateNetworkRequests");

            var env = await CoreWebView2Environment.CreateAsync(null,
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MeetWrapper", "WebView2Data"),
                options);

            await webView.EnsureCoreWebView2Async(env);

            // Allow self-signed / untrusted certs (local dev server)
            webView.CoreWebView2.ServerCertificateErrorDetected += (s, args) =>
            {
                args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            };

            // Keep URL bar in sync with actual navigation
            webView.CoreWebView2.NavigationStarting += (s, args) =>
            {
                txtUrl.Text = args.Uri;
                lblStatus.Text = "Loading…";
            };

            webView.CoreWebView2.NavigationCompleted += (s, args) =>
            {
                txtUrl.Text = webView.CoreWebView2.Source;
                lblStatus.Text = args.IsSuccess ? "Done" : "Failed to load page";
                this.Text = $"{webView.CoreWebView2.DocumentTitle} — MeetWrapper";
            };

            webView.CoreWebView2.SourceChanged += (s, args) =>
            {
                txtUrl.Text = webView.CoreWebView2.Source;
            };

            Navigate(HomeUrl);
        }

        private void Navigate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            // Auto-prepend https:// if user forgot the scheme
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            webView.CoreWebView2?.Navigate(url);
        }

        // ── Button handlers ────────────────────────────────────────────────

        private void btnBack_Click(object sender, EventArgs e)
        {
            if (webView.CoreWebView2?.CanGoBack == true)
                webView.CoreWebView2.GoBack();
        }

        private void btnForward_Click(object sender, EventArgs e)
        {
            if (webView.CoreWebView2?.CanGoForward == true)
                webView.CoreWebView2.GoForward();
        }

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            webView.CoreWebView2?.Reload();
        }

        private void btnHome_Click(object sender, EventArgs e)
        {
            Navigate(HomeUrl);
        }

        private void btnGo_Click(object sender, EventArgs e)
        {
            Navigate(txtUrl.Text.Trim());
        }

        // Press Enter in URL bar to navigate
        private void txtUrl_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Return)
            {
                e.Handled = true;
                Navigate(txtUrl.Text.Trim());
            }
        }
    }
}
