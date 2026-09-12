namespace LiveKitMeet.Tray;

public sealed class LoginForm : Form
{
    private readonly AuthClient _authClient;
    private readonly TextBox _serverUrlBox = new();
    private readonly TextBox _usernameBox = new();
    private readonly TextBox _passwordBox = new();
    private readonly Button _loginButton = new();
    private readonly Label _errorLabel = new();

    public LoginForm(AuthClient authClient, string serverUrl)
    {
        _authClient = authClient;
        Text = "LiveKit Meet - Tray sign in";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(430, 270);

        var title = new Label
        {
            Text = "Connect to LiveKit Meet",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(24, 20)
        };
        AddLabel("Server URL", 24, 58);
        AddLabel("Username", 24, 98);
        AddLabel("Password", 24, 138);

        _serverUrlBox.SetBounds(125, 54, 275, 24);
        _serverUrlBox.Text = serverUrl;
        _usernameBox.SetBounds(125, 94, 275, 24);
        _passwordBox.SetBounds(125, 134, 275, 24);
        _passwordBox.UseSystemPasswordChar = true;

        _errorLabel.SetBounds(24, 170, 376, 36);
        _errorLabel.ForeColor = Color.Firebrick;
        _errorLabel.AutoSize = false;

        _loginButton.Text = "Sign in";
        _loginButton.SetBounds(290, 218, 110, 30);
        _loginButton.Click += LoginAsync;
        AcceptButton = _loginButton;

        Controls.AddRange(new Control[]
        {
            title, _serverUrlBox, _usernameBox, _passwordBox, _errorLabel, _loginButton
        });
    }

    public string ServerUrl { get; private set; } = string.Empty;
    public AuthTokenResponse? Tokens { get; private set; }

    private void AddLabel(string text, int x, int y)
    {
        Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y + 4) });
    }

    private async void LoginAsync(object? sender, EventArgs e)
    {
        _loginButton.Enabled = false;
        _errorLabel.Text = string.Empty;
        try
        {
            ServerUrl = AuthClient.NormalizeServerUrl(_serverUrlBox.Text);
            Tokens = await _authClient.LoginAsync(ServerUrl, _usernameBox.Text, _passwordBox.Text);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _errorLabel.Text = ex.Message;
        }
        finally
        {
            _loginButton.Enabled = true;
        }
    }
}
