# LiveKit Meet tray app

This Windows tray client connects to the server at `/hubs/call-invitations` and waits for incoming `IncomingCall` messages.

Run it from the solution directory:

```powershell
dotnet run --project .\LiveKitMeet.Tray\LiveKitMeet.Tray.csproj
```

On first launch, double-click the tray icon. The WPF WebView2 sign-in window uses the same web session as LiveKit Meet, then exchanges its protected refresh cookie for a separate tray session. The tray's rotating refresh token is protected with Windows DPAPI under the current Windows user profile.

The Microsoft Edge WebView2 Runtime must be installed. The embedded WebView keeps its own persistent signed-in session; it does not read cookies from an external Chrome or Edge profile.

The tray app must be running and connected for the web Ring action to reach that user. Incoming invitations play the configured ringtone while the WPF Accept/Decline window is open. Accepting an invitation opens the server-provided room URL in the authenticated WebView2 window.

Audio paths are configured in `%APPDATA%\LiveKitMeet\tray-settings.json`. `RingtonePath` defaults to `./ringtone.mp3`, resolved relative to the tray executable directory. `AcceptSoundPath` and `DeclineSoundPath` are empty by default; set either to an absolute path or a path relative to the tray executable directory to enable the corresponding sound. The project copies `ringtone\ringtone.mp3` to the executable directory as `ringtone.mp3`.

Use **Open Meet** from the tray menu, or double-click the icon, to open the meeting site in the WPF WebView2 window. The tray SignalR connection remains active while the window is closed, so incoming invitations continue to ring in the tray.
