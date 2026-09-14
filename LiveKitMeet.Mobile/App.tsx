import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  AppState,
  BackHandler,
  Linking,
  PermissionsAndroid,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
  Vibration,
  NativeModules
} from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as Device from 'expo-device';
import Constants from 'expo-constants';
import * as Notifications from 'expo-notifications';
import * as TaskManager from 'expo-task-manager';
import { WebView, WebViewMessageEvent, WebViewNavigation } from 'react-native-webview';
import { SafeAreaView } from 'react-native-safe-area-context';

// ─── Background notification task ────────────────────────────────────────────
// This task name MUST match what is passed to Notifications.registerTaskAsync.
const BACKGROUND_NOTIFICATION_TASK = 'BACKGROUND_NOTIFICATION_TASK';
const INCOMING_CALL_CATEGORY = 'INCOMING_CALL';
const INCOMING_CALL_CHANNEL = 'incoming-calls-v2';

type IncomingCallNativeModule = {
  showIncomingCall: (callerName: string, roomName: string, roomUrl: string, invitationId: string) => void;
  dismissIncomingCall: (invitationId: string) => void;
};

const incomingCallNativeModule = NativeModules.IncomingCall as IncomingCallNativeModule | undefined;

type PushData = {
  roomUrl?: string;
  roomName?: string;
  invitationId?: string;
  fromDisplayName?: string;
  fromUserName?: string;
  callerDisplayName?: string;
  [key: string]: unknown;
};

function parseBackgroundPushData(
  taskData: Notifications.NotificationTaskPayload
): Record<string, unknown> | null {
  if ('actionIdentifier' in taskData) {
    return null;
  }

  const dataString = taskData.data?.dataString;
  if (typeof dataString === 'string') {
    try {
      const parsed = JSON.parse(dataString) as unknown;
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
        ? parsed as Record<string, unknown>
        : null;
    } catch {
      return null;
    }
  }

  return null;
}

// Register the task handler at module level (runs even when the app is killed).
// When Android receives a data-only high-priority FCM message while the app is
// in the background or killed, expo-notifications wakes the JS engine and
// invokes this task.  We post a rich local notification with Accept / Decline
// action buttons so the user can respond from the lock screen.
TaskManager.defineTask<Notifications.NotificationTaskPayload>(BACKGROUND_NOTIFICATION_TASK, async ({ data, error }) => {
  if (error) {
    console.warn('[BGTask] error', error);
    return;
  }

  const payload = parseBackgroundPushData(data);
  if (!payload) return;

  if (payload.type === 'CANCEL_CALL') {
    const notificationId = payload.invitationId ?? payload.callUUID;
    if (typeof notificationId === 'string') {
      try {
        await incomingCallNativeModule?.dismissIncomingCall(notificationId);
      } catch {
        // The native full-screen module is unavailable in Expo Go.
      }
      await Notifications.dismissNotificationAsync(notificationId).catch(() => undefined);
    }
    return;
  }

  if (payload.type !== 'INCOMING_CALL') return;
  if (AppState.currentState === 'active') return;

  // The EAS Android build receives data-only FCM messages in the native
  // FirebaseMessagingService, which launches the full-screen Activity. Do not
  // also create an Expo notification for the same message.
  if (Platform.OS === 'android') return;

  const roomUrl = payload.roomUrl as string | undefined;
  if (!roomUrl) return;

  const callerName =
    (payload.callerName as string | undefined) ??
    (payload.fromDisplayName as string | undefined) ??
    'Someone';
  const roomName = (payload.roomName as string | undefined) ?? 'LiveKit meeting';
  const notificationId =
    (payload.invitationId as string | undefined) ??
    (payload.callUUID as string | undefined) ??
    `incoming-${Date.now()}`;

  if (incomingCallNativeModule?.showIncomingCall) {
    try {
      await incomingCallNativeModule.showIncomingCall(callerName, roomName, roomUrl, notificationId);
      return;
    } catch {
      // Fall back to an Expo notification when running without the native module.
    }
  }

  // Post a local heads-up notification that shows Accept / Decline buttons.
  await Notifications.scheduleNotificationAsync({
    identifier: notificationId,
    content: {
      title: `Incoming call from ${callerName}`,
      body: roomName,
      data: payload,
      categoryIdentifier: INCOMING_CALL_CATEGORY,
      sound: 'default',
    },
    trigger: null, // fire immediately
  });
});

