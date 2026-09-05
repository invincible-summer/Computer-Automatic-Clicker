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
import com.macroclicker.mobile.model.MacroEvent
import com.macroclicker.mobile.overlay.FloatingBall
import com.macroclicker.mobile.record.GestureRecorder
import com.macroclicker.mobile.store.MacroStore
import java.util.concurrent.CopyOnWriteArraySet
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

/**
 * 无障碍服务：宿主 + 回放引擎 + 录制引擎。
 *
 * 线程模型（修复旧版卡死）：
 * - 回放在独立线程，按事件间隔循环；
 * - dispatchGesture 一律投递到主线程执行，回调计数闩在回放线程等待，
 *   绝不阻塞主线程；
 * - 悬浮窗/录制层的增删只发生在主线程。
 */
class MacroService : AccessibilityService() {

    companion object {
        private const val MAX_STROKE_MS = 60_000

        @Volatile
        var instance: MacroService? = null
            private set

        val isReady: Boolean get() = instance != null

        @Volatile
        var isPlaying = false
            private set

        val isRecording: Boolean get() = instance?.recorder?.isActive == true

        fun stopAll() {
            instance?.let {
                it.stopPlayback()
                it.stopRecording(save = false)
            }
        }

        // ---------------- 状态广播（主线程回调） ----------------

        private val listeners = CopyOnWriteArraySet<() -> Unit>()
        private val mainHandler = Handler(Looper.getMainLooper())

        fun addStateListener(l: () -> Unit) { listeners.add(l) }

        fun removeStateListener(l: () -> Unit) { listeners.remove(l) }

        fun notifyState() = mainHandler.post { listeners.forEach { runCatching(it) } }
    }

    private val mainHandler = Handler(Looper.getMainLooper())
    private var ball: FloatingBall? = null

    var recorder: GestureRecorder? = null
        private set

    private var playThread: Thread? = null

    @Volatile
    private var playStop = false

    @Volatile
    private var playPaused = false

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        ball = FloatingBall(this).also { it.show() }
        notifyState()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit

    override fun onInterrupt() = Unit

    override fun onUnbind(intent: Intent?): Boolean {
        release()
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        release()
        super.onDestroy()
    }

    private fun release() {
        stopPlayback()
        recorder?.stop(save = false)
        val b = ball
        mainHandler.post { b?.remove() }
        ball = null
        if (instance === this) instance = null
    }

    // ---------------- 手势构建与注入 ----------------

