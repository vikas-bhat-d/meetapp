import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  PermissionsAndroid,
  Platform,
  Pressable,
  SafeAreaView,
  StyleSheet,
  Text,
  TextInput,
  View
} from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
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

const defaultServerUrl =
  process.env.EXPO_PUBLIC_SERVER_URL ??
  (Constants.expoConfig?.extra?.serverUrl as string | undefined) ??
    'https://192.168.1.6:8443';

const SERVER_URL_STORAGE_KEY = 'livekitmeet.serverUrl';

function normalizeServerUrl(value: unknown): string | null {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return null;
  }

  try {
    const url = new URL(value.trim());
    if (url.protocol !== 'http:' && url.protocol !== 'https:') {
      return null;
    }

    url.search = '';
    url.hash = '';
    return url.toString().replace(/\/$/, '');
  } catch {
    return null;
  }
}

const DEFAULT_SERVER_URL = normalizeServerUrl(defaultServerUrl) ?? 'https://192.168.1.6:8443';

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

function normalizeRoomUrl(value: unknown, serverUrl: string): string | null {
  if (!isHttpUrl(value)) {
    return null;
  }

  const target = new URL(value);
  const configured = new URL(serverUrl);
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
  const hasLoadedDocumentRef = useRef(false);
  const loadFallbackTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingNotificationUrlRef = useRef<string | null>(null);
  const [serverUrl, setServerUrl] = useState(DEFAULT_SERVER_URL);
  const [serverUrlDraft, setServerUrlDraft] = useState(DEFAULT_SERVER_URL);
  const [configurationReady, setConfigurationReady] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [settingsMessage, setSettingsMessage] = useState<string | null>(null);
  const [currentUrl, setCurrentUrl] = useState(DEFAULT_SERVER_URL);
  const [pushToken, setPushToken] = useState<string | null>(null);
  const [fcmStatus, setFcmStatus] = useState('Requesting FCM token...');
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;

    AsyncStorage.getItem(SERVER_URL_STORAGE_KEY)
      .then(savedUrl => {
        if (!active) {
          return;
        }

        const configuredUrl = normalizeServerUrl(savedUrl) ?? DEFAULT_SERVER_URL;
        setServerUrl(configuredUrl);
        setServerUrlDraft(configuredUrl);
        setCurrentUrl(pendingNotificationUrlRef.current ?? configuredUrl);
        setConfigurationReady(true);
        setIsLoading(true);
      })
      .catch(() => {
        if (!active) {
          return;
        }

        setConfigurationReady(true);
        setIsLoading(true);
      });

    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    if (!configurationReady) {
      return;
    }

    hasLoadedDocumentRef.current = false;
    setIsLoading(true);

    loadFallbackTimerRef.current = setTimeout(() => {
      setIsLoading(false);
    }, 10000);

    return () => {
      if (loadFallbackTimerRef.current) {
        clearTimeout(loadFallbackTimerRef.current);
        loadFallbackTimerRef.current = null;
      }
    };
  }, [configurationReady, currentUrl]);

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
    const roomUrl = normalizeRoomUrl(data?.roomUrl, serverUrl);
    if (roomUrl) {
      pendingNotificationUrlRef.current = roomUrl;
      setCurrentUrl(roomUrl);
      setIsLoading(true);
    }
  }, [serverUrl]);

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
    setIsLoading(false);
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

  const saveServerUrl = async () => {
    const nextServerUrl = normalizeServerUrl(serverUrlDraft);
    if (!nextServerUrl) {
      setSettingsMessage('Enter a valid http:// or https:// server URL.');
      return;
    }

    try {
      await AsyncStorage.setItem(SERVER_URL_STORAGE_KEY, nextServerUrl);
    } catch {
      setSettingsMessage('The server URL could not be saved on this device.');
      return;
    }
    setServerUrl(nextServerUrl);
    setServerUrlDraft(nextServerUrl);
    setCurrentUrl(nextServerUrl);
    pendingNotificationUrlRef.current = null;
    setError(null);
    setSettingsMessage(null);
    setShowSettings(false);
  };

  if (!configurationReady) {
    return (
      <SafeAreaView style={styles.centered}>
        <ActivityIndicator size="large" color="#6f8cff" />
        <Text style={styles.errorText}>Loading app settings...</Text>
      </SafeAreaView>
    );
  }

  if (error) {
    return (
      <SafeAreaView style={styles.centered}>
        <Text style={styles.errorTitle}>Unable to open LiveKit Meet</Text>
        <Text style={styles.errorText}>{error}</Text>
        <Text style={styles.errorText}>Server: {serverUrl}</Text>
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
      <Pressable style={styles.settingsButton} onPress={() => {
        setServerUrlDraft(serverUrl);
        setSettingsMessage(null);
        setShowSettings(true);
      }}>
        <Text style={styles.settingsButtonText}>Settings</Text>
      </Pressable>
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
        onLoadStart={() => {
          setIsLoading(true);
          if (loadFallbackTimerRef.current) {
            clearTimeout(loadFallbackTimerRef.current);
          }
          loadFallbackTimerRef.current = setTimeout(() => {
            setIsLoading(false);
          }, 10000);
        }}
        onLoadProgress={event => {
          if (event.nativeEvent.progress >= 0.5) {
            hasLoadedDocumentRef.current = true;
            setIsLoading(false);
          }
        }}
        onLoadEnd={() => {
          hasLoadedDocumentRef.current = true;
          setIsLoading(false);
          if (loadFallbackTimerRef.current) {
            clearTimeout(loadFallbackTimerRef.current);
            loadFallbackTimerRef.current = null;
          }
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
      {showSettings && (
        <View style={styles.settingsOverlay}>
          <View style={styles.settingsCard}>
            <Text style={styles.settingsTitle}>App settings</Text>
            <Text style={styles.settingsLabel}>Server base URL</Text>
            <TextInput
              value={serverUrlDraft}
              onChangeText={setServerUrlDraft}
              autoCapitalize="none"
              autoCorrect={false}
              keyboardType="url"
              placeholder="https://192.168.1.6:8443"
              placeholderTextColor="#7d8aa8"
              style={styles.settingsInput}
            />
            <Text style={styles.settingsHint}>
              Use the same HTTPS address that is reachable from this phone.
            </Text>
            {settingsMessage && <Text style={styles.settingsMessage}>{settingsMessage}</Text>}
            <View style={styles.settingsActions}>
              <Pressable
                style={[styles.actionButton, styles.cancelButton]}
                onPress={() => {
                  setServerUrlDraft(serverUrl);
                  setSettingsMessage(null);
                  setShowSettings(false);
                }}
              >
                <Text style={styles.actionButtonText}>Cancel</Text>
              </Pressable>
              <Pressable style={[styles.actionButton, styles.saveButton]} onPress={saveServerUrl}>
                <Text style={styles.actionButtonText}>Save & reload</Text>
              </Pressable>
            </View>
          </View>
        </View>
      )}
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
  settingsButton: {
    position: 'absolute',
    zIndex: 3,
    top: 10,
    right: 10,
    paddingVertical: 7,
    paddingHorizontal: 10,
    borderRadius: 6,
    backgroundColor: 'rgba(16, 24, 42, 0.88)'
  },
  settingsButtonText: {
    color: '#ffffff',
    fontSize: 12,
    fontWeight: '600'
  },
  settingsOverlay: {
    position: 'absolute',
    zIndex: 4,
    top: 0,
    right: 0,
    bottom: 0,
    left: 0,
    alignItems: 'center',
    justifyContent: 'center',
    padding: 24,
    backgroundColor: 'rgba(5, 10, 22, 0.78)'
  },
  settingsCard: {
    width: '100%',
    maxWidth: 420,
    padding: 22,
    borderRadius: 14,
    backgroundColor: '#1c2940'
  },
  settingsTitle: {
    marginBottom: 20,
    color: '#ffffff',
    fontSize: 22,
    fontWeight: '700'
  },
  settingsLabel: {
    marginBottom: 8,
    color: '#e8edf8',
    fontSize: 14,
    fontWeight: '600'
  },
  settingsInput: {
    paddingVertical: 12,
    paddingHorizontal: 12,
    borderWidth: 1,
    borderColor: '#53627f',
    borderRadius: 8,
    color: '#ffffff',
    backgroundColor: '#111b2e',
    fontSize: 15
  },
  settingsHint: {
    marginTop: 8,
    color: '#aab6cf',
    fontSize: 12,
    lineHeight: 17
  },
  settingsMessage: {
    marginTop: 10,
    color: '#ffb4b4',
    fontSize: 12
  },
  settingsActions: {
    flexDirection: 'row',
    justifyContent: 'flex-end',
    marginTop: 20,
    gap: 10
  },
  actionButton: {
    paddingVertical: 10,
    paddingHorizontal: 14,
    borderRadius: 7
  },
  cancelButton: {
    backgroundColor: '#3a455c'
  },
  saveButton: {
    backgroundColor: '#2e6bea'
  },
  actionButtonText: {
    color: '#ffffff',
    fontSize: 13,
    fontWeight: '600'
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
