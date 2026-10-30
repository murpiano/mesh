package com.mesh.core

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat

/**
 * Foreground service with no logic of its own, like [MeshConnectionService] — but declared as
 * `mediaProjection`, which is what Android 14 and newer require to be running *before* a
 * MediaProjection is used at all. [MainActivity] starts it as soon as the capture consent comes
 * back and stops it when the cast ends.
 */
class CastService : Service() {
    companion object {
        private const val CHANNEL_ID = "mesh_cast"
        private const val NOTIFICATION_ID = 2
        private const val EXTRA_PEER_NAME = "peerName"

        // A service asked for with startForegroundService MUST reach startForeground, or the
        // system kills the whole app ten seconds later. Both flags below exist for that: a stop
        // that arrives before the service is up is remembered instead of being carried out, and
        // the service stops itself once it is foreground.
        @Volatile private var running = false
        @Volatile private var stopRequested = false
        private var onStarted: ((String?) -> Unit)? = null

        /**
         * Starts the service and calls [onReady] on the main thread once it is really in the
         * foreground — null when it is, a reason when it could not be. Nothing may use a
         * MediaProjection before that: Android 14 and newer refuse one without this service.
         */
        fun start(context: Context, peerName: String, onReady: (String?) -> Unit) {
            stopRequested = false
            onStarted = onReady
            val intent = Intent(context, CastService::class.java).putExtra(EXTRA_PEER_NAME, peerName)
            try {
                ContextCompat.startForegroundService(context, intent)
            } catch (e: Exception) {
                onStarted = null
                onReady("The phone could not start the casting service: ${e.message}")
            }
        }

        fun stop(context: Context) {
            onStarted = null
            if (!running) {
                stopRequested = true // the service stops itself as soon as it is foreground
                return
            }
            stopRequested = false
            context.stopService(Intent(context, CastService::class.java))
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val peerName = intent?.getStringExtra(EXTRA_PEER_NAME) ?: "device"
        createChannelIfNeeded()
        val notification = NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Mesh")
            .setContentText("Casting this screen to $peerName")
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setOngoing(true)
            .build()
        val failure = try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                ServiceCompat.startForeground(this, NOTIFICATION_ID, notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
            running = true
            null
        } catch (e: Exception) {
            "The phone refused to run the casting service: ${e.message}"
        }

        val ready = onStarted
        onStarted = null
        if (failure != null) {
            stopSelf()
            ready?.invoke(failure)
            return START_NOT_STICKY
        }
        if (stopRequested) { // the cast was called off while this service was coming up
            stopRequested = false
            stopSelf()
            return START_NOT_STICKY
        }
        ready?.invoke(null)
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        super.onDestroy()
        running = false
        onStarted = null
    }

    private fun createChannelIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Mesh cast", NotificationManager.IMPORTANCE_LOW))
    }
}