Notifications.setNotificationHandler({
  handleNotification: async () => ({
    shouldShowAlert: true,
    shouldShowBanner: true,
    shouldShowList: true,
    shouldPlaySound: true,
    shouldSetBadge: false
  })
});

type IncomingCall = PushData & {
  title?: string;
  body?: string;
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
    // Use a new channel id so devices that previously created a silent
    // incoming-calls channel receive the updated ringing settings.
    await Notifications.setNotificationChannelAsync(INCOMING_CALL_CHANNEL, {
      name: 'Incoming calls',
      importance: Notifications.AndroidImportance.MAX,
      sound: 'default',
      vibrationPattern: [0, 250, 250, 250],
      lockscreenVisibility: Notifications.AndroidNotificationVisibility.PUBLIC
    });
  }

  // Set up the notification category with Accept / Decline action buttons.
  // These appear as tappable buttons on the incoming call notification when
  // the app is in the background or killed.
  await Notifications.setNotificationCategoryAsync(INCOMING_CALL_CATEGORY, [
    {
      identifier: 'ACCEPT_CALL',
      buttonTitle: '✅ Accept',
      options: {
        opensAppToForeground: true,
        isDestructive: false,
        isAuthenticationRequired: false,
      },
    },
    {
      identifier: 'DECLINE_CALL',
      buttonTitle: '❌ Decline',
      options: {
        opensAppToForeground: false,
        isDestructive: true,
        isAuthenticationRequired: false,
      },
    },
  ]);

  const permissions = await Notifications.getPermissionsAsync();
  let finalStatus = permissions.status;
  if (finalStatus !== 'granted') {
    finalStatus = (await Notifications.requestPermissionsAsync()).status;
  }

  if (finalStatus !== 'granted') {
    return null;
  }

  // Register background task so expo-notifications wakes the JS engine when
  // a data-only FCM message arrives while the app is killed/backgrounded.
  try {
    if (TaskManager.isTaskDefined(BACKGROUND_NOTIFICATION_TASK) &&
        !(await TaskManager.isTaskRegisteredAsync(BACKGROUND_NOTIFICATION_TASK))) {
      await Notifications.registerTaskAsync(BACKGROUND_NOTIFICATION_TASK);
    }
  } catch (taskError) {
    // Expo Go cannot run headless tasks, but it can still receive a push token.
    console.warn('[Push] Background task registration unavailable:', taskError);
  }

  const nativeToken = await Notifications.getDevicePushTokenAsync();
  return typeof nativeToken.data === 'string' ? nativeToken.data : null;
}


type MediaPermissionStatus = 'granted' | 'denied' | 'blocked';

type MediaPermissionResult = {
  camera: MediaPermissionStatus;
  microphone: MediaPermissionStatus;
};

function toMediaPermissionStatus(value: string): MediaPermissionStatus {
  if (value === PermissionsAndroid.RESULTS.GRANTED) {
    return 'granted';
  }

  if (value === PermissionsAndroid.RESULTS.NEVER_ASK_AGAIN) {
    return 'blocked';
  }

  return 'denied';
}

async function requestMediaPermissionsAsync(): Promise<MediaPermissionResult> {
  if (Platform.OS !== 'android' || Platform.Version < 23) {
    return { camera: 'granted', microphone: 'granted' };
  }

  // Request these one at a time. Android can drop one of two simultaneous
  // permission dialogs, which leaves WebView with camera but not microphone.
  const cameraResult = await PermissionsAndroid.request(
    PermissionsAndroid.PERMISSIONS.CAMERA,
    {
      title: 'Camera permission',
      message: 'LiveKit Meet needs camera access for video meetings.',
      buttonPositive: 'Allow'
    }
  );
  const microphoneResult = await PermissionsAndroid.request(
    PermissionsAndroid.PERMISSIONS.RECORD_AUDIO,
    {
      title: 'Microphone permission',
      message: 'LiveKit Meet needs microphone access so other participants can hear you.',
      buttonPositive: 'Allow'
    }
  );

  return {
    camera: toMediaPermissionStatus(cameraResult),
    microphone: toMediaPermissionStatus(microphoneResult)
  };
}

