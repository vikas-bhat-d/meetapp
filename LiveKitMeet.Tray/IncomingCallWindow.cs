using System.Windows;
using System.Windows.Controls;

namespace LiveKitMeet.Tray;

public sealed class IncomingCallWindow : Window
{
    public IncomingCallWindow(CallInvitationMessage invitation)
    {
        Title = "Incoming LiveKit call";
        Width = 460;
        Height = 280;
        MinWidth = 460;
        MinHeight = 280;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;

        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock
        {
            Text = "Incoming call",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{invitation.FromDisplayName} ({invitation.FromUserName}) is inviting you to:\n{invitation.RoomName}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 22, 0, 0),
            FontSize = 15
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Microphone: {(invitation.AudioEnabled ? "on" : "off")}    Camera: {(invitation.VideoEnabled ? "on" : "off")}",
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 16, 0, 0)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0)
        };
        var accept = new Button
        {
            Content = "Accept",
            Width = 92,
            Height = 32,
            IsDefault = true,
            Margin = new Thickness(0, 0, 10, 0)
        };
        accept.Click += (_, _) =>
        {
            DialogResult = true;
        };
        var decline = new Button
        {
            Content = "Decline",
            Width = 92,
            Height = 32,
            IsCancel = true
        };
        decline.Click += (_, _) =>
        {
            DialogResult = false;
        };
        buttons.Children.Add(accept);
        buttons.Children.Add(decline);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public bool ClosedByRemoteStatus { get; private set; }

    public void CloseByRemoteStatus()
    {
        ClosedByRemoteStatus = true;
        Close();
    }

    public void CloseForApplicationExit()
    {
        Close();
    }

}
