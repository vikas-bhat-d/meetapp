const { withAndroidManifest, withAppBuildGradle, withDangerousMod } = require('@expo/config-plugins');
const fs = require('fs');
const path = require('path');

function nativeSources(packageName) {
  const packageDeclaration = `package ${packageName}`;

  return {
    'IncomingCallModule.kt': `${packageDeclaration}

import android.os.Handler
import android.os.Looper
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.bridge.ReactContextBaseJavaModule
import com.facebook.react.bridge.ReactMethod

class IncomingCallModule(reactContext: ReactApplicationContext) : ReactContextBaseJavaModule(reactContext) {
  override fun getName(): String = "IncomingCall"

  @ReactMethod
  fun showIncomingCall(callerName: String, roomName: String, roomUrl: String, invitationId: String) {
    Handler(Looper.getMainLooper()).post {
      IncomingCallNotification.show(reactApplicationContext, callerName, roomName, roomUrl, invitationId)
    }
  }

  @ReactMethod
  fun dismissIncomingCall(invitationId: String) {
    Handler(Looper.getMainLooper()).post {
      IncomingCallNotification.dismiss(reactApplicationContext, invitationId)
    }
  }
}
`,
    'IncomingCallPackage.kt': `${packageDeclaration}

import com.facebook.react.ReactPackage
import com.facebook.react.bridge.NativeModule
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.uimanager.ViewManager

class IncomingCallPackage : ReactPackage {
  override fun createNativeModules(reactContext: ReactApplicationContext): List<NativeModule> =
    listOf(IncomingCallModule(reactContext))

  override fun createViewManagers(reactContext: ReactApplicationContext): List<ViewManager<*, *>> =
    emptyList()
}
`,
    'IncomingCallFirebaseService.kt': `${packageDeclaration}

import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage

class IncomingCallFirebaseService : FirebaseMessagingService() {
  override fun onMessageReceived(message: RemoteMessage) {
    val payload: Map<String, String> = message.data
    val messageType: String? = payload["type"]
    when (messageType) {
      "INCOMING_CALL" -> {
        val roomUrl: String = payload["roomUrl"] ?: return
        val invitationId: String = payload["invitationId"] ?: payload["callUUID"] ?: return
        IncomingCallNotification.show(
          applicationContext,
          payload["callerName"] ?: payload["fromDisplayName"] ?: "Someone",
          payload["roomName"] ?: "LiveKit meeting",
          roomUrl,
          invitationId
        )
      }
      "CANCEL_CALL" -> {
        val invitationId: String = payload["invitationId"] ?: payload["callUUID"] ?: return
        IncomingCallNotification.dismiss(applicationContext, invitationId)
      }
    }
  }
}
`,
    'IncomingCallNotification.kt': `${packageDeclaration}

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat

object IncomingCallNotification {
  const val ACTION_ACCEPT = "${packageName}.incoming_call.ACCEPT"
  const val ACTION_DECLINE = "${packageName}.incoming_call.DECLINE"
  const val EXTRA_CALLER_NAME = "callerName"
  const val EXTRA_ROOM_NAME = "roomName"
  const val EXTRA_ROOM_URL = "roomUrl"
  const val EXTRA_INVITATION_ID = "invitationId"
  const val EXTRA_NOTIFICATION_ID = "notificationId"

  private const val CHANNEL_ID = "incoming-calls-fullscreen-v1"

  fun show(context: Context, callerName: String, roomName: String, roomUrl: String, invitationId: String) {
    val notificationId = invitationId.hashCode()
    ensureChannel(context)

    val fullScreenIntent = callIntent(context, callerName, roomName, invitationId, roomUrl, null)
    val fullScreenPendingIntent = pendingActivity(context, notificationId, fullScreenIntent)
    val acceptPendingIntent = pendingActivity(
      context,
      notificationId + 1,
      callIntent(context, callerName, roomName, invitationId, roomUrl, ACTION_ACCEPT)
    )
    val declinePendingIntent = pendingActivity(
      context,
      notificationId + 2,
      callIntent(context, callerName, roomName, invitationId, roomUrl, ACTION_DECLINE)
    )

    val notification = NotificationCompat.Builder(context, CHANNEL_ID)
      .setSmallIcon(context.applicationInfo.icon)
      .setContentTitle("Incoming call from " + callerName)
      .setContentText(roomName)
      .setCategory(NotificationCompat.CATEGORY_CALL)
      .setPriority(NotificationCompat.PRIORITY_MAX)
      .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
      .setOngoing(true)
      .setAutoCancel(false)
      .setOnlyAlertOnce(false)
      .setColor(Color.rgb(46, 107, 234))
      .setVibrate(longArrayOf(0, 800, 600, 800))
      .setFullScreenIntent(fullScreenPendingIntent, true)
      .setContentIntent(fullScreenPendingIntent)
      .addAction(NotificationCompat.Action.Builder(android.R.drawable.ic_menu_call, "Accept", acceptPendingIntent).build())
      .addAction(NotificationCompat.Action.Builder(android.R.drawable.ic_delete, "Decline", declinePendingIntent).build())
      .setTimeoutAfter(120000L)
      .build()

    try {
      NotificationManagerCompat.from(context).notify(notificationId, notification)
    } catch (_: SecurityException) {
      // Android 13+ can reject notifications until POST_NOTIFICATIONS is granted.
    }
  }

  fun dismiss(context: Context, invitationId: String) {
    NotificationManagerCompat.from(context).cancel(invitationId.hashCode())
  }

  private fun ensureChannel(context: Context) {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
    val manager = context.getSystemService(NotificationManager::class.java)
    val channel = NotificationChannel(
      CHANNEL_ID,
      "Incoming calls",
      NotificationManager.IMPORTANCE_HIGH
    )
    channel.description = "Incoming LiveKit calls"
    channel.setSound(
      android.media.RingtoneManager.getDefaultUri(android.media.RingtoneManager.TYPE_RINGTONE),
      android.media.AudioAttributes.Builder()
        .setUsage(android.media.AudioAttributes.USAGE_NOTIFICATION_RINGTONE)
        .build()
    )
    channel.enableVibration(true)
    channel.vibrationPattern = longArrayOf(0, 800, 600, 800)
    channel.lockscreenVisibility = android.app.Notification.VISIBILITY_PUBLIC
    manager.createNotificationChannel(channel)
  }

  private fun callIntent(context: Context, callerName: String, roomName: String, invitationId: String, roomUrl: String, action: String?): Intent =
    Intent(context, IncomingCallActivity::class.java).apply {
      if (action != null) this.action = action
      putExtra(EXTRA_CALLER_NAME, callerName)
      putExtra(EXTRA_ROOM_NAME, roomName)
      putExtra(EXTRA_ROOM_URL, roomUrl)
      putExtra(EXTRA_INVITATION_ID, invitationId)
      putExtra(EXTRA_NOTIFICATION_ID, invitationId.hashCode())
      flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
    }

  private fun pendingActivity(context: Context, requestCode: Int, intent: Intent): PendingIntent {
    var flags = PendingIntent.FLAG_UPDATE_CURRENT
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) flags = flags or PendingIntent.FLAG_IMMUTABLE
    return PendingIntent.getActivity(context, requestCode, intent, flags)
  }
}
`,
    'IncomingCallActivity.kt': `${packageDeclaration}

import android.app.Activity
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.Window
import android.view.WindowManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView

class IncomingCallActivity : Activity() {
  private var invitationId: String = ""
  private var roomUrl: String = ""

  override fun onCreate(savedInstanceState: Bundle?) {
    super.onCreate(savedInstanceState)
    prepareWindow()
    handleIntent(intent)
  }

  override fun onNewIntent(intent: Intent?) {
    super.onNewIntent(intent)
    if (intent != null) {
      setIntent(intent)
      handleIntent(intent)
    }
  }

  override fun onBackPressed() {
    declineCall()
  }

  private fun prepareWindow() {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
      setShowWhenLocked(true)
      setTurnScreenOn(true)
    } else {
      window.addFlags(WindowManagerFlags.SHOW_WHEN_LOCKED or WindowManagerFlags.TURN_SCREEN_ON)
    }
    window.addFlags(WindowManagerFlags.KEEP_SCREEN_ON)
    window.statusBarColor = Color.rgb(16, 24, 42)
    window.navigationBarColor = Color.rgb(16, 24, 42)
  }

  private fun handleIntent(incomingIntent: Intent) {
    invitationId = incomingIntent.getStringExtra(IncomingCallNotification.EXTRA_INVITATION_ID) ?: ""
    roomUrl = incomingIntent.getStringExtra(IncomingCallNotification.EXTRA_ROOM_URL) ?: ""
    when (incomingIntent.action) {
      IncomingCallNotification.ACTION_ACCEPT -> acceptCall()
      IncomingCallNotification.ACTION_DECLINE -> declineCall()
      else -> showCallScreen(
        incomingIntent.getStringExtra(IncomingCallNotification.EXTRA_CALLER_NAME) ?: "Someone",
        incomingIntent.getStringExtra(IncomingCallNotification.EXTRA_ROOM_NAME) ?: "LiveKit meeting"
      )
    }
  }

  private fun showCallScreen(callerName: String, roomName: String) {
    val root = LinearLayout(this).apply {
      orientation = LinearLayout.VERTICAL
      gravity = Gravity.CENTER
      setPadding(32, 48, 32, 48)
      setBackgroundColor(Color.rgb(16, 24, 42))
    }

    val eyebrow = textView("INCOMING CALL", 14, Color.rgb(131, 165, 255), Typeface.BOLD)
    val caller = textView(callerName, 30, Color.WHITE, Typeface.BOLD)
    val room = textView(roomName, 16, Color.rgb(215, 222, 240), Typeface.NORMAL)
    root.addView(eyebrow, centeredParams(0, 12))
    root.addView(caller, centeredParams(0, 8))
    root.addView(room, centeredParams(0, 36))

    val actions = LinearLayout(this).apply {
      orientation = LinearLayout.HORIZONTAL
      gravity = Gravity.CENTER
    }
    val decline = Button(this).apply {
      text = "Decline"
      isAllCaps = false
      setTextColor(Color.WHITE)
      setBackgroundColor(Color.rgb(143, 63, 53))
      setOnClickListener { declineCall() }
    }
    val accept = Button(this).apply {
      text = "Accept"
      isAllCaps = false
      setTextColor(Color.WHITE)
      setBackgroundColor(Color.rgb(46, 139, 87))
      setOnClickListener { acceptCall() }
    }
    actions.addView(decline, buttonParams())
    actions.addView(accept, buttonParams())
    root.addView(actions)
    setContentView(root)
  }

  private fun acceptCall() {
    IncomingCallNotification.dismiss(this, invitationId)
    val deepLink = Uri.Builder()
      .scheme("livekitmeet")
      .authority("incoming")
      .appendQueryParameter("roomUrl", roomUrl)
      .build()
    startActivity(Intent(this, MainActivity::class.java).apply {
      action = Intent.ACTION_VIEW
      data = deepLink
      flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
    })
    finish()
  }

  private fun declineCall() {
    IncomingCallNotification.dismiss(this, invitationId)
    val deepLink = Uri.Builder()
      .scheme("livekitmeet")
      .authority("incoming")
      .appendQueryParameter("action", "decline")
      .appendQueryParameter("invitationId", invitationId)
      .build()
    startActivity(Intent(this, MainActivity::class.java).apply {
      action = Intent.ACTION_VIEW
      data = deepLink
      flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
    })
    finish()
  }

  private fun textView(value: String, size: Int, color: Int, style: Int) = TextView(this).apply {
    text = value
    textSize = size.toFloat()
    setTextColor(color)
    typeface = Typeface.create(Typeface.DEFAULT, style)
    gravity = Gravity.CENTER
  }

  private fun centeredParams(horizontal: Int, bottom: Int) = LinearLayout.LayoutParams(
    LinearLayout.LayoutParams.MATCH_PARENT,
    LinearLayout.LayoutParams.WRAP_CONTENT
  ).apply {
    setMargins(horizontal, 0, horizontal, bottom)
  }

  private fun buttonParams() = LinearLayout.LayoutParams(0, 56.dp(), 1f).apply {
    setMargins(6.dp(), 0, 6.dp(), 0)
  }

  private fun Int.dp(): Int = (this * resources.displayMetrics.density).toInt()

  private object WindowManagerFlags {
    const val SHOW_WHEN_LOCKED = WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED
    const val TURN_SCREEN_ON = WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON
    const val KEEP_SCREEN_ON = WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON
  }
}
`,
  };
}