async function requestMicrophonePermissionAsync(): Promise<MediaPermissionStatus> {
  if (Platform.OS !== 'android' || Platform.Version < 23) {
    return 'granted';
  }

  try {
    const result = await PermissionsAndroid.request(
      PermissionsAndroid.PERMISSIONS.RECORD_AUDIO,
      {
        title: 'Microphone permission',
        message: 'LiveKit Meet needs microphone access so other participants can hear you.',
        buttonPositive: 'Allow'
      }
    );
    return toMediaPermissionStatus(result);
  } catch {
    return 'denied';
  }
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
  const [canGoBack, setCanGoBack] = useState(false);
  const [settingsMessage, setSettingsMessage] = useState<string | null>(null);
  const [currentUrl, setCurrentUrl] = useState(DEFAULT_SERVER_URL);
  const [pushToken, setPushToken] = useState<string | null>(null);
  const [fcmStatus, setFcmStatus] = useState('Requesting FCM token...');
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [mediaPermissions, setMediaPermissions] = useState<MediaPermissionResult>({
    camera: Platform.OS === 'android' ? 'denied' : 'granted',
    microphone: Platform.OS === 'android' ? 'denied' : 'granted'
  });
  const [mediaPermissionsChecked, setMediaPermissionsChecked] = useState(Platform.OS !== 'android');
  const [isRequestingMediaPermissions, setIsRequestingMediaPermissions] = useState(false);
  const [microphoneError, setMicrophoneError] = useState<string | null>(null);
  const [incomingCall, setIncomingCall] = useState<IncomingCall | null>(null);
  const incomingCallRef = useRef<IncomingCall | null>(null);
  const incomingNotificationIdRef = useRef<string | null>(null);

  const requestMediaAccess = useCallback(async (): Promise<MediaPermissionResult> => {
    setIsRequestingMediaPermissions(true);
    try {
      const result = await requestMediaPermissionsAsync();
      setMediaPermissions(result);
      return result;
    } catch {
      const result: MediaPermissionResult = { camera: 'denied', microphone: 'denied' };
      setMediaPermissions(result);
      return result;
    } finally {
      setMediaPermissionsChecked(true);
      setIsRequestingMediaPermissions(false);
    }
  }, []);

  const handleEnableMicrophone = useCallback(async () => {
    setMicrophoneError(null);
    const microphoneStatus = await requestMicrophonePermissionAsync();
    setMediaPermissions(previous => ({ ...previous, microphone: microphoneStatus }));
    if (microphoneStatus === 'blocked') {
      await Linking.openSettings();
      return;
    }

    if (microphoneStatus === 'granted') {
      webViewRef.current?.injectJavaScript(`
        window.livekitBridge?.recoverMicrophone?.().catch?.(() => false);
        true;
      `);
    }
  }, []);

  useEffect(() => {
    if (Platform.OS === 'android') {
      requestMediaAccess().catch(() => undefined);
    }
  }, [requestMediaAccess]);

  useEffect(() => {
    if (!microphoneError || mediaPermissions.microphone !== 'granted') {
      return;
    }

    const subscription = AppState.addEventListener('change', nextState => {
      if (nextState === 'active') {
        webViewRef.current?.injectJavaScript(`
          window.livekitBridge?.recoverMicrophone?.();
          true;
        `);
      }
    });

    return () => subscription.remove();
  }, [mediaPermissions.microphone, microphoneError]);

  useEffect(() => {
    const subscription = BackHandler.addEventListener('hardwareBackPress', () => {
      if (showSettings) {
        setShowSettings(false);
        return true;
      }

      if (canGoBack) {
        webViewRef.current?.goBack();
        return true;
      }

      return false;
    });

    return () => subscription.remove();
  }, [canGoBack, showSettings]);

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

  const stopIncomingRing = useCallback(() => {
    const invitationId = incomingCallRef.current?.invitationId;
    if (Platform.OS === 'android') {
      Vibration.cancel();
    }
    const notificationId = incomingNotificationIdRef.current;
    incomingNotificationIdRef.current = null;
    if (notificationId) {
      Notifications.dismissNotificationAsync(notificationId).catch(() => undefined);
    }
    if (invitationId) {
      try {
        incomingCallNativeModule?.dismissIncomingCall(invitationId);
      } catch {
        // The native full-screen module is unavailable in Expo Go.
      }
    }
    incomingCallRef.current = null;
    setIncomingCall(null);
  }, []);

  const handleIncomingNotification = useCallback((notification: Notifications.Notification) => {
    const content = notification.request.content;
    const data = content.data as PushData | undefined;
    if (data?.type === 'CANCEL_CALL') {
      const invitationId = data.invitationId ?? data.callUUID;
      if (typeof invitationId === 'string') {
        try {
          incomingCallNativeModule?.dismissIncomingCall(invitationId);
        } catch {
          // The native full-screen module is unavailable in Expo Go.
        }
      }
      if (!incomingCallRef.current?.invitationId || incomingCallRef.current.invitationId === invitationId) {
        stopIncomingRing();
      }
      return;
    }
    if (!data?.roomUrl) {
      return;
    }

    incomingNotificationIdRef.current = notification.request.identifier;
    const nextCall = {
      ...data,
      title: content.title ?? 'Incoming call',
      body: content.body ?? 'Someone is inviting you to a meeting.'
    };
    incomingCallRef.current = nextCall;
    setIncomingCall(nextCall);

    // Keep ringing while the in-app answer card is visible. The notification
    // channel also supplies the normal Android notification sound.
    if (Platform.OS === 'android') {
      Vibration.vibrate([0, 800, 600], true);
    }
  }, [stopIncomingRing]);

  const acceptIncomingCall = useCallback(() => {
    const call = incomingCall;
    stopIncomingRing();
    openNotificationRoom(call ?? undefined);
  }, [incomingCall, openNotificationRoom, stopIncomingRing]);

  const handleNotificationResponse = useCallback((response: Notifications.NotificationResponse) => {
    const data = response.notification.request.content.data as PushData;
    if (response.actionIdentifier === 'DECLINE_CALL') {
      stopIncomingRing();
      return;
    }

    if (response.actionIdentifier !== 'ACCEPT_CALL' &&
        response.actionIdentifier !== Notifications.DEFAULT_ACTION_IDENTIFIER) {
      return;
    }

    stopIncomingRing();
    openNotificationRoom(data);
  }, [openNotificationRoom, stopIncomingRing]);

  useEffect(() => {
    registerForPushNotificationsAsync()
      .then(token => {
        setPushToken(token);
        setFcmStatus(token ? 'FCM token ready; sign in to register' : 'FCM token unavailable');
      })
      .catch(() => {
        setPushToken(null);
        setFcmStatus('FCM token unavailable');
      });

    const handleDeepLink = (deepLink: string | null) => {
      if (!deepLink) return;
      try {
        let extracted: string | null = null;
        try {
          const parsed = new URL(deepLink);
          extracted = parsed.searchParams.get('roomUrl');
        } catch {
          const match = deepLink.match(/[?&]roomUrl=([^&]+)/);
          if (match && match[1]) {
            extracted = decodeURIComponent(match[1]);
          }
        }
        if (extracted) {
          stopIncomingRing();
          openNotificationRoom({ roomUrl: extracted });
        }
      } catch (_) {}
    };

    Linking.getInitialURL().then(handleDeepLink);
    const linkingSubscription = Linking.addEventListener('url', event => {
      handleDeepLink(event.url);
    });

    const receivedSubscription = Notifications.addNotificationReceivedListener(handleIncomingNotification);
    const responseSubscription = Notifications.addNotificationResponseReceivedListener(handleNotificationResponse);

    Notifications.getLastNotificationResponseAsync().then(response => {
      if (response) {
        handleNotificationResponse(response);
      }
    });

    return () => {
      linkingSubscription.remove();
      receivedSubscription.remove();
      responseSubscription.remove();
      stopIncomingRing();
    };
  }, [handleIncomingNotification, handleNotificationResponse, stopIncomingRing]);

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

  const handleNavigation = (navigation: WebViewNavigation) => {
    setCanGoBack(navigation.canGoBack);
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
        mediaType?: string;
        message?: string;
      };
      if (message.type === 'media-error' && message.mediaType === 'microphone') {
        setMicrophoneError(message.message ?? 'The WebView could not access the microphone.');
        return;
      }
      if (message.type === 'media-recovered' && message.mediaType === 'microphone') {
        setMicrophoneError(null);
        setMediaPermissions(previous => ({ ...previous, microphone: 'granted' }));
        return;
      }
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
      <SafeAreaView style={styles.centered} edges={['top', 'bottom']}>
        <ActivityIndicator size="large" color="#6f8cff" />
        <Text style={styles.errorText}>Loading app settings...</Text>
      </SafeAreaView>
    );
  }

  if (!mediaPermissionsChecked) {
    return (
      <SafeAreaView style={styles.centered} edges={['top', 'bottom']}>
        <ActivityIndicator size="large" color="#6f8cff" />
        <Text style={styles.errorText}>Requesting camera and microphone permission...</Text>
      </SafeAreaView>
    );
  }

  if (error) {
    return (
      <SafeAreaView style={styles.centered} edges={['top', 'bottom']}>
        <Text style={styles.errorTitle}>Unable to open LiveKit Meet</Text>
        <Text style={styles.errorText}>{error}</Text>
        <Text style={styles.errorText}>Server: {serverUrl}</Text>
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.safeArea} edges={['top', 'bottom']}>
      <View style={styles.container}>
        <View style={styles.topBar}>
          <View style={styles.topBarLeading}>
            {canGoBack && (
              <Pressable style={styles.backButton} onPress={() => webViewRef.current?.goBack()}>
                <Text style={styles.settingsButtonText}>‹ Back</Text>
              </Pressable>
            )}
            <Text style={styles.topBarTitle}>LiveKit Meet</Text>
          </View>
          <View style={styles.topBarActions}>
            {(mediaPermissions.microphone !== 'granted' || microphoneError) && (
              <Pressable
                style={styles.microphoneButton}
                onPress={handleEnableMicrophone}
                disabled={isRequestingMediaPermissions}
              >
                <Text style={styles.settingsButtonText}>
                  {isRequestingMediaPermissions ? 'Checking mic...' : 'Enable microphone'}
                </Text>
              </Pressable>
            )}
            <Pressable style={styles.settingsButton} onPress={() => {
              setServerUrlDraft(serverUrl);
              setSettingsMessage(null);
              setShowSettings(true);
            }}>
              <Text style={styles.settingsButtonText}>Settings</Text>
            </Pressable>
          </View>
        </View>
        {incomingCall && (
          <View style={styles.incomingCallOverlay}>
            <View style={styles.incomingCallCard}>
              <Text style={styles.incomingCallEyebrow}>Incoming call</Text>
              <Text style={styles.incomingCallTitle}>
                {incomingCall.fromDisplayName ?? incomingCall.callerDisplayName ?? incomingCall.fromUserName ?? 'Someone'}
              </Text>
              <Text style={styles.incomingCallRoom}>{incomingCall.roomName ?? 'LiveKit meeting'}</Text>
              <Text style={styles.incomingCallBody}>{incomingCall.body}</Text>
              <View style={styles.incomingCallActions}>
                <Pressable style={[styles.callActionButton, styles.declineCallButton]} onPress={stopIncomingRing}>
                  <Text style={styles.callActionText}>Decline</Text>
                </Pressable>
                <Pressable style={[styles.callActionButton, styles.answerCallButton]} onPress={acceptIncomingCall}>
                  <Text style={styles.callActionText}>Answer</Text>
                </Pressable>
              </View>
            </View>
          </View>
        )}
        <View style={styles.webViewContainer}>
          {isLoading && (
            <View style={styles.loadingOverlay}>
              <ActivityIndicator size="large" color="#6f8cff" />
            </View>
          )}
          <WebView
        style={styles.webView}
        ref={webViewRef}
        source={{ uri: currentUrl }}
        javaScriptEnabled
        domStorageEnabled
        sharedCookiesEnabled
        thirdPartyCookiesEnabled
        mediaPlaybackRequiresUserAction={false}
        allowsInlineMediaPlayback
        mediaCapturePermissionGrantType="grant"
        originWhitelist={['http://*', 'https://*']}
        onMessage={handleWebViewMessage}
        onLoadStart={() => {
          // Blazor/LiveKit navigation can fire load events after the room is
          // already usable. Do not put the native overlay back over a live room.
          if (!hasLoadedDocumentRef.current) {
            setIsLoading(true);
          }
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
          <View style={styles.pushStatus}>
            <Text style={styles.pushStatusText}>{fcmStatus}</Text>
          </View>
          {(mediaPermissions.microphone !== 'granted' || microphoneError) && (
            <Pressable style={styles.microphoneWarning} onPress={handleEnableMicrophone}>
              <Text style={styles.microphoneWarningText}>
                {microphoneError
                  ? `Microphone could not start: ${microphoneError}`
                  : 'Microphone access is off. Tap here to enable it.'}
              </Text>
            </Pressable>
          )}
        </View>
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
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: '#10182a'
  },
  container: {
    flex: 1,
    backgroundColor: '#10182a'
  },
  topBar: {
    minHeight: 48,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: 6,
    paddingHorizontal: 10,
    backgroundColor: '#10182a'
  },
  topBarTitle: {
    color: '#ffffff',
    fontSize: 14,
    fontWeight: '700'
  },
  topBarLeading: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8
  },
  backButton: {
    paddingVertical: 7,
    paddingHorizontal: 10,
    borderRadius: 6,
    backgroundColor: 'rgba(16, 24, 42, 0.88)'
  },
  topBarActions: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8
  },
  incomingCallOverlay: {
    position: 'absolute',
    zIndex: 20,
    top: 48,
    right: 0,
    bottom: 0,
    left: 0,
    alignItems: 'center',
    justifyContent: 'flex-start',
    padding: 18,
    backgroundColor: 'rgba(5, 10, 22, 0.78)'
  },
  incomingCallCard: {
    width: '100%',
    maxWidth: 420,
    padding: 22,
    borderRadius: 16,
    backgroundColor: '#1c2940',
    shadowColor: '#000000',
    shadowOpacity: 0.3,
    shadowRadius: 12,
    elevation: 8
  },
  incomingCallEyebrow: {
    marginBottom: 8,
    color: '#83a5ff',
    fontSize: 13,
    fontWeight: '700',
    textTransform: 'uppercase'
  },
  incomingCallTitle: {
    color: '#ffffff',
    fontSize: 24,
    fontWeight: '700'
  },
  incomingCallRoom: {
    marginTop: 4,
    color: '#d7def0',
    fontSize: 15,
    fontWeight: '600'
  },
  incomingCallBody: {
    marginTop: 12,
    color: '#aab6cf',
    fontSize: 13,
    lineHeight: 18
  },
  incomingCallActions: {
    flexDirection: 'row',
    justifyContent: 'flex-end',
    marginTop: 22,
    gap: 10
  },
  callActionButton: {
    minWidth: 100,
    alignItems: 'center',
    paddingVertical: 12,
    paddingHorizontal: 16,
    borderRadius: 8
  },
  declineCallButton: {
    backgroundColor: '#8f3f35'
  },
  answerCallButton: {
    backgroundColor: '#2e8b57'
  },
  callActionText: {
    color: '#ffffff',
    fontSize: 14,
    fontWeight: '700'
  },
  webViewContainer: {
    position: 'relative',
    flex: 1
  },
  webView: {
    flex: 1
  },
  loadingOverlay: {
    position: 'absolute',
    zIndex: 10,
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
    paddingVertical: 7,
    paddingHorizontal: 10,
    borderRadius: 6,
    backgroundColor: 'rgba(16, 24, 42, 0.88)'
  },
  microphoneButton: {
    paddingVertical: 7,
    paddingHorizontal: 10,
    borderRadius: 6,
    backgroundColor: '#a94b3e'
  },
  microphoneWarning: {
    position: 'absolute',
    zIndex: 3,
    right: 10,
    bottom: 46,
    left: 10,
    paddingVertical: 8,
    paddingHorizontal: 10,
    borderRadius: 6,
    backgroundColor: '#8f3f35'
  },
  microphoneWarningText: {
    color: '#ffffff',
    fontSize: 12,
    textAlign: 'center'
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
