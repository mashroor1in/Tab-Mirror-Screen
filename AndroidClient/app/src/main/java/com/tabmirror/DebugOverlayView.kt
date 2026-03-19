package com.tabmirror

import android.content.Context
import android.graphics.*
import android.util.AttributeSet
import android.view.View
import kotlinx.coroutines.*
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.Socket

/**
 * Receives JSON stats heartbeats from the Windows Host (port 7534)
 * and provides a gorgeous floating overlay with real-time metrics.
 *
 * Toggle: 4-finger tap gesture (handled in MirrorActivity)
 */
class DebugOverlayView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        typeface = Typeface.MONOSPACE
        textSize = 38f
        color = Color.WHITE
    }
    private val bgPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.argb(185, 0, 0, 0)
    }
    private val greenPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        typeface = Typeface.MONOSPACE
        textSize = 38f
        color = Color.parseColor("#66FF66")
    }
    private val yellowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        typeface = Typeface.MONOSPACE
        textSize = 38f
        color = Color.parseColor("#FFD700")
    }
    private val rectF = RectF()

    // Stats state
    var fps: Double = 0.0
    var bitrateKbps: Int = 0
    var resolution: String = "--"
    var encoder: String = "--"
    var abrTier: String = "--"
    var encMs: Double = 0.0

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (visibility != VISIBLE) return

        val padding = 32f
        val lineHeight = 52f
        val lines = buildLines()
        val boxW = 580f
        val boxH = padding * 2 + lines.size * lineHeight

        rectF.set(padding, padding, padding + boxW, padding + boxH)
        canvas.drawRoundRect(rectF, 24f, 24f, bgPaint)

        var y = padding * 2 + 8f
        for ((label, value, isGood) in lines) {
            paint.textSize = 32f
            paint.color = Color.parseColor("#AAAAAA")
            canvas.drawText(label, padding * 2, y, paint)

            val valPaint = if (isGood) greenPaint else yellowPaint
            valPaint.textSize = 36f
            canvas.drawText(value, padding * 2 + 250f, y, valPaint)
            y += lineHeight
        }
    }

    private data class StatLine(val label: String, val value: String, val isGood: Boolean = true)

    private fun buildLines(): List<StatLine> = listOf(
        StatLine("📊 TAB MIRROR", "DEBUG", true),
        StatLine("", "", true),
        StatLine("FPS", "${fps.toInt()}", fps >= 55),
        StatLine("Bitrate", "${bitrateKbps / 1000} Mbps", bitrateKbps >= 5000),
        StatLine("Enc Time", "${String.format("%.1f", encMs)} ms", encMs < 5.0),
        StatLine("Resolution", resolution, true),
        StatLine("Encoder", encoder.replace("h264_", "").uppercase(), true),
        StatLine("ABR Tier", abrTier, abrTier == "High")
    )

    fun update(json: JSONObject) {
        fps = json.optDouble("fps", fps)
        bitrateKbps = json.optInt("bitrate_kbps", bitrateKbps)
        resolution = json.optString("resolution", resolution)
        encoder = json.optString("encoder", encoder)
        abrTier = json.optString("abr_tier", abrTier)
        encMs = json.optDouble("enc_ms", encMs)
        postInvalidate()
    }
}

/**
 * Connects to the stats port and pumps JSON into the overlay view.
 */
class StatsReceiver(
    private val host: String,
    private val statsPort: Int = 7534,
    private val overlay: DebugOverlayView
) {
    private var job: Job? = null

    fun start(scope: CoroutineScope) {
        job = scope.launch(Dispatchers.IO) {
            while (isActive) {
                try {
                    Socket(host, statsPort).use { socket ->
                        socket.tcpNoDelay = true
                        val reader = BufferedReader(InputStreamReader(socket.getInputStream()))
                        var line: String?
                        while (reader.readLine().also { line = it } != null && isActive) {
                            try {
                                val json = JSONObject(line!!)
                                withContext(Dispatchers.Main) { overlay.update(json) }
                            } catch (_: Exception) {}
                        }
                    }
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    android.util.Log.w("StatsReceiver", "Stats socket error: ${e.message}")
                    delay(3000)
                }
            }
        }
    }

    fun stop() { job?.cancel() }
}