    /** 事件 → 手势；WAIT 返回 null。时长统一钳制到单笔画上限 60s。 */
    fun buildGesture(ev: MacroEvent): GestureDescription? {
        val duration = when (ev.type) {
            EventType.SWIPE -> ev.duration.coerceIn(50, MAX_STROKE_MS)
            else -> 60
        }
        val path = Path()
        when (ev.type) {
            EventType.TAP -> path.moveTo(ev.x.toFloat(), ev.y.toFloat())
            EventType.SWIPE -> {
                path.moveTo(ev.x.toFloat(), ev.y.toFloat())
                path.lineTo(ev.x2.toFloat(), ev.y2.toFloat())
            }
            EventType.WAIT -> return null
        }
        return GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, duration.toLong()))
            .build()
    }

    private fun evTimeoutMs(ev: MacroEvent): Long = when (ev.type) {
        EventType.SWIPE -> ev.duration.coerceIn(50, MAX_STROKE_MS).toLong() + 2000
        else -> 3000L
    }

    /** 异步注入：结果回调（主线程）带超时保底，防止系统吞掉回调。 */
    fun dispatchAsync(gesture: GestureDescription, timeoutMs: Long, onDone: (Boolean) -> Unit) {
        val done = AtomicBoolean(false)
        val finish: (Boolean) -> Unit = { ok ->
            if (done.compareAndSet(false, true)) {
                mainHandler.post { onDone(ok) }
            }
        }
        mainHandler.post {
            try {
                dispatchGesture(gesture, object : GestureResultCallback() {
                    override fun onCompleted(gestureDescription: GestureDescription?) = finish(true)
                    override fun onCancelled(gestureDescription: GestureDescription?) = finish(false)
                }, null)
            } catch (_: Exception) {
                finish(false)
            }
        }
        mainHandler.postDelayed({ finish(false) }, timeoutMs + 1500)
    }

    /** 阻塞式注入（仅回放线程调用）。 */
    private fun dispatchSync(gesture: GestureDescription, timeoutMs: Long): Boolean {
        val latch = CountDownLatch(1)
        val ok = AtomicBoolean(false)
        dispatchAsync(gesture, timeoutMs) { r -> ok.set(r); latch.countDown() }
        latch.await(timeoutMs + 2000, TimeUnit.MILLISECONDS)
        return ok.get()
    }

    // ---------------- 回放引擎 ----------------

    fun startPlayback(config: MacroConfig) {
        if (isPlaying || isRecording) return
        if (config.events.isEmpty()) {
            ball?.setStatus(getString(R.string.toast_no_events))
            return
        }
        isPlaying = true
        playStop = false
        playPaused = false
        ball?.onPlayStateChanged(true)
        notifyState()

        val settings = config.settings.copy()
        val events = config.events.toList()
        playThread = thread(name = "macro-player") {
            try {
                for (i in settings.countdown downTo 1) {
                    if (playStop) return@thread
                    ball?.setStatus("$i 秒后开始…")
                    sleepInterruptible(1000)
                }
                var round = 0
                while (!playStop) {
                    round++
                    ball?.setStatus(if (settings.loopMode == 0) "执行中…" else "第 $round 轮执行中…")
                    for (ev in events) {
                        if (playStop) return@thread
                        if (ev.delay > 0) sleepInterruptible((ev.delay * 1000).toLong())
                        if (playStop) return@thread
                        buildGesture(ev)?.let { dispatchSync(it, evTimeoutMs(ev)) }
                    }
                    when {
                        settings.loopMode == 0 -> break
                        settings.loopMode == 1 && round >= settings.loopCount -> break
                    }
                    if (settings.loopInterval > 0) sleepInterruptible((settings.loopInterval * 1000).toLong())
                }
                ball?.setStatus(if (playStop) "已停止" else "执行完成 ✔")
            } finally {
                isPlaying = false
                ball?.onPlayStateChanged(false)
                notifyState()
            }
        }
    }

    fun stopPlayback() {
        playStop = true
        playPaused = false
        playThread?.interrupt()
    }

    fun pausePlayback() {
        if (isPlaying) playPaused = true
    }

    fun resumePlayback() {
        playPaused = false
    }

    /** 可中断休眠：随时响应停止与暂停（暂停期间不计时）。 */
    private fun sleepInterruptible(ms: Long) {
        var remaining = ms
        while (remaining > 0 && !playStop) {
            if (playPaused) {
                Thread.sleep(120)
                continue
            }
            val step = minOf(50, remaining)
            try {
                Thread.sleep(step)
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
                return
            }
            remaining -= step
        }
    }

    // ---------------- 录制引擎 ----------------

    fun startRecording(liveReplay: Boolean): Boolean {
        if (isPlaying || isRecording) return false
        val r = GestureRecorder(
            service = this,
            liveReplay = liveReplay,
            onCountChanged = { count ->
                mainHandler.post { ball?.setStatus(getString(R.string.rec_status, count)) }
            },
            onFinished = { events -> handleRecordingFinished(events) }
        )
        recorder = r
        r.start()
        ball?.onRecordingChanged(true)
        notifyState()
        return true
    }

    fun stopRecording(save: Boolean) {
        recorder?.stop(save)
    }

    private fun handleRecordingFinished(events: List<MacroEvent>) {
        recorder = null
        if (events.isEmpty()) {
            ball?.setStatus(getString(R.string.panel_status_idle))
            Toast.makeText(this, R.string.toast_record_empty, Toast.LENGTH_SHORT).show()
        } else {
            val cfg = MacroStore.loadCurrent(this)
            cfg.events = events.toMutableList()
            MacroStore.save(this, cfg)
            Toast.makeText(this, getString(R.string.toast_record_done, events.size), Toast.LENGTH_SHORT).show()
        }
        ball?.onRecordingChanged(false)
        notifyState()
    }
}
