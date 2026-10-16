package com.mesh.core

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat

/**
 * Foreground service with no logic of its own: its only job is to raise this process to
 * foreground importance while a [MeshSession] is connected, so Android (and aggressive OEM
 * battery managers such as MIUI's) don't freeze the process or cut its network access once the
 * app is minimized. [MainActivity] starts it when a session attaches and stops it when the user
 * disconnects (or the session is otherwise torn down for good); the actual socket, read/write
 * and reconnect loops all keep living in the Activity exactly as before.
 */
class MeshConnectionService : Service() {
    companion object {
        private const val CHANNEL_ID = "mesh_connection"
        private const val NOTIFICATION_ID = 1

        fun start(context: Context, peerName: String) {
            val intent = Intent(context, MeshConnectionService::class.java)
                .putExtra(EXTRA_PEER_NAME, peerName)
            ContextCompat.startForegroundService(context, intent)
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, MeshConnectionService::class.java))
        }

        private const val EXTRA_PEER_NAME = "peerName"
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val peerName = intent?.getStringExtra(EXTRA_PEER_NAME) ?: "device"
        createChannelIfNeeded()
        val notification = NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Mesh")
            .setContentText("Connected to $peerName")
            .setSmallIcon(android.R.drawable.stat_sys_upload_done)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            ServiceCompat.startForeground(this, NOTIFICATION_ID, notification,
                android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
        return START_STICKY
    }

    private fun createChannelIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Mesh connection", NotificationManager.IMPORTANCE_LOW))
    }
}
