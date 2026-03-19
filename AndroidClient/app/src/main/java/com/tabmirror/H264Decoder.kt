package com.tabmirror

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import java.nio.ByteBuffer

/**
 * H.264 hardware decoder using Android's MediaCodec API.
 *
 * Decoded frames are rendered directly to the provided [Surface] (e.g. from a SurfaceView or
 * TextureView) — the zero-copy path: decoder → GPU compositor, no CPU involvement.
 */
class H264Decoder(private val surface: Surface) {

    private var codec: MediaCodec? = null
    private var isStarted = false
    private val bufferInfo = MediaCodec.BufferInfo()

    /**
     * Must be called after receiving the first SPS/PPS NAL units so MediaCodec
     * knows the resolution. Can also be called speculatively with the expected size.
     */
    fun configure(width: Int, height: Int) {
        codec?.run { stop(); release() }

        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 1 shl 20) // 1 MB input buffer
            // Request low-latency decoding (Android 10+)
            if (android.os.Build.VERSION.SDK_INT >= 30) {
                setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }
        }

        codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).also { c ->
            // Configure with Surface = direct GPU render, no CPU copy
            c.configure(format, surface, null, 0)
            c.start()
        }
        isStarted = true
    }

    /**
     * Feed a raw NAL unit (Annex-B or length-prefixed) into the decoder.
     * Decoded frames are automatically rendered to the Surface.
     *
     * @param nalData    Raw bytes of one NAL unit (with start code 0x00 0x00 0x00 0x01)
     * @param presentationUs Presentation timestamp in microseconds
     */
    fun feedNalUnit(nalData: ByteArray, presentationUs: Long) {
        val c = codec ?: return

        // First, drain any already decoded frames to make room and reduce lag
        drainOutputToSurface(c)

        try {
            // Use a short timeout instead of infinite (-1L) to avoid blocking the network thread
            val inputIndex = c.dequeueInputBuffer(1000L) // 1ms timeout
            if (inputIndex >= 0) {
                val inputBuffer: ByteBuffer = c.getInputBuffer(inputIndex)!!
                inputBuffer.clear()
                inputBuffer.put(nalData)
                c.queueInputBuffer(inputIndex, 0, nalData.size, presentationUs, 0)
            }
        } catch (e: Exception) {
            android.util.Log.e("H264Decoder", "Error feeding NAL unit, resetting...", e)
            codec?.run { try { stop(); start() } catch (ex: Exception) { } }
        }

        // Drain again after feeding to render the results immediately
        drainOutputToSurface(c)
    }

    private fun drainOutputToSurface(c: MediaCodec) {
        while (true) {
            val outputIndex = c.dequeueOutputBuffer(bufferInfo, 0)
            when {
                outputIndex >= 0 -> {
                    // render=true pushes the frame directly to the Surface (GPU path)
                    c.releaseOutputBuffer(outputIndex, true)
                }
                outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    // Resolution/codec change — log but keep going
                    val newFormat = c.outputFormat
                    android.util.Log.i("H264Decoder", "Output format changed: $newFormat")
                }
                else -> break // No more output right now
            }
        }
    }

    /** Signal end-of-stream and flush remaining decoded frames. */
    fun flush() {
        codec?.flush()
    }

    fun release() {
        if (!isStarted) return
        codec?.stop()
        codec?.release()
        codec = null
        isStarted = false
    }
}