module.exports = function withIncomingCall(config) {
  const packageName = config.android?.package || 'com.livekitmeet.mobile';

  config = withAppBuildGradle(config, config => {
    const firebaseDependency = "    implementation 'com.google.firebase:firebase-messaging:24.0.1'";
    if (!config.modResults.contents.includes('com.google.firebase:firebase-messaging')) {
      config.modResults.contents = config.modResults.contents.replace(
        'dependencies {',
        `dependencies {\n${firebaseDependency}`
      );
    }
    return config;
  });

  config = withAndroidManifest(config, config => {
    const application = config.modResults.manifest.application?.[0];
    if (application) {
      application.activity = application.activity || [];
      const exists = application.activity.some(
        activity => activity.$?.['android:name'] === '.IncomingCallActivity'
      );
      if (!exists) {
        application.activity.push({
          $: {
            'android:name': '.IncomingCallActivity',
            'android:exported': 'false',
            'android:excludeFromRecents': 'true',
            'android:launchMode': 'singleTop',
            'android:showWhenLocked': 'true',
            'android:turnScreenOn': 'true',
            'android:theme': '@style/AppTheme',
          },
        });
      }

      application.service = application.service || [];
      const serviceExists = application.service.some(
        service => service.$?.['android:name'] === '.IncomingCallFirebaseService'
      );
      if (!serviceExists) {
        application.service.push({
          $: {
            'android:name': '.IncomingCallFirebaseService',
            'android:exported': 'false',
            'android:stopWithTask': 'false',
          },
          'intent-filter': [
            {
              action: [{ $: { 'android:name': 'com.google.firebase.MESSAGING_EVENT' } }],
            },
          ],
        });
      }
    }
    return config;
  });

  return withDangerousMod(config, [
    'android',
    async config => {
      const packageDirectory = path.join(
        config.modRequest.platformProjectRoot,
        'app',
        'src',
        'main',
        'java',
        ...packageName.split('.')
      );
      fs.mkdirSync(packageDirectory, { recursive: true });
      const sources = nativeSources(packageName);
      for (const [fileName, content] of Object.entries(sources)) {
        fs.writeFileSync(path.join(packageDirectory, fileName), content);
      }

      const applicationFile = path.join(packageDirectory, 'MainApplication.kt');
      if (fs.existsSync(applicationFile)) {
        let applicationSource = fs.readFileSync(applicationFile, 'utf8');
        if (!applicationSource.includes('add(IncomingCallPackage())')) {
          applicationSource = applicationSource.replace(
            'PackageList(this).packages.apply {',
            'PackageList(this).packages.apply {\n              add(IncomingCallPackage())'
          );
          fs.writeFileSync(applicationFile, applicationSource);
        }
      }
      return config;
    },
  ]);
};
