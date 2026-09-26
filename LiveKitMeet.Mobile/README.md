# LiveKit Meet Android app

This is a lightweight Expo React Native wrapper around the existing LiveKit Meet website. It keeps the website login session inside WebView and registers the Android native FCM token with the server after the user signs in.

## Firebase setup

1. Open the [Firebase Console](https://console.firebase.google.com/) and create or select a project.
2. Add an Android app with package name `com.livekitmeet.mobile`.
3. Download `google-services.json` and place it in this directory. Do not commit it; it is ignored by `.gitignore`.
4. In Firebase project settings, open **Service accounts**, create a private key, and keep the downloaded JSON file outside the repository.
5. Configure the server with the service-account values. For Visual Studio F5, use .NET User Secrets instead of environment variables:

```powershell
$firebaseAccount = Get-Content .\firebase-service-account.json -Raw | ConvertFrom-Json

dotnet user-secrets --project ..\livekitmeet\livekitmeet.csproj set "Firebase:ProjectId" $firebaseAccount.project_id
dotnet user-secrets --project ..\livekitmeet\livekitmeet.csproj set "Firebase:ClientEmail" $firebaseAccount.client_email
dotnet user-secrets --project ..\livekitmeet\livekitmeet.csproj set "Firebase:PrivateKey" $firebaseAccount.private_key
```

Alternatively, store only the path to the service-account file. This is recommended for local Visual Studio development:

```powershell
dotnet user-secrets --project ..\livekitmeet\livekitmeet.csproj set "Firebase:ServiceAccountPath" "C:\secure\firebase-service-account.json"
```

The server also accepts a file path accidentally placed in `Firebase:PrivateKey` and will read the JSON file automatically.

The web project is already configured with a `UserSecretsId`, and the Visual Studio Development profiles already set `ASPNETCORE_ENVIRONMENT=Development`, so F5 loads these values automatically. The secret values are stored outside the repository. An alternative local-only file is provided as `..\livekitmeet\appsettings.Development.json.example`; rename it to `appsettings.Development.json` and fill in the values. That file is ignored by Git.

If you prefer environment variables when running from a terminal, the equivalent is:

```powershell
$env:Firebase__ProjectId = $firebaseAccount.project_id
$env:Firebase__ClientEmail = $firebaseAccount.client_email
$env:Firebase__PrivateKey = $firebaseAccount.private_key
```

The ASP.NET Core server uses these values to call Firebase Cloud Messaging. Never commit the service-account JSON or private key.

## Run locally

Install dependencies:

```powershell
cd .\LiveKitMeet.Mobile
npm install
```

For the Android emulator, the default server URL is `http://10.0.2.2:5189`. Start the web server first:

```powershell
dotnet run --project ..\livekitmeet\livekitmeet.csproj --urls http://0.0.0.0:5189
```

Then build and install the debug APK on an emulator or USB-connected device:

```powershell
npx expo run:android
```

For a physical device, change `expo.extra.serverUrl` in `app.json` to the computer's LAN IP, for example `http://192.168.1.10:5189`. Production should use HTTPS.

## File configuration and logs

On the first Android launch, the native wrapper opens Android's system folder picker. Android may not allow the `Internal storage` root to be selected, so select any folder inside it and grant access. The app creates a visible `wincalldata` directory inside the selected folder and stores its files there. For example, if you select `Download`:

```text
/storage/emulated/0/Download/wincalldata/config.json
```

If you select a different folder, look for `wincalldata` inside that exact folder. The selected directory URI is kept in app-private storage so access can be restored on later launches. Android's modern scoped-storage rules do not allow the app to request unrestricted access to all shared storage; the system folder picker grants access only to the selected directory. Existing `.wincalldata` directories from an older build are migrated to visible `wincalldata` automatically.

The repository includes `config.json.example` with the supported shape:

```json
{
	"serverUrl": "https://192.168.1.6:8443",
	"retainLog": 3
}
```

`serverUrl` is read when the app starts. `retainLog` accepts `0` through `3`: `0` disables file logging and removes stored log files, while `1`, `2`, and `3` retain that many calendar days including today. Logs are written beside the config file as `logs/YYYY-MM-DD.log`.

If the picker is cancelled, the app falls back to its private app directory for that run. The shared folder is requested again on a later fresh launch. For a debug build, the shared files can be inspected with:

```powershell
adb shell ls -la /sdcard/Download/wincalldata
adb shell cat /sdcard/Download/wincalldata/config.json
adb shell ls -la /sdcard/Download/wincalldata/logs
```

Replace the shared `config.json` with the example or another valid JSON file, then fully close and reopen the app. If the file is missing or invalid, the app recreates it from `EXPO_PUBLIC_SERVER_URL` or the `expo.extra.serverUrl` fallback. Camera and microphone prompts are shown one at a time before notification permission, and the WebView is recreated after returning from the background so a stale Blazor connection does not remain on screen.

## Build an APK

```powershell
npx eas login
npx eas build --platform android --profile preview
```

The `preview` profile produces an installable APK. The app shows FCM notifications, and tapping one opens the invitation URL inside the WebView.
