package com.macroclicker.mobile.service

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Intent
import android.graphics.Path
import android.os.Handler
import android.os.Looper
import android.view.accessibility.AccessibilityEvent
import android.widget.Toast
import com.macroclicker.mobile.R
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroConfig
import com.macroclicker.mobile.overlay.FloatingControls
import com.macroclicker.mobile.overlay.PickOverlay
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/**
 * 无障碍服务：通过 dispatchGesture 向其他应用注入点击/滑动手势（回放引擎），
 * 并承载悬浮控制球/面板与屏幕取点浮层。
 */
class ClickService : AccessibilityService() {

    companion object {
        @Volatile
        var instance: ClickService? = null
            private set

        val isReady: Boolean get() = instance != null

        @Volatile
        var isPlaying = false
            private set

        fun stopAll() {
            instance?.stopPlayback()
        }
    }

    val mainHandler = Handler(Looper.getMainLooper())

    private lateinit var floating: FloatingControls
    private var floatingInitialized = false
    private var pickOverlay: PickOverlay? = null
    private var playThread: Thread? = null

    @Volatile private var stopFlag = false
    @Volatile private var paused = false

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        floating = FloatingControls(this)
        floatingInitialized = true
        floating.show()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit

    override fun onInterrupt() = Unit

    override fun onUnbind(intent: Intent?): Boolean {
        if (floatingInitialized) floating.remove()
        pickOverlay?.dismiss()
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        stopPlayback()
        if (floatingInitialized) floating.remove()
        pickOverlay?.dismiss()
        instance = null
        super.onDestroy()
    }

    // ---------------- 手势注入 ----------------

    private fun send(gesture: GestureDescription): Boolean {
        val latch = CountDownLatch(1)
        var ok = false
        dispatchGesture(gesture, object : AccessibilityService.GestureResultCallback() {
            override fun onCompleted(g: GestureDescription?) {
                ok = true
                latch.countDown()
            }

            override fun onCancelled(g: GestureDescription?) {
                latch.countDown()
            }
        }, null)
        latch.await(10, TimeUnit.SECONDS)
        return ok
    }

    fun tap(x: Int, y: Int): Boolean {
        val path = Path().apply { moveTo(x.toFloat(), y.toFloat()) }
        val gesture = GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, 60))
            .build()
        return send(gesture)
    }

    fun swipe(x1: Int, y1: Int, x2: Int, y2: Int, durationMs: Int): Boolean {
        val path = Path().apply {
            moveTo(x1.toFloat(), y1.toFloat())
            lineTo(x2.toFloat(), y2.toFloat())
        }
        val gesture = GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, durationMs.coerceIn(50, 60_000).toLong()))
            .build()
        return send(gesture)
    }

    // ---------------- 回放引擎 ----------------

    fun startPlayback(config: MacroConfig) {
        if (isPlaying) return
        if (config.events.isEmpty()) {
            floating.setStatus("序列为空")
            return
        }
        stopFlag = false
        paused = false
        isPlaying = true
        floating.onPlayStateChanged(true)

        val settings = config.settings.copy()
        val events = config.events.toList()

        playThread = thread(name = "macro-player") {
            try {
                if (settings.countdown > 0) {
                    for (i in settings.countdown downTo 1) {
                        if (stopFlag) return@thread
                        floating.setStatus("倒计时 $i 秒…")
                        sleepInterruptible(1000)
                    }
                }
                var round = 0
                while (!stopFlag) {
                    round++
                    floating.setStatus(if (settings.loopMode == 0) "执行中…" else "第 $round 轮执行中…")
                    for (ev in events) {
                        if (stopFlag) return@thread
                        sleepInterruptible((ev.delay * 1000).toLong())
                        if (stopFlag) return@thread
                        when (ev.type) {
                            EventType.TAP -> tap(ev.x, ev.y)
                            EventType.SWIPE -> swipe(ev.x, ev.y, ev.x2, ev.y2, ev.duration)
                            EventType.WAIT -> Unit
                        }
                    }
                    if (settings.loopMode == 0) break
                    if (settings.loopMode == 1 && round >= settings.loopCount) break
                    sleepInterruptible((settings.loopInterval * 1000).toLong())
                }
                val stopped = stopFlag
                floating.setStatus(if (stopped) "已停止" else "执行完成 ✔")
                mainHandler.post {
                    Toast.makeText(
                        this,
                        if (stopped) R.string.toast_play_stopped else R.string.toast_play_done,
                        Toast.LENGTH_SHORT
                    ).show()
                }
            } finally {
                isPlaying = false
                floating.onPlayStateChanged(false)
            }
        }
    }

    fun stopPlayback() {
        stopFlag = true
        paused = false
        playThread?.interrupt()
    }

    /** 可中断休眠：随时响应停止与暂停。 */
    private fun sleepInterruptible(ms: Long) {
        var remaining = ms
        while (remaining > 0 && !stopFlag) {
            if (paused) {
                try { Thread.sleep(120) } catch (e: InterruptedException) { return }
                continue
            }
            val step = minOf(120, remaining)
            try { Thread.sleep(step) } catch (e: InterruptedException) { return }
            remaining -= step
        }
    }

    // ---------------- 屏幕取点 ----------------

    fun startPick(mode: PickOverlay.Mode) {
        mainHandler.post {
            floating.hide()
            (pickOverlay ?: PickOverlay(this).also { pickOverlay = it }).start(mode)
        }
    }

    fun onPickFinished() {
        mainHandler.post { floating.show() }
    }
}
