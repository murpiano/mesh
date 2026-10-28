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

        fun start(context: Context, peerName: String) {
            val intent = Intent(context, CastService::class.java).putExtra(EXTRA_PEER_NAME, peerName)
            ContextCompat.startForegroundService(context, intent)
        }

        fun stop(context: Context) {
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
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ServiceCompat.startForeground(this, NOTIFICATION_ID, notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
        return START_NOT_STICKY
    }

    private fun createChannelIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Mesh cast", NotificationManager.IMPORTANCE_LOW))
    }
}
