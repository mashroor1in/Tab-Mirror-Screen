package com.tabmirror

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.util.Log
import kotlinx.coroutines.*

/**
 * Monitors the Android clipboard for changes and notifies the provided listener.
 * Also allows setting the Android clipboard from external data.
 */
class ClipboardSync(
    private val context: Context,
    private val onLocalClipboardChanged: (String) -> Unit
) {
    private val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    private var lastText: String = ""

    private val listener = ClipboardManager.OnPrimaryClipChangedListener {
        val clip = clipboard.primaryClip
        if (clip != null && clip.itemCount > 0) {
            val text = clip.getItemAt(0).text?.toString() ?: ""
            if (text.isNotEmpty() && text != lastText) {
                lastText = text
                Log.i("ClipboardSync", "Local clipboard changed: ${if (text.length > 40) text.take(40) + "..." else text}")
                onLocalClipboardChanged(text)
            }
        }
    }

    fun start() {
        clipboard.addPrimaryClipChangedListener(listener)
    }

    fun stop() {
        clipboard.removePrimaryClipChangedListener(listener)
    }

    fun setRemoteClipboard(text: String) {
        if (text == lastText) return
        lastText = text
        Log.i("ClipboardSync", "Setting remote clipboard: ${if (text.length > 40) text.take(40) + "..." else text}")
        
        // Context.CLIPBOARD_SERVICE must be accessed from the main thread
        MainScope().launch {
            val clip = ClipData.newPlainText("PC Clipboard", text)
            clipboard.setPrimaryClip(clip)
        }
    }
}
