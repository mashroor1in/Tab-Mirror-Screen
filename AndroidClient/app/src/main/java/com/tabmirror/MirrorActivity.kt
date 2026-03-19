package com.tabmirror

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Bundle
import android.util.Log
import android.view.*
import android.widget.*
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import com.tabmirror.databinding.ActivityMirrorBinding

/**
 * Main (and only) Activity for Tab Mirror.
 * Manages the full-screen SurfaceView, connection UI, and lifecycle of
 * [StreamReceiver] and [TouchSender].
 */
class MirrorActivity : AppCompatActivity(), SurfaceHolder.Callback {

    private lateinit var binding: ActivityMirrorBinding

    private var decoder: H264Decoder? = null
    private var receiver: StreamReceiver? = null
    private var touchSender: TouchSender? = null
    private var statsReceiver: StatsReceiver? = null
    private var gestureMacro: GestureMacro? = null
    private var clipboardSync: ClipboardSync? = null
    private var nsdManager: NsdManager? = null
    private var discoveryListener: NsdManager.DiscoveryListener? = null

    companion object {
        private const val TAG = "MirrorActivity"
        private const val SERVICE_TYPE = "_tabmirror._tcp"
        const val DEFAULT_VIDEO_PORT   = 7531
        const val DEFAULT_CONTROL_PORT = 7532
        const val DEFAULT_STATS_PORT   = 7534
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMirrorBinding.inflate(layoutInflater)
        setContentView(binding.root)

        goFullscreen()
        setupSurface()
        setupUi()
    }

    override fun onDestroy() {
        super.onDestroy()
        stopStreaming()
    }

    // ── UI setup ──────────────────────────────────────────────────────────

    private fun setupUi() {
        binding.btnConnect.setOnClickListener {
            val host = binding.etHost.text.toString().trim()
            if (host.isEmpty()) {
                binding.tvStatus.text = "Enter a host IP address"
                return@setOnClickListener
            }
            startStreaming(host)
        }

        binding.btnDiscover.setOnClickListener {
            binding.tvStatus.text = "Searching for Tab Mirror on local network..."
            startDiscovery()
        }

        binding.btnDisconnect.setOnClickListener {
            stopStreaming()
            binding.connectionPanel.visibility = View.VISIBLE
            binding.btnDisconnect.visibility   = View.GONE
            binding.tvStatus.text = "Disconnected."
        }

        binding.cbUsbMode.setOnCheckedChangeListener { _, isChecked ->
            if (isChecked) {
                binding.etHost.setText("127.0.0.1")
                binding.tvStatus.text = "USB mode: run 'adb reverse tcp:7531 tcp:7531 && adb reverse tcp:7532 tcp:7532 && adb reverse tcp:7533 tcp:7533' on your PC first."
            }
        }
    }

    // ── SurfaceHolder callbacks ───────────────────────────────────────────

    private fun setupSurface() {
        binding.surfaceView.holder.addCallback(this)
        binding.surfaceView.keepScreenOn = true
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        Log.i(TAG, "Surface created")
        // Touch listener is set in startStreaming() once all components are ready
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        Log.i(TAG, "Surface changed: ${width}x${height}")
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        Log.i(TAG, "Surface destroyed")
        stopStreaming()
    }

    // ── Streaming control ─────────────────────────────────────────────────

    private fun startStreaming(host: String) {
        val surface = binding.surfaceView.holder.surface ?: run {
            binding.tvStatus.text = "Surface not ready yet — try again"
            return
        }

        binding.tvStatus.text = "Connecting to $host..."
        binding.connectionPanel.visibility = View.GONE
        binding.btnDisconnect.visibility   = View.VISIBLE

        // Width/height will be updated by StreamReceiver once it reads stream metadata
        decoder = H264Decoder(surface)

        receiver = StreamReceiver(
            host       = host,
            videoPort  = DEFAULT_VIDEO_PORT,
            decoder    = decoder!!,
            onConnected    = { binding.tvStatus.text = "Streaming! ✓" },
            onDisconnected = { msg -> binding.tvStatus.text = "Disconnected: $msg" }
        ).also { it.start(lifecycleScope) }

        val clipboardManager = ClipboardSync(this) { text ->
            touchSender?.sendClipboard(text)
        }.also { it.start() }
        clipboardSync = clipboardManager

        val ts = TouchSender(host, DEFAULT_CONTROL_PORT) { text ->
            clipboardSync?.setRemoteClipboard(text)
        }.also { it.start(lifecycleScope) }
        touchSender = ts

        val gm = GestureMacro(host, DEFAULT_CONTROL_PORT, lifecycleScope)
        gestureMacro = gm

        // ── Single unified touch listener — order matters:
        //    1. GestureMacro: checks 3-finger swipes, stylus double-tap (returns false → doesn't consume)
        //    2. TouchSender: handles all mouse/stylus/scroll events (returns true → consumes)
        //    3. 4-finger tap: toggles the debug overlay regardless
        ts.setupGestureDetector(this, binding.surfaceView)
        binding.surfaceView.setOnTouchListener { v, event ->
            // 4-finger tap → toggle debug overlay (intercept before any other handler)
            if (event.pointerCount == 4 && event.actionMasked == MotionEvent.ACTION_DOWN) {
                binding.debugOverlay.visibility = if (binding.debugOverlay.visibility == android.view.View.VISIBLE)
                    android.view.View.GONE else android.view.View.VISIBLE
                return@setOnTouchListener true
            }
            // Gesture macros (3-finger swipes etc.) — does NOT consume the event
            gm.handleEvent(v, event)
            // Normal touch + stylus forwarding — consumes the event
            ts.handleEvent(v, event)
        }

        binding.surfaceView.setOnHoverListener { v, event ->
            ts.handleEvent(v, event)
        }

        statsReceiver = StatsReceiver(host, DEFAULT_STATS_PORT, binding.debugOverlay).also { it.start(lifecycleScope) }
    }

