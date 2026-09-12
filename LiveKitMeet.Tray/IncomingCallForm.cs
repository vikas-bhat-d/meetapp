namespace LiveKitMeet.Tray;

public sealed class IncomingCallForm : Form
{
    public IncomingCallForm(CallInvitationMessage invitation)
    {
        Text = "Incoming LiveKit call";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(430, 220);

        var title = new Label
        {
            Text = "Incoming call",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(24, 20)
        };
        var caller = new Label
        {
            Text = $"{invitation.FromDisplayName} ({invitation.FromUserName}) is inviting you to:\r\n{invitation.RoomName}",
            AutoSize = false,
            Size = new Size(380, 58),
            Location = new Point(24, 58)
        };
        var media = new Label
        {
            Text = $"Microphone: {(invitation.AudioEnabled ? "on" : "off")}    Camera: {(invitation.VideoEnabled ? "on" : "off")}",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Location = new Point(24, 122)
        };
        var accept = new Button { Text = "Accept", DialogResult = DialogResult.OK };
        accept.SetBounds(215, 168, 90, 30);
        var decline = new Button { Text = "Decline", DialogResult = DialogResult.Cancel };
        decline.SetBounds(315, 168, 90, 30);

        Controls.AddRange(new Control[] { title, caller, media, accept, decline });
        AcceptButton = accept;
        CancelButton = decline;
    }
}
