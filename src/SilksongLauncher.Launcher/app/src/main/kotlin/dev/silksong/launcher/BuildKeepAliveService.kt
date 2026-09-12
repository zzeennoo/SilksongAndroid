package dev.silksong.launcher

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.IBinder
import android.os.PowerManager

/**
 * Keeps the launcher process in a user-visible state while the on-device build
 * is running. The build still belongs to [SetupActivity]; this service is the
 * process-lifetime anchor that makes pressing Home or an OEM background policy
 * less likely to discard its controller halfway through an IL2CPP run.
 */
class BuildKeepAliveService : Service() {
    companion object {
        private const val CHANNEL_ID = "silksong-build"
        private const val NOTIFICATION_ID = 0x5342
        private const val WAKE_LOCK_TIMEOUT_MS = 6L * 60L * 60L * 1000L

        fun start(context: Context) {
            context.startForegroundService(Intent(context, BuildKeepAliveService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, BuildKeepAliveService::class.java))
        }
    }

    private var wakeLock: PowerManager.WakeLock? = null

    override fun onCreate() {
        super.onCreate()
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                "Silksong build",
                NotificationManager.IMPORTANCE_LOW,
            ).apply { description = "Keeps the on-device port build running" },
        )
        startForeground(NOTIFICATION_ID, notification())
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "$packageName:build")
            .apply { acquire(WAKE_LOCK_TIMEOUT_MS) }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_NOT_STICKY

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        wakeLock?.let { if (it.isHeld) it.release() }
        wakeLock = null
        super.onDestroy()
    }

    private fun notification(): Notification {
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, SetupActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("Building Silksong")
            .setContentText("Build protection is active; keep the device plugged in")
            .setContentIntent(open)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .build()
    }
}
