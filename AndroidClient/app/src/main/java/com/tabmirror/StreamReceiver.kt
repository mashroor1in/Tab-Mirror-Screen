package com.tabmirror

import kotlinx.coroutines.*
import java.io.DataInputStream
import java.net.Socket
import java.net.SocketException

/**
 * Connects to the Windows host over TCP and continuously reads H.264 NAL units,
 * feeding them into the [H264Decoder].
 *
 * Wire format (matches WindowsHost/Network/StreamServer.cs):
 *   [1 byte:  msg type]   0x01=video, 0x02=touch
 *   [4 bytes: length, big-endian]
 *   [N bytes: payload]
 */
class StreamReceiver(
    private val host: String,
    private val videoPort: Int = 7531,
    private val decoder: H264Decoder,
    private val onConnected: () -> Unit = {},
    private val onDisconnected: (String) -> Unit = {}
) {
    private var job: Job? = null
    private var socket: Socket? = null

    // Resolution of the stream: set from first IDR frame or a config message
    var streamWidth  = 1920
    var streamHeight = 1080

    fun start(scope: CoroutineScope) {
        job = scope.launch(Dispatchers.IO) {
            while (isActive) {
                try {
                    android.util.Log.i("StreamReceiver", "Connecting to $host:$videoPort...")
                    socket = Socket(host, videoPort).apply {
                        tcpNoDelay = true
                        // Use a smaller buffer to avoid stale frame pile-up in the OS
                        receiveBufferSize = 64 * 1024
                    }
                    val dis = DataInputStream(socket!!.getInputStream())

                    // Initialise decoder with expected resolution
                    decoder.configure(streamWidth, streamHeight)

                    withContext(Dispatchers.Main) { onConnected() }
                    android.util.Log.i("StreamReceiver", "Connected!")

                    var pts = 0L
                    while (isActive) {
                        // Read message header: [type 1B][length 4B]
                        val msgType = dis.readByte()
                        val length  = dis.readInt()

                        val payload = ByteArray(length)
                        dis.readFully(payload)
                        when (msgType.toInt()) {
                            0x01 -> {
                                // H.264 NAL unit
                                // Use current system time (µs) to ensure low-latency rendering
                                val nowUs = System.nanoTime() / 1000L
                                decoder.feedNalUnit(payload, nowUs)
                            }
                            0x05 -> {
                                // Resolution Sync: Payload is [W:4B][H:4B]
                                if (payload.size >= 8) {
                                    val w = ((payload[0].toInt() and 0xFF) shl 24) or
                                            ((payload[1].toInt() and 0xFF) shl 16) or
                                            ((payload[2].toInt() and 0xFF) shl 8) or
                                            (payload[3].toInt() and 0xFF)
                                    val h = ((payload[4].toInt() and 0xFF) shl 24) or
                                            ((payload[5].toInt() and 0xFF) shl 16) or
                                            ((payload[6].toInt() and 0xFF) shl 8) or
                                            (payload[7].toInt() and 0xFF)
                                    
                                    android.util.Log.i("StreamReceiver", "Resolution Sync: ${w}x${h}")
                                    streamWidth = w
                                    streamHeight = h
                                    decoder.configure(w, h)
                                }
                            }
                            else -> {
                                android.util.Log.w("StreamReceiver", "Unknown msg type: $msgType")
                            }
                        }
                    }
                } catch (e: SocketException) {
                    android.util.Log.w("StreamReceiver", "Socket error: ${e.message}")
                    withContext(Dispatchers.Main) { onDisconnected(e.message ?: "Connection lost") }
                    delay(2_000) // Retry after 2 seconds
                } catch (e: CancellationException) {
                    throw e // Let coroutine cancellation propagate
                } catch (e: Exception) {
                    android.util.Log.e("StreamReceiver", "Unexpected error", e)
                    withContext(Dispatchers.Main) { onDisconnected(e.message ?: "Error") }
                    delay(2_000)
                } finally {
                    socket?.runCatching { close() }
                }
            }
        }
    }

    fun stop() {
        job?.cancel()
        socket?.runCatching { close() }
    }
}
