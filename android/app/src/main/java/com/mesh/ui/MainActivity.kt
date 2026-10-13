package com.mesh.ui

import android.os.Bundle
import android.widget.ArrayAdapter
import android.widget.ListView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.mesh.core.*
import kotlinx.coroutines.launch
import java.util.UUID

class MainActivity : AppCompatActivity() {
    private val devices = mutableListOf<DiscoveredDevice>()
    private lateinit var adapter: ArrayAdapter<String>
    private lateinit var discovery: MeshDiscovery

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(com.mesh.R.layout.activity_main)

        val listView = findViewById<ListView>(com.mesh.R.id.deviceListView)
        adapter = ArrayAdapter(this, android.R.layout.simple_list_item_1, mutableListOf())
        listView.adapter = adapter

        discovery = MeshDiscovery(this)
        val selfUuid = UUID.randomUUID().toString() // stable per-install id; persisted separately in a future task
        discovery.advertise(selfUuid, android.os.Build.MODEL, "android", 51555)
        discovery.startBrowsing { device ->
            runOnUiThread {
                if (devices.any { it.uuid == device.uuid }) return@runOnUiThread
                devices.add(device)
                adapter.add(device.name)
            }
        }

        var selectedIndex = -1
        listView.setOnItemClickListener { _, _, position, _ -> selectedIndex = position }

        findViewById<android.widget.Button>(com.mesh.R.id.connectButton).setOnClickListener {
            if (selectedIndex < 0) return@setOnClickListener
            connectTo(devices[selectedIndex])
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        discovery.stop()
    }

    private fun connectTo(target: DiscoveredDevice) {
        lifecycleScope.launch connectJob@{
            val identity = IdentityStore.loadOrCreate(this@MainActivity)
            val session = PairingSession()
            try {
                session.connectTo(identity, target.ipAddress, target.port)
            } catch (ex: Exception) {
                Toast.makeText(this@MainActivity, "Connection failed: ${ex.message}", Toast.LENGTH_LONG).show()
                return@connectJob
            }

            val dao = MeshDatabase.getInstance(this@MainActivity).pairedDeviceDao()
            if (session.isPeerTrusted(dao, target.uuid)) {
                Toast.makeText(this@MainActivity, "Reconnected to ${target.name} (trusted).", Toast.LENGTH_LONG).show()
                return@connectJob
            }

            val pin = session.computePin(identity.first)
            PinConfirmDialog.show(this@MainActivity, pin) { matched ->
                lifecycleScope.launch {
                    if (!matched) {
                        Toast.makeText(this@MainActivity, "Pairing failed.", Toast.LENGTH_LONG).show()
                        return@launch
                    }
                    dao.upsert(PairedDevice(
                        uuid = target.uuid,
                        name = target.name,
                        fingerprint = session.peerFingerprint!!,
                        lastIp = target.ipAddress,
                        pairedAt = System.currentTimeMillis()
                    ))
                    Toast.makeText(this@MainActivity, "Paired with ${target.name}.", Toast.LENGTH_LONG).show()
                }
            }
        }
    }
}
