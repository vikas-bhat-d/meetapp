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
  View,
  Vibration,
  NativeModules
} from 'react-native';
import * as Device from 'expo-device';
import Constants from 'expo-constants';
import * as Notifications from 'expo-notifications';
import * as TaskManager from 'expo-task-manager';
import { WebView, WebViewMessageEvent, WebViewNavigation } from 'react-native-webview';
import { SafeAreaView } from 'react-native-safe-area-context';
import { appendAppLog, loadAppConfig } from './appStorage';

// ─── Background notification task ────────────────────────────────────────────
// This task name MUST match what is passed to Notifications.registerTaskAsync.
const BACKGROUND_NOTIFICATION_TASK = 'BACKGROUND_NOTIFICATION_TASK';
const INCOMING_CALL_CATEGORY = 'INCOMING_CALL';
const INCOMING_CALL_CHANNEL = 'incoming-calls-v2';

type IncomingCallNativeModule = {
  showIncomingCall: (callerName: string, roomName: string, roomUrl: string, declineUrl: string, invitationId: string, declineToken: string) => void;
  dismissIncomingCall: (invitationId: string) => void;
};

const incomingCallNativeModule = NativeModules.IncomingCall as IncomingCallNativeModule | undefined;

type PushData = {
  roomUrl?: string;
  declineUrl?: string;
  roomName?: string;
  invitationId?: string;
  callUUID?: string;
  declineToken?: string;
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
    await appendAppLog('Background notification task error', { error: String(error) });
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
      await incomingCallNativeModule.showIncomingCall(
        callerName,
        roomName,
        roomUrl,
        (payload.declineUrl as string | undefined) ?? '',
        notificationId,
        (payload.declineToken as string | undefined) ?? ''
      );
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
    'https://192.168.29.214:8443';

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

const DEFAULT_SERVER_URL = normalizeServerUrl(defaultServerUrl) ?? 'https://192.168.29.214:8443';

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
    await appendAppLog('Background notification task registration unavailable', {
      error: String(taskError)
    });
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

  let cameraResult = PermissionsAndroid.RESULTS.DENIED;
  try {
    const alreadyGranted = await PermissionsAndroid.check(PermissionsAndroid.PERMISSIONS.CAMERA);
    cameraResult = alreadyGranted
      ? PermissionsAndroid.RESULTS.GRANTED
      : await PermissionsAndroid.request(
          PermissionsAndroid.PERMISSIONS.CAMERA,
          {
            title: 'Camera permission',
            message: 'LiveKit Meet needs camera access for video meetings.',
            buttonPositive: 'Allow'
          }
        );
  } catch {
    cameraResult = PermissionsAndroid.RESULTS.DENIED;
  }

  await new Promise<void>(resolve => setTimeout(resolve, 350));

  let microphoneResult = PermissionsAndroid.RESULTS.DENIED;
  try {
    const alreadyGranted = await PermissionsAndroid.check(PermissionsAndroid.PERMISSIONS.RECORD_AUDIO);
    microphoneResult = alreadyGranted
      ? PermissionsAndroid.RESULTS.GRANTED
      : await PermissionsAndroid.request(
          PermissionsAndroid.PERMISSIONS.RECORD_AUDIO,
          {
            title: 'Microphone permission',
            message: 'LiveKit Meet needs microphone access so other participants can hear you.',
            buttonPositive: 'Allow'
          }
        );
  } catch {
    microphoneResult = PermissionsAndroid.RESULTS.DENIED;
  }

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
    const alreadyGranted = await PermissionsAndroid.check(PermissionsAndroid.PERMISSIONS.RECORD_AUDIO);
    if (alreadyGranted) {
      return 'granted';
    }

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
  const appStateRef = useRef(AppState.currentState);
  const reloadOnResumeRef = useRef(false);
  const [serverUrl, setServerUrl] = useState(DEFAULT_SERVER_URL);
  const [configurationReady, setConfigurationReady] = useState(false);
  const [canGoBack, setCanGoBack] = useState(false);
  const [currentUrl, setCurrentUrl] = useState(DEFAULT_SERVER_URL);
  const [webViewKey, setWebViewKey] = useState(0);
  const [pushToken, setPushToken] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [mediaPermissions, setMediaPermissions] = useState<MediaPermissionResult>({
    camera: Platform.OS === 'android' ? 'denied' : 'granted',
    microphone: Platform.OS === 'android' ? 'denied' : 'granted'
  });
  const [mediaPermissionsChecked, setMediaPermissionsChecked] = useState(Platform.OS !== 'android');
  const [microphoneError, setMicrophoneError] = useState<string | null>(null);
  const [incomingCall, setIncomingCall] = useState<IncomingCall | null>(null);
  const incomingCallRef = useRef<IncomingCall | null>(null);
  const incomingNotificationIdRef = useRef<string | null>(null);
  const pendingCallOutcomeRef = useRef<{ invitationId: string; outcome: 'accept' | 'decline' | 'end' } | null>(null);

  const requestMediaAccess = useCallback(async (): Promise<MediaPermissionResult> => {
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

  const reloadWebView = useCallback((reason: string) => {
    hasLoadedDocumentRef.current = false;
    setCanGoBack(false);
    setError(null);
    setIsLoading(true);
    setWebViewKey(previousKey => previousKey + 1);
    void appendAppLog('WebView reloaded', { reason });
  }, []);

  useEffect(() => {
    const subscription = AppState.addEventListener('change', nextState => {
      const previousState = appStateRef.current;
      if (nextState === 'background') {
        reloadOnResumeRef.current = true;
        webViewRef.current?.stopLoading();
      } else if (
        nextState === 'active' &&
        previousState === 'background' &&
        reloadOnResumeRef.current &&
        configurationReady
      ) {
        reloadOnResumeRef.current = false;
        reloadWebView('App resumed after background');
      }

      appStateRef.current = nextState;
    });

    return () => subscription.remove();
  }, [configurationReady, reloadWebView]);

  useEffect(() => {
    if (Platform.OS === 'android' && configurationReady) {
      requestMediaAccess().catch(() => undefined);
    }
  }, [configurationReady, requestMediaAccess]);

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
      if (canGoBack) {
        webViewRef.current?.goBack();
        return true;
      }

      return false;
    });

    return () => subscription.remove();
  }, [canGoBack]);

  useEffect(() => {
    let active = true;

    loadAppConfig(DEFAULT_SERVER_URL)
      .then(appConfig => {
        if (!active) {
          return;
        }

        const configuredUrl = normalizeServerUrl(appConfig.serverUrl) ?? DEFAULT_SERVER_URL;
        setServerUrl(configuredUrl);
        setCurrentUrl(pendingNotificationUrlRef.current ?? configuredUrl);
        setConfigurationReady(true);
        setIsLoading(true);
        void appendAppLog('Mobile configuration loaded', { retainLog: appConfig.retainLog });
      })
      .catch(error => {
        if (!active) {
          return;
        }

        void appendAppLog('Mobile configuration could not be loaded', { error: String(error) });
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
    const token = JSON.stringify(pushToken);
    return `
      (async function () {
        const fcmToken = ${token};

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

        document.querySelectorAll('form').forEach(function (form) {
          const action = new URL(form.action || window.location.href, window.location.href);
          if (action.pathname !== '/api/auth/logout' || form.dataset.fcmLogoutHooked === 'true') {
            return;
          }

          form.dataset.fcmLogoutHooked = 'true';
          form.addEventListener('submit', function (event) {
            if (form.dataset.fcmLogoutSubmitting === 'true') {
              return;
            }

            event.preventDefault();
            form.dataset.fcmLogoutSubmitting = 'true';
            fetch('/api/devices/fcm/unregister', {
              method: 'POST',
              credentials: 'include',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ token: fcmToken, platform: 'android' })
            }).catch(function () {}).finally(function () {
              form.submit();
            });
          });
        });
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

  const reportCallOutcome = useCallback((invitationId: string | undefined, outcome: 'accept' | 'decline' | 'end') => {
    if (!invitationId) {
      return;
    }

    if (!hasLoadedDocumentRef.current) {
      pendingCallOutcomeRef.current = { invitationId, outcome };
      return;
    }

    const endpoint = `/api/call-invitations/${encodeURIComponent(invitationId)}/${outcome}`;
    webViewRef.current?.injectJavaScript(`
      fetch(${JSON.stringify(endpoint)}, {
        method: 'POST',
        credentials: 'include'
      }).catch(() => undefined);
      true;
    `);
  }, []);

  const flushPendingCallOutcome = useCallback(() => {
    const pending = pendingCallOutcomeRef.current;
    if (!pending || !hasLoadedDocumentRef.current) {
      return;
    }

    pendingCallOutcomeRef.current = null;
    reportCallOutcome(pending.invitationId, pending.outcome);
  }, [reportCallOutcome]);

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
    reportCallOutcome(call?.invitationId ?? call?.callUUID, 'accept');
    stopIncomingRing();
    openNotificationRoom(call ?? undefined);
  }, [incomingCall, openNotificationRoom, reportCallOutcome, stopIncomingRing]);

  const declineIncomingCall = useCallback(() => {
    reportCallOutcome(
      incomingCallRef.current?.invitationId ?? incomingCallRef.current?.callUUID,
      'decline');
    stopIncomingRing();
  }, [reportCallOutcome, stopIncomingRing]);

  const handleNotificationResponse = useCallback((response: Notifications.NotificationResponse) => {
    const data = response.notification.request.content.data as PushData;
    if (response.actionIdentifier === 'DECLINE_CALL') {
      reportCallOutcome(data.invitationId ?? data.callUUID, 'decline');
      stopIncomingRing();
      return;
    }

    if (response.actionIdentifier !== 'ACCEPT_CALL' &&
        response.actionIdentifier !== Notifications.DEFAULT_ACTION_IDENTIFIER) {
      return;
    }

    stopIncomingRing();
    openNotificationRoom(data);
  }, [openNotificationRoom, reportCallOutcome, stopIncomingRing]);

  useEffect(() => {
    if (!configurationReady || !mediaPermissionsChecked) {
      return;
    }

    let active = true;
    registerForPushNotificationsAsync()
      .then(token => {
        if (!active) {
          return;
        }

        setPushToken(token);
        void appendAppLog(token ? 'FCM token ready' : 'FCM token unavailable');
      })
      .catch(error => {
        if (!active) {
          return;
        }

        setPushToken(null);
        void appendAppLog('FCM token registration failed', { error: String(error) });
      });

    return () => {
      active = false;
    };
  }, [configurationReady, mediaPermissionsChecked]);

  useEffect(() => {

    const handleDeepLink = (deepLink: string | null) => {
      if (!deepLink) return;
      try {
        let extracted: string | null = null;
        let action: string | null = null;
        let invitationId: string | null = null;
        try {
          const parsed = new URL(deepLink);
          extracted = parsed.searchParams.get('roomUrl');
          action = parsed.searchParams.get('action');
          invitationId = parsed.searchParams.get('invitationId');
        } catch {
          const match = deepLink.match(/[?&]roomUrl=([^&]+)/);
          if (match && match[1]) {
            extracted = decodeURIComponent(match[1]);
          }
        }
        if (action === 'decline') {
          reportCallOutcome(invitationId ?? undefined, 'decline');
          stopIncomingRing();
          return;
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
  }, [handleIncomingNotification, handleNotificationResponse, reportCallOutcome, stopIncomingRing]);

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
        void appendAppLog('FCM registration response', { status: message.status ?? 0 });
      }
    } catch {
      // Ignore messages that are not diagnostics from our registration script.
    }
  };

  if (!configurationReady) {
    return (
      <SafeAreaView style={styles.centered} edges={['top', 'bottom']}>
        <ActivityIndicator size="large" color="#f12d36" />
        <Text style={styles.errorText}>Loading app settings...</Text>
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
                <Pressable style={[styles.callActionButton, styles.declineCallButton]} onPress={declineIncomingCall}>
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
              <ActivityIndicator size="large" color="#f12d36" />
            </View>
          )}
          <WebView
        style={styles.webView}
        ref={webViewRef}
        key={webViewKey}
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
          flushPendingCallOutcome();
        }}
        onNavigationStateChange={handleNavigation}
        onError={event => {
          setIsLoading(false);
          const description = event.nativeEvent.description || 'The server could not be reached.';
          setError(description);
          void appendAppLog('WebView failed to load', { error: description });
        }}
          />
          {mediaPermissionsChecked && (mediaPermissions.microphone !== 'granted' || microphoneError) && (
            <Pressable style={styles.microphoneWarning} onPress={handleEnableMicrophone}>
              <Text style={styles.microphoneWarningText}>
                {microphoneError
                  ? `Microphone could not start: ${microphoneError}`
                  : 'Microphone access is off. Tap here to enable it.'}
              </Text>
            </Pressable>
          )}
        </View>
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: '#f7f8fa'
  },
  container: {
    flex: 1,
    backgroundColor: '#f7f8fa'
  },
  incomingCallOverlay: {
    position: 'absolute',
    zIndex: 20,
    top: 0,
    right: 0,
    bottom: 0,
    left: 0,
    alignItems: 'center',
    justifyContent: 'flex-start',
    padding: 18,
    backgroundColor: 'rgba(32, 37, 43, 0.32)'
  },
  incomingCallCard: {
    width: '100%',
    maxWidth: 420,
    padding: 22,
    borderRadius: 16,
    backgroundColor: '#ffffff',
    shadowColor: '#000000',
    shadowOpacity: 0.3,
    shadowRadius: 12,
    elevation: 8
  },
  incomingCallEyebrow: {
    marginBottom: 8,
    color: '#d91f2a',
    fontSize: 13,
    fontWeight: '700',
    textTransform: 'uppercase'
  },
  incomingCallTitle: {
    color: '#20252b',
    fontSize: 24,
    fontWeight: '700'
  },
  incomingCallRoom: {
    marginTop: 4,
    color: '#747b84',
    fontSize: 15,
    fontWeight: '600'
  },
  incomingCallBody: {
    marginTop: 12,
    color: '#747b84',
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
    backgroundColor: '#f12d36'
  },
  answerCallButton: {
    backgroundColor: '#2f9a69'
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
    backgroundColor: '#f7f8fa'
  },
  microphoneWarning: {
    position: 'absolute',
    zIndex: 3,
    right: 10,
    bottom: 10,
    left: 10,
    paddingVertical: 8,
    paddingHorizontal: 10,
    borderRadius: 6,
    backgroundColor: '#f12d36'
  },
  microphoneWarningText: {
    color: '#ffffff',
    fontSize: 12,
    textAlign: 'center'
  },
  centered: {
    flex: 1,
    padding: 24,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#f7f8fa'
  },
  errorTitle: {
    marginBottom: 12,
    color: '#20252b',
    fontSize: 20,
    fontWeight: '700',
    textAlign: 'center'
  },
  errorText: {
    marginTop: 6,
    color: '#747b84',
    textAlign: 'center'
  }
});
