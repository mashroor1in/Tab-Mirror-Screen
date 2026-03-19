package com.tabmirror

import android.view.MotionEvent
import android.view.View
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Detects multi-finger gesture macros on the streaming surface and
 * sends corresponding keyboard shortcut commands to the Windows Host.
 *
 * Gesture → Windows shortcut mapping (user-configurable in Phase 4D):
 *   3-finger swipe UP    → Win+Tab        (Task View)
 *   3-finger swipe DOWN  → Win+D         (Show Desktop)
 *   3-finger swipe LEFT  → Ctrl+Win+Left  (Previous Virtual Desktop)
 *   3-finger swipe RIGHT → Ctrl+Win+Right (Next Virtual Desktop)
 *   Left-edge swipe      → Alt+F4        (Close Window)
 *   Double-tap (stylus)  → Ctrl+Z        (Undo)
 *
 * Macro packet format (Control Port 7532):
 *   Byte 0:  type = 0x10 (gesture macro)
 *   Byte 1:  macro ID (see constants below)
 */
class GestureMacro(
    private val host: String,
    private val controlPort: Int = 7532,
    private val scope: CoroutineScope
) {

    companion object {
        const val MACRO_TASK_VIEW    : Byte = 0x01
        const val MACRO_SHOW_DESKTOP : Byte = 0x02
        const val MACRO_PREV_DESKTOP : Byte = 0x03
        const val MACRO_NEXT_DESKTOP : Byte = 0x04
        const val MACRO_CLOSE_WINDOW : Byte = 0x05
        const val MACRO_UNDO         : Byte = 0x06
    }

    // Track swipe start positions
    private val fingerStartY = mutableMapOf<Int, Float>()
    private val fingerStartX = mutableMapOf<Int, Float>()

    fun attachToView(view: View) {
        // attachToView is now a no-op: MirrorActivity owns the single touch listener.
        // Call handleEvent(v, event) from the unified listener instead.
    }

    /**
     * Process a motion event — call this from MirrorActivity's single setOnTouchListener.
     * Returns false so the event continues to other handlers.
     */
    fun handleEvent(v: View, event: MotionEvent): Boolean {
        var lastTapTime = 0L

        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                val idx = event.actionIndex
                fingerStartY[event.getPointerId(idx)] = event.getY(idx)
                fingerStartX[event.getPointerId(idx)] = event.getX(idx)

                // Double-tap detection for stylus undo
                if (event.getToolType(idx) == MotionEvent.TOOL_TYPE_STYLUS) {
                    val now = System.currentTimeMillis()
                    if (now - lastTapTime < 300) sendMacro(MACRO_UNDO)
                    lastTapTime = now
                }
            }

            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                if (event.pointerCount == 3) {
                    val idx = event.actionIndex
                    val id = event.getPointerId(idx)
                    val startY = fingerStartY[id] ?: return false
                    val startX = fingerStartX[id] ?: return false
                    val dy = event.getY(idx) - startY
                    val dx = event.getX(idx) - startX
                    val threshold = v.height * 0.07f

                    if (Math.abs(dy) > Math.abs(dx)) {
                        if (dy < -threshold) sendMacro(MACRO_TASK_VIEW)
                        else if (dy > threshold) sendMacro(MACRO_SHOW_DESKTOP)
                    } else {
                        if (dx < -threshold) sendMacro(MACRO_PREV_DESKTOP)
                        else if (dx > threshold) sendMacro(MACRO_NEXT_DESKTOP)
                    }
                }

                if (event.pointerCount == 1) {
                    val id = event.getPointerId(0)
                    val startX = fingerStartX[id] ?: return false
                    val endX = event.getX(0)
                    if (startX < 30f && endX > v.width * 0.3f)
                        sendMacro(MACRO_CLOSE_WINDOW)
                }

                fingerStartY.remove(event.getPointerId(event.actionIndex))
                fingerStartX.remove(event.getPointerId(event.actionIndex))
            }
        }
        return false // Don't consume — pass event to TouchSender
    }

    private fun sendMacro(macroId: Byte) {
        scope.launch(Dispatchers.IO) {
            try {
                Socket(host, controlPort).use { socket ->
                    socket.tcpNoDelay = true
                    val packet = ByteBuffer.allocate(7).order(ByteOrder.LITTLE_ENDIAN).apply {
                        put(0x02)            // msg type = control
                        putInt(2)            // payload length
                        put(0x10)            // sub-type = gesture macro
                        put(macroId)
                    }.array()
                    socket.getOutputStream().write(packet)
                    socket.getOutputStream().flush()
                }
            } catch (e: Exception) {
                android.util.Log.w("GestureMacro", "Send macro failed: ${e.message}")
            }
        }
    }
}
