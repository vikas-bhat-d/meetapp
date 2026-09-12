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

## Build an APK

```powershell
npx eas login
npx eas build --platform android --profile preview
```

The `preview` profile produces an installable APK. The app shows FCM notifications, and tapping one opens the invitation URL inside the WebView.
