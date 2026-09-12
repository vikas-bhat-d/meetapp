# LiveKit Meet tray app

This Windows tray client connects to the server at `/hubs/call-invitations` and waits for incoming `IncomingCall` messages.

Run it from the solution directory:

```powershell
dotnet run --project .\LiveKitMeet.Tray\LiveKitMeet.Tray.csproj
```

On first launch, sign in with a user created by the web administrator. The access token stays in memory and the rotating refresh token is protected with Windows DPAPI under the current Windows user profile.

The tray app must be running and connected for the web Ring action to reach that user. Accepting an invitation opens the server-provided room URL in the default browser. The browser still enforces the normal web login requirement.
