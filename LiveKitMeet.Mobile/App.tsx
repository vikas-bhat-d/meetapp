import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  PermissionsAndroid,
  Platform,
  SafeAreaView,
  StyleSheet,
  Text,
  View
} from 'react-native';
import * as Device from 'expo-device';
import Constants from 'expo-constants';
import * as Notifications from 'expo-notifications';
import { WebView, WebViewMessageEvent, WebViewNavigation } from 'react-native-webview';

Notifications.setNotificationHandler({
  handleNotification: async () => ({
    shouldShowAlert: true,
    shouldShowBanner: true,
    shouldShowList: true,
    shouldPlaySound: true,
    shouldSetBadge: false
  })
});

type PushData = {
  roomUrl?: string;
  roomName?: string;
  invitationId?: string;
};

const configuredServerUrl =
  process.env.EXPO_PUBLIC_SERVER_URL ??
  (Constants.expoConfig?.extra?.serverUrl as string | undefined) ??
  'http://192.168.1.6:5189';

const SERVER_URL = configuredServerUrl.replace(/\/$/, '');

function isHttpUrl(value: unknown): value is string {
  if (typeof value !== 'string' || value.length > 2048) {
    return false;
  }

  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

function normalizeRoomUrl(value: unknown): string | null {
  if (!isHttpUrl(value)) {
    return null;
  }

  const target = new URL(value);
  const configured = new URL(SERVER_URL);
  if (target.hostname === 'localhost' || target.hostname === '127.0.0.1') {
    target.protocol = configured.protocol;
    target.host = configured.host;
  }

  return target.toString();
}

async function registerForPushNotificationsAsync(): Promise<string | null> {
  if (!Device.isDevice) {
    return null;
  }

  if (Platform.OS === 'android') {
    await Notifications.setNotificationChannelAsync('incoming-calls', {
      name: 'Incoming calls',
      importance: Notifications.AndroidImportance.MAX,
      sound: 'default',
      vibrationPattern: [0, 250, 250, 250],
      lockscreenVisibility: Notifications.AndroidNotificationVisibility.PUBLIC
    });
  }

  const permissions = await Notifications.getPermissionsAsync();
  let finalStatus = permissions.status;
  if (finalStatus !== 'granted') {
    finalStatus = (await Notifications.requestPermissionsAsync()).status;
  }

  if (finalStatus !== 'granted') {
    return null;
  }

  const nativeToken = await Notifications.getDevicePushTokenAsync();
  return typeof nativeToken.data === 'string' ? nativeToken.data : null;
}

async function requestMediaPermissionsAsync(): Promise<void> {
  if (Platform.OS !== 'android' || Platform.Version < 23) {
    return;
  }

  await PermissionsAndroid.requestMultiple([
    PermissionsAndroid.PERMISSIONS.CAMERA,
    PermissionsAndroid.PERMISSIONS.RECORD_AUDIO
  ]);
}

export default function App() {
  const webViewRef = useRef<WebView>(null);
  const [currentUrl, setCurrentUrl] = useState(SERVER_URL);
  const [pushToken, setPushToken] = useState<string | null>(null);
  const [fcmStatus, setFcmStatus] = useState('Requesting FCM token...');
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const pushRegistrationScript = useMemo(() => {
    if (!pushToken) {
      return undefined;
    }

    const payload = JSON.stringify({ token: pushToken, platform: 'android' });
    return `
      (async function () {
        try {
          const response = await fetch('/api/devices/fcm', {
            method: 'POST',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: ${JSON.stringify(payload)}
          });
          window.ReactNativeWebView?.postMessage(JSON.stringify({
            type: 'fcm-registration',
            status: response.status
          }));
        } catch (_) {
          window.ReactNativeWebView?.postMessage(JSON.stringify({
            type: 'fcm-registration',
            status: 0
          }));
        }
      })();
      true;
    `;
  }, [pushToken]);

  const openNotificationRoom = useCallback((data: PushData | undefined) => {
    const roomUrl = normalizeRoomUrl(data?.roomUrl);
    if (roomUrl) {
      setCurrentUrl(roomUrl);
    }
  }, []);

  useEffect(() => {
    requestMediaPermissionsAsync().catch(() => undefined);
    registerForPushNotificationsAsync()
      .then(token => {
        setPushToken(token);
        setFcmStatus(token ? 'FCM token ready; sign in to register' : 'FCM token unavailable');
      })
      .catch(() => {
        setPushToken(null);
        setFcmStatus('FCM token unavailable');
      });

    const receivedSubscription = Notifications.addNotificationReceivedListener(() => {
      // The notification handler above displays the foreground notification.
    });
    const responseSubscription = Notifications.addNotificationResponseReceivedListener(response => {
      openNotificationRoom(response.notification.request.content.data as PushData);
    });

    Notifications.getLastNotificationResponseAsync().then(response => {
      if (response) {
        openNotificationRoom(response.notification.request.content.data as PushData);
      }
    });

    return () => {
      receivedSubscription.remove();
      responseSubscription.remove();
    };
  }, [openNotificationRoom]);

  useEffect(() => {
    if (!pushRegistrationScript) {
      return;
    }

    // The FCM token can arrive after the WebView's first page load. Retry
    // registration whenever the token becomes available.
    const timer = setTimeout(() => {
      webViewRef.current?.injectJavaScript(pushRegistrationScript);
    }, 250);
    return () => clearTimeout(timer);
  }, [pushRegistrationScript]);

  const handleNavigation = (_navigation: WebViewNavigation) => {
    setError(null);
    if (pushRegistrationScript) {
      webViewRef.current?.injectJavaScript(pushRegistrationScript);
    }
  };

  const handleWebViewMessage = (event: WebViewMessageEvent) => {
    try {
      const message = JSON.parse(event.nativeEvent.data) as {
        type?: string;
        status?: number;
      };
      if (message.type === 'fcm-registration') {
        setFcmStatus(
          message.status === 204
            ? 'FCM registered'
            : `FCM registration failed (${message.status ?? 0}); sign in inside the APK`
        );
      }
    } catch {
      // Ignore messages that are not diagnostics from our registration script.
    }
  };

  if (error) {
    return (
      <SafeAreaView style={styles.centered}>
        <Text style={styles.errorTitle}>Unable to open LiveKit Meet</Text>
        <Text style={styles.errorText}>{error}</Text>
        <Text style={styles.errorText}>Server: {SERVER_URL}</Text>
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.container}>
      {isLoading && (
        <View style={styles.loadingOverlay}>
          <ActivityIndicator size="large" color="#6f8cff" />
        </View>
      )}
      <View style={styles.pushStatus}>
        <Text style={styles.pushStatusText}>{fcmStatus}</Text>
      </View>
      <WebView
        ref={webViewRef}
        source={{ uri: currentUrl }}
        javaScriptEnabled
        domStorageEnabled
        sharedCookiesEnabled
        thirdPartyCookiesEnabled
        mediaPlaybackRequiresUserAction={false}
        allowsInlineMediaPlayback
        mediaCapturePermissionGrantType="grantIfSameHostElsePrompt"
        originWhitelist={['http://*', 'https://*']}
        onMessage={handleWebViewMessage}
        onLoadStart={() => setIsLoading(true)}
        onLoadEnd={() => {
          setIsLoading(false);
          if (pushRegistrationScript) {
            webViewRef.current?.injectJavaScript(pushRegistrationScript);
          }
        }}
        onNavigationStateChange={handleNavigation}
        onError={event => {
          setIsLoading(false);
          setError(event.nativeEvent.description || 'The server could not be reached.');
        }}
      />
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#10182a'
  },
  loadingOverlay: {
    position: 'absolute',
    zIndex: 1,
    top: 0,
    right: 0,
    bottom: 0,
    left: 0,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#10182a'
  },
  pushStatus: {
    position: 'absolute',
    zIndex: 2,
    right: 8,
    bottom: 8,
    left: 8,
    paddingVertical: 4,
    paddingHorizontal: 8,
    borderRadius: 6,
    backgroundColor: 'rgba(16, 24, 42, 0.82)'
  },
  pushStatusText: {
    color: '#b7c0d8',
    fontSize: 11,
    textAlign: 'center'
  },
  centered: {
    flex: 1,
    padding: 24,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#10182a'
  },
  errorTitle: {
    marginBottom: 12,
    color: '#ffffff',
    fontSize: 20,
    fontWeight: '700',
    textAlign: 'center'
  },
  errorText: {
    marginTop: 6,
    color: '#b7c0d8',
    textAlign: 'center'
  }
});
