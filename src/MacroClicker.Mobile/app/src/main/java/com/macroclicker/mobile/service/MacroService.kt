package com.macroclicker.mobile.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.widget.Toast
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.macroclicker.mobile.R
import com.macroclicker.mobile.inject.Injector
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroConfig
import com.macroclicker.mobile.model.MacroEvent
import com.macroclicker.mobile.overlay.FloatingBall
import com.macroclicker.mobile.record.GestureRecorder
import com.macroclicker.mobile.store.MacroStore
import java.util.concurrent.CopyOnWriteArraySet
import kotlin.concurrent.thread

/**
 * 前台服务（specialUse）：宿主悬浮球 + 回放引擎 + 录制引擎。
 *
 * 不使用无障碍服务——手势注入经 Shizuku（ADB shell uid）完成：
 * 快速路径（InputManager.injectInputEvent，毫秒级）失败自动回落
 * 固定 argv 的 /system/bin/input。回放线程同步等待注入结果，失败即停。
 *
 * 生命周期：任何录制/执行开始时自动拉起；「悬浮球常驻」开关（设置页）为 on
 * 时服务保留，否则会话结束即自毁。通知提供「停止一切 / 退出」操作。
 */
class MacroService : Service() {

    companion object {
        const val ACTION_STOP_ALL = "com.macroclicker.mobile.STOP_ALL"
        const val ACTION_QUIT = "com.macroclicker.mobile.QUIT"

        private const val CHANNEL_ID = "macro_session"
        private const val NOTIF_ID = 100

        @Volatile
        var instance: MacroService? = null
            private set

        val isRunning: Boolean get() = instance != null

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

        /** 前台（Activity 内）调用，确保服务已启动。 */
        fun ensureStarted(context: Context) {
            if (instance == null) {
                ContextCompat.startForegroundService(
                    context, Intent(context, MacroService::class.java))
            }
        }

        fun stopIfIdle(context: Context) {
            val it = instance ?: return
            if (!isPlaying && !isRecording && !MacroStore.ballEnabled(context)) {
                context.stopService(Intent(context, MacroService::class.java))
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
    private val notifManager by lazy { getSystemService(NOTIFICATION_SERVICE) as NotificationManager }

    var recorder: GestureRecorder? = null
        private set

    private var playThread: Thread? = null

    @Volatile
    private var playStop = false

    /** Shizuku 断开（状态离开 READY）时立即停止回放，绝不盲点续跑。 */
    private val injectorListener: (Injector.State) -> Unit = { s ->
        if (s != Injector.State.READY && isPlaying) {
            stopPlayback()
            status(getString(R.string.play_inject_fail))
        }
    }

    // ---------------- 生命周期 ----------------

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        createChannel()
        startAsForeground()
        Injector.bind()
        Injector.addStateListener(injectorListener)
        ball = FloatingBall(this).also { it.show() }
        notifyState()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP_ALL -> stopAll()
            ACTION_QUIT -> {
                stopAll()
                stopSelf()
            }
        }
        return START_NOT_STICKY
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
        Injector.removeStateListener(injectorListener)
        Injector.unbind()
        if (instance === this) instance = null
        notifyState()
    }

    private fun createChannel() {
        val ch = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.notif_channel),
            NotificationManager.IMPORTANCE_LOW
        ).apply { setShowBadge(false) }
        notifManager.createNotificationChannel(ch)
    }

    private fun startAsForeground() {
        val n = buildNotification(getString(R.string.notif_ready))
        if (Build.VERSION.SDK_INT >= 34) {
            ServiceCompat.startForeground(
                this, NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIF_ID, n)
        }
    }

    private fun buildNotification(text: String): Notification {
        val stopPi = PendingIntent.getService(
            this, 1,
            Intent(this, MacroService::class.java).setAction(ACTION_STOP_ALL),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val quitPi = PendingIntent.getService(
            this, 2,
            Intent(this, MacroService::class.java).setAction(ACTION_QUIT),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val builder = Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notif)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setOngoing(true)
            .setContentIntent(launchPi())
            .addAction(0, getString(R.string.notif_stop), stopPi)
            .addAction(0, getString(R.string.notif_quit), quitPi)
        return builder.build()
    }

    private fun launchPi(): PendingIntent = PendingIntent.getActivity(
        this, 3,
        packageManager.getLaunchIntentForPackage(packageName),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
    )

    /** 任意线程可调：更新通知与悬浮球状态行。 */
    private fun status(text: String) {
        ball?.setStatus(text)
        runCatching { notifManager.notify(NOTIF_ID, buildNotification(text)) }
    }

    // ---------------- 回放引擎 ----------------

    fun startPlayback(config: MacroConfig): Boolean {
        if (isPlaying || isRecording) return false
        if (config.events.isEmpty()) {
            ball?.setStatus(getString(R.string.toast_no_events))
            return false
        }
        Injector.bind()
        if (Injector.state != Injector.State.READY) {
            ball?.setStatus(getString(R.string.shell_not_ready_short))
            Toast.makeText(this, R.string.shell_not_ready_short, Toast.LENGTH_SHORT).show()
            return false
        }
        isPlaying = true
        playStop = false
        ball?.onPlayStateChanged(true)
        notifyState()

        val settings = config.settings.copy()
        val events = config.events.toList()
        playThread = thread(name = "macro-player") {
            try {
                for (i in settings.countdown downTo 1) {
                    if (playStop) return@thread
                    status(getString(R.string.play_countdown, i))
                    sleepInterruptible(1000)
                }
                var round = 0
                while (!playStop) {
                    round++
                    status(if (settings.loopMode == 0)
                        getString(R.string.play_status_once)
                    else getString(R.string.play_status_round, round))
                    for (ev in events) {
                        if (playStop) return@thread
                        if (ev.delay > 0) sleepInterruptible((ev.delay * 1000).toLong())
                        if (playStop) return@thread
                        if (!inject(ev)) {
                            status(getString(R.string.play_inject_fail))
                            return@thread
                        }
                    }
                    when {
                        settings.loopMode == 0 -> break
                        settings.loopMode == 1 && round >= settings.loopCount -> break
                    }
                    if (settings.loopInterval > 0)
                        sleepInterruptible((settings.loopInterval * 1000).toLong())
                }
                status(if (playStop) getString(R.string.play_stopped)
                else getString(R.string.play_finished))
            } finally {
                isPlaying = false
                mainHandler.post {
                    ball?.onPlayStateChanged(false)
                    notifyState()
                    stopIfIdle(this)
                }
            }
        }
        return true
    }

    /** 同步注入单个事件（回放线程）；WAIT 直接成功。 */
    private fun inject(ev: MacroEvent): Boolean = when (ev.type) {
        EventType.TAP -> Injector.tap(ev.x, ev.y)
        EventType.SWIPE -> Injector.swipe(
            ev.x, ev.y, ev.x2, ev.y2, ev.duration.coerceIn(50, 60_000))
        EventType.WAIT -> true
    }

    fun stopPlayback() {
        playStop = true
        playThread?.interrupt()
    }

    /** 可中断休眠：随时响应停止。 */
    private fun sleepInterruptible(ms: Long) {
        var remaining = ms
        while (remaining > 0 && !playStop) {
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
        // 引擎未就绪时降级为纯标记录制（只记录、不作用于界面）
        val withLive = liveReplay && Injector.state == Injector.State.READY
        if (liveReplay && !withLive) {
            Toast.makeText(this, R.string.rec_live_fallback, Toast.LENGTH_SHORT).show()
        }
        val r = GestureRecorder(
            service = this,
            liveReplay = withLive,
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
        stopIfIdle(this)
    }
}
