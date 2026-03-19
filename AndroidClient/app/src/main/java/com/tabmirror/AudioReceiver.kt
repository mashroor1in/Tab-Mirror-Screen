package com.tabmirror

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import kotlinx.coroutines.*
import java.io.InputStream
import java.net.Socket

class AudioReceiver(
    private val host: String,
    private val audioPort: Int = 7533
) {
    private var job: Job? = null
    private var socket: Socket? = null
    private var audioTrack: AudioTrack? = null

    fun start(scope: CoroutineScope) {
        job = scope.launch(Dispatchers.IO) {
            while (isActive) {
                try {
                    socket = Socket(host, audioPort).apply { tcpNoDelay = true }
                    android.util.Log.i("AudioReceiver", "Audio connected to $host:$audioPort")

                    val minBufSize = AudioTrack.getMinBufferSize(
                        48000,
                        AudioFormat.CHANNEL_OUT_STEREO,
                        AudioFormat.ENCODING_PCM_16BIT
                    )

                    // Provide a generous buffer to prevent underruns but small enough for low latency
                    audioTrack = AudioTrack.Builder()
                        .setAudioAttributes(
                            AudioAttributes.Builder()
                                .setUsage(AudioAttributes.USAGE_MEDIA)
                                .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                                .build()
                        )
                        .setAudioFormat(
                            AudioFormat.Builder()
                                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                                .setSampleRate(48000)
                                .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                                .build()
                        )
                        .setBufferSizeInBytes(minBufSize * 2)
                        .setTransferMode(AudioTrack.MODE_STREAM)
                        .build()

                    audioTrack?.play()

                    val ips: InputStream = socket!!.getInputStream()
                    val buffer = ByteArray(minBufSize) // Read in small chunks

                    while (isActive) {
                        val read = ips.read(buffer)
                        if (read == -1) break
                        if (read > 0) {
                            audioTrack?.write(buffer, 0, read)
                        }
                    }
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    android.util.Log.w("AudioReceiver", "Audio socket error: ${e.message}")
                    delay(2000)
                } finally {
                    audioTrack?.stop()
                    audioTrack?.release()
                    audioTrack = null
                    socket?.runCatching { close() }
                }
            }
        }
    }

    fun stop() {
        job?.cancel()
        audioTrack?.runCatching { stop() }
        audioTrack?.runCatching { release() }
        socket?.runCatching { close() }
    }
}