    private fun stopStreaming() {
        nsdManager?.stopServiceDiscovery(discoveryListener)
        receiver?.stop()
        touchSender?.stop()
        statsReceiver?.stop()
        clipboardSync?.stop()
        decoder?.release()
        receiver      = null
        touchSender   = null
        statsReceiver = null
        gestureMacro  = null
        decoder       = null
    }

    // ── mDNS discovery ────────────────────────────────────────────────────

    private fun startDiscovery() {
        nsdManager = getSystemService(Context.NSD_SERVICE) as NsdManager

        discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(regType: String) {
                Log.i(TAG, "Discovery started: $regType")
            }
            override fun onServiceFound(info: NsdServiceInfo) {
                Log.i(TAG, "Service found: ${info.serviceName} (${info.serviceType})")
                nsdManager?.resolveService(info, object : NsdManager.ResolveListener {
                    override fun onServiceResolved(resolvedInfo: NsdServiceInfo) {
                        val ip   = resolvedInfo.host.hostAddress ?: return
                        val port = resolvedInfo.port
                        Log.i(TAG, "Found TabMirror at $ip:$port")
                        runOnUiThread {
                            binding.etHost.setText(ip)
                            binding.tvStatus.text = "Found $ip — tap Connect"
                            Toast.makeText(this@MirrorActivity, "Found host: $ip", Toast.LENGTH_SHORT).show()
                        }
                    }
                    override fun onResolveFailed(i: NsdServiceInfo, code: Int) {
                        Log.w(TAG, "Resolve failed: $code for ${i.serviceName}")
                        runOnUiThread {
                            binding.tvStatus.text = "Found service but resolution failed (Code: $code)"
                        }
                    }
                })
            }
            override fun onServiceLost(info: NsdServiceInfo)    { 
                Log.i(TAG, "Service lost: ${info.serviceName}") 
                runOnUiThread { binding.tvStatus.text = "Service lost." }
            }
            override fun onDiscoveryStopped(sType: String)       { Log.i(TAG, "Discovery stopped") }
            override fun onStartDiscoveryFailed(s: String, e: Int) { 
                Log.e(TAG, "Start failed: $e") 
                runOnUiThread { binding.tvStatus.text = "Discovery start failed: $e" }
            }
            override fun onStopDiscoveryFailed(s: String, e: Int)  { Log.e(TAG, "Stop failed: $e") }
        }

        nsdManager?.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
    }

    // ── Keyboard Relay ────────────────────────────────────────────────────

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (touchSender != null && !isLocalKey(keyCode)) {
            touchSender?.sendKeyboard(0, keyCode, event.unicodeChar)
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean {
        if (touchSender != null && !isLocalKey(keyCode)) {
            touchSender?.sendKeyboard(1, keyCode, event.unicodeChar)
            return true
        }
        return super.onKeyUp(keyCode, event)
    }

    private fun isLocalKey(keyCode: Int): Boolean = when (keyCode) {
        KeyEvent.KEYCODE_VOLUME_UP,
        KeyEvent.KEYCODE_VOLUME_DOWN,
        KeyEvent.KEYCODE_POWER,
        KeyEvent.KEYCODE_HOME -> true
        else -> false
    }

    // ── Immersive mode ────────────────────────────────────────────────────

    private fun goFullscreen() {
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }
}
