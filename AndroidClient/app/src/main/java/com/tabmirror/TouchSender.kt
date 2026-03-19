package com.tabmirror

import android.view.GestureDetector
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.View
import kotlinx.coroutines.*
import java.io.DataOutputStream
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.LinkedBlockingQueue

/**
 * Captures touch/gesture events from a View and sends them to the Windows host
 * over a separate TCP control connection.
 *
 * Packet format (matches WindowsHost/Input/InputInjector.cs):
 *   Outer wrapper: [type=0x02 1B][length 4B]
 *   Inner payload: [action 1B][normX float32][normY float32]{[scroll delta float32]}
 *
 * Actions:
 *   0 = move, 1 = finger-down (left click), 2 = finger-up
 *   3 = scroll (two-finger), 4 = right-click-down (long press), 5 = right-click-up
 *
 * Message Types:
 *   0x02 = touch/gesture, 0x03 = clipboard, 0x04 = keyboard
 */
class TouchSender(
    private val host: String,
    private val controlPort: Int = 7532,
    private val onClipboardReceived: (String) -> Unit = {}
) {
    private val sendQueue = LinkedBlockingQueue<Pair<Byte, ByteArray>>(128)
    private var senderJob: Job? = null
    private var receiverJob: Job? = null
    private var socket: Socket? = null
    private var dos: DataOutputStream? = null
    private var dis: java.io.DataInputStream? = null

    // Gesture detector for long-press (right click)
    private var gestureDetector: GestureDetector? = null
    private var isLongPressing = false

    fun start(scope: CoroutineScope) {
        senderJob = scope.launch(Dispatchers.IO) {
            while (isActive) {
                try {
                    socket = Socket(host, controlPort).apply { tcpNoDelay = true }
                    dos = DataOutputStream(socket!!.getOutputStream())
                    dis = java.io.DataInputStream(socket!!.getInputStream())
                    android.util.Log.i("TouchSender", "Control connected to $host:$controlPort")

                    // Start receiver job
                    receiverJob = launch(Dispatchers.IO) {
                        try {
                            while (isActive) {
                                val msgType = dis?.readByte() ?: break
                                val length = dis?.readInt() ?: break
                                val payload = ByteArray(length)
                                dis?.readFully(payload)

                                if (msgType.toInt() == 0x03) {
                                    val text = String(payload, Charsets.UTF_8)
                                    withContext(Dispatchers.Main) { onClipboardReceived(text) }
                                }
                            }
                        } catch (e: Exception) {
                            android.util.Log.w("TouchSender", "Receiver error: ${e.message}")
                        }
                    }

                    while (isActive) {
                        val (msgType, payload) = sendQueue.poll(100, java.util.concurrent.TimeUnit.MILLISECONDS)
                            ?: continue
                        dos?.apply {
                            writeByte(msgType.toInt())         // msg type: 0x02, 0x03, 0x04
                            writeInt(payload.size)      // length
                            write(payload)              // payload
                            flush()
                        }
                    }
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    android.util.Log.w("TouchSender", "Control socket error: ${e.message}")
                    delay(2000)
                } finally {
                    receiverJob?.cancel()
                    dos?.runCatching { close() }
                    dis?.runCatching { close() }
                    socket?.runCatching { close() }
                }
            }
        }
    }

    /**
     * Initialise the long-press gesture detector. Must be called before handleEvent.
     * Kept separate so that MirrorActivity owns the single setOnTouchListener.
     */
    fun setupGestureDetector(context: android.content.Context, view: View) {
        gestureDetector = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
            override fun onLongPress(e: MotionEvent) {
                isLongPressing = true
                enqueue(0x02, buildPacket(4, e, e.x / view.width, e.y / view.height))
            }
        })
    }

    /**
     * Process a single MotionEvent from the owner view's touch listener.
     * Returns true to indicate the event was consumed.
     */
    fun handleEvent(v: View, event: MotionEvent): Boolean {
        gestureDetector?.onTouchEvent(event)

        val nx = event.x / v.width
        val ny = event.y / v.height

        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                isLongPressing = false
                enqueue(0x02, buildPacket(1, event, nx, ny))
            }
            MotionEvent.ACTION_MOVE, MotionEvent.ACTION_HOVER_MOVE -> {
                enqueue(0x02, buildPacket(0, event, nx, ny))
            }
            MotionEvent.ACTION_UP -> {
                if (isLongPressing) {
                    enqueue(0x02, buildPacket(5, event, nx, ny))
                    isLongPressing = false
                } else {
                    enqueue(0x02, buildPacket(2, event, nx, ny))
                }
            }
            MotionEvent.ACTION_POINTER_DOWN -> { /* handled as scroll below */ }
        }

        // Two-finger scroll
        if (event.pointerCount == 2 && event.actionMasked == MotionEvent.ACTION_MOVE) {
            val cy = event.getY(0)
            enqueue(0x02, buildScrollPacket(nx, ny, (cy - event.getY(1)) / v.height))
        }

        return true
    }

    /**
     * Legacy convenience — attaches a touch listener directly; ONLY use when no other
     * component needs the view's touch events (e.g. testing).
     */
    fun attachToView(view: View, context: android.content.Context) {
        setupGestureDetector(context, view)
        view.setOnTouchListener { v, event -> handleEvent(v, event) }
    }

    // Buffer reuse for move/hover events to reduce GC pressure
    private val moveBuffer = ByteBuffer.allocate(22).order(ByteOrder.LITTLE_ENDIAN)

    private fun buildPacket(action: Int, event: MotionEvent, nx: Float, ny: Float): ByteArray {
        synchronized(moveBuffer) {
            moveBuffer.clear()
            moveBuffer.put(action.toByte())
            
            val type = event.getToolType(0)
            val outType = when (type) {
                MotionEvent.TOOL_TYPE_STYLUS -> 2.toByte()
                MotionEvent.TOOL_TYPE_ERASER -> 3.toByte()
                MotionEvent.TOOL_TYPE_MOUSE -> 4.toByte()
                else -> 1.toByte()
            }
            moveBuffer.put(outType)
            moveBuffer.putFloat(nx)
            moveBuffer.putFloat(ny)
            moveBuffer.putFloat(event.pressure)
            
            val tilt = event.getAxisValue(MotionEvent.AXIS_TILT).toDouble()
            val orientation = event.getAxisValue(MotionEvent.AXIS_ORIENTATION).toDouble()
            val tx = (Math.sin(orientation) * Math.sin(tilt)).toFloat()
            val ty = (-Math.cos(orientation) * Math.sin(tilt)).toFloat()
            moveBuffer.putFloat(tx)
            moveBuffer.putFloat(ty)
            
            return moveBuffer.array().copyOf()
        }
    }

    private fun buildScrollPacket(nx: Float, ny: Float, delta: Float): ByteArray =
        ByteBuffer.allocate(13).order(ByteOrder.LITTLE_ENDIAN).apply {
            put(3.toByte())   // action = scroll
            putFloat(nx)
            putFloat(ny)
            putFloat(delta)
        }.array()

    fun sendClipboard(text: String) {
        val payload = text.toByteArray(Charsets.UTF_8)
        enqueue(0x03, payload)
    }

    /**
     * Send keyboard event to PC.
     * Format: [1B action: 0=down, 1=up][4B vkey int32][4B unicode int32]
     */
    fun sendKeyboard(action: Int, vkey: Int, unicode: Int) {
        val payload = ByteBuffer.allocate(9).order(ByteOrder.LITTLE_ENDIAN).apply {
            put(action.toByte())
            putInt(vkey)
            putInt(unicode)
        }.array()
        enqueue(0x04, payload)
    }

    private fun enqueue(type: Int, payload: ByteArray) {
        // If queue is nearly full, drop oldest move/hover packets to keep latency low
        if (sendQueue.size > 64 && (payload[0].toInt() == 0 || payload[0].toInt() == 6)) {
            sendQueue.poll() 
        }
        sendQueue.offer(type.toByte() to payload) 
    }

    fun stop() {
        senderJob?.cancel()
        receiverJob?.cancel()
        dos?.runCatching { close() }
        dis?.runCatching { close() }
        socket?.runCatching { close() }
    }
}
