package com.macroclicker.mobile.overlay

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.graphics.PixelFormat
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.ContextThemeWrapper
import android.view.Gravity
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.WindowInsets
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.TextView
import android.widget.Toast
import com.macroclicker.mobile.R
import com.macroclicker.mobile.databinding.OverlayPanelBinding
import com.macroclicker.mobile.service.MacroService
import com.macroclicker.mobile.store.MacroStore
import kotlin.math.abs
import kotlin.math.min

/**
 * 悬浮控制球：可拖动、位置按屏幕比例记忆；点按展开控制面板。
 * 录制/执行中点球即停止，无需回到 App。
 *
 * v4：面板由 XML（overlay_panel.xml）以深色 M3 主题 inflate，
 * 矢量图标替代文本符号；拖动/落点边界避让系统导航栏与挖孔。
 */
class FloatingBall(private val service: MacroService) {

    private val wm = service.getSystemService(Context.WINDOW_SERVICE) as WindowManager
    private val handler = Handler(Looper.getMainLooper())
    private val prefs = service.getSharedPreferences("floating", Context.MODE_PRIVATE)

    private var ball: View? = null
    private var ballParams: WindowManager.LayoutParams? = null
    private var ballIcon: ImageView? = null
    private var panel: View? = null
    private var panelParams: WindowManager.LayoutParams? = null
    private var panelStatus: TextView? = null

    private val ballSize get() = dp(56)

    private fun dp(v: Int): Int = (v * service.resources.displayMetrics.density).toInt()
    private fun screenW(): Int = service.resources.displayMetrics.widthPixels
    private fun screenH(): Int = service.resources.displayMetrics.heightPixels

    /** 底部系统栏 + 挖孔避让高度（悬浮窗坐标系按整屏计，需自行减去）。 */
    private fun bottomInset(): Int {
        if (Build.VERSION.SDK_INT >= 30) {
            runCatching {
                return wm.currentWindowMetrics.windowInsets
                    .getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                    .bottom
            }
        }
        val res = service.resources
        val id = res.getIdentifier("navigation_bar_height", "dimen", "android")
        return if (id > 0) res.getDimensionPixelSize(id) else dp(24)
    }

    fun show() = handler.post { addBall() }

    fun remove() = handler.post {
        removePanel()
        removeBall()
    }

    // ---------------- 悬浮球 ----------------

    @SuppressLint("ClickableViewAccessibility")
    private fun addBall() {
        if (ball != null) return
        val icon = ImageView(service).apply {
            setImageResource(R.drawable.ic_ball_play)
            scaleType = ImageView.ScaleType.FIT_CENTER
            val pad = dp(15)
            setPadding(pad, pad, pad, pad)
        }
        ballIcon = icon
        val v = FrameLayout(service).apply {
            setBackgroundResource(R.drawable.bg_ball)
            addView(icon, FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            ))
        }
        val params = WindowManager.LayoutParams(
            ballSize, ballSize,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            val saved = loadPosition()
            x = saved.first.coerceIn(0, (screenW() - ballSize).coerceAtLeast(0))
            y = saved.second.coerceIn(0, (screenH() - ballSize).coerceAtLeast(0))
        }

        var downRawX = 0f
        var downRawY = 0f
        var startX = 0
        var startY = 0
        var movedFar = false
        v.setOnTouchListener { _, e ->
            when (e.action) {
                MotionEvent.ACTION_DOWN -> {
                    downRawX = e.rawX; downRawY = e.rawY
                    startX = ballParams?.x ?: 0; startY = ballParams?.y ?: 0
                    movedFar = false
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = (e.rawX - downRawX).toInt()
                    val dy = (e.rawY - downRawY).toInt()
                    if (abs(dx) > 8 || abs(dy) > 8) movedFar = true
                    if (movedFar) {
                        ballParams?.x = (startX + dx).coerceIn(0, (screenW() - ballSize).coerceAtLeast(0))
                        ballParams?.y = (startY + dy)
                            .coerceIn(0, (screenH() - ballSize - bottomInset()).coerceAtLeast(0))
                        ball?.let { runCatching { wm.updateViewLayout(it, ballParams) } }
                    }
                    true
                }
                MotionEvent.ACTION_UP -> {
                    if (!movedFar) onBallTapped() else savePosition()
                    true
                }
                else -> false
            }
        }

        runCatching { wm.addView(v, params) }
        ball = v
        ballParams = params
        refreshBallState()
    }

    private fun onBallTapped() {
        when {
            MacroService.isRecording -> {
                removePanel()
                service.stopRecording(save = true)
            }
            MacroService.isPlaying -> MacroService.stopAll()
            else -> togglePanel()
        }
    }

    private fun removeBall() {
        ball?.let { runCatching { wm.removeView(it) } }
        ball = null
        ballParams = null
        ballIcon = null
    }

    private fun refreshBallState() {
        val icon = ballIcon ?: return
        when {
            MacroService.isRecording -> {
                icon.setImageResource(R.drawable.ic_ball_stop)
                ball?.setBackgroundResource(R.drawable.bg_ball_record)
            }
            MacroService.isPlaying -> {
                icon.setImageResource(R.drawable.ic_ball_stop)
                ball?.setBackgroundResource(R.drawable.bg_ball)
            }
            else -> {
                icon.setImageResource(R.drawable.ic_ball_play)
                ball?.setBackgroundResource(R.drawable.bg_ball)
            }
        }
    }

    private fun savePosition() {
        val p = ballParams ?: return
        val maxX = (screenW() - ballSize).coerceAtLeast(1)
        val maxY = (screenH() - ballSize).coerceAtLeast(1)
        prefs.edit()
            .putFloat("xRatio", p.x.toFloat() / maxX)
            .putFloat("yRatio", p.y.toFloat() / maxY)
            .apply()
    }

    private fun loadPosition(): Pair<Int, Int> {
        val xr = prefs.getFloat("xRatio", 0.86f)
        val yr = prefs.getFloat("yRatio", 0.42f)
        val maxX = (screenW() - ballSize).coerceAtLeast(1)
        val maxY = (screenH() - ballSize).coerceAtLeast(1)
        return (xr * maxX).toInt() to (yr * maxY).toInt()
    }

    // ---------------- 控制面板 ----------------

    @SuppressLint("ClickableViewAccessibility")
    private fun togglePanel() {
        if (panel != null) {
            removePanel()
            return
        }

        val themed = ContextThemeWrapper(service, R.style.Theme_MacroClicker_Overlay)
        val b = OverlayPanelBinding.inflate(LayoutInflater.from(themed))

        b.panelClose.setOnClickListener { removePanel() }
        b.btnPanelRecord.setOnClickListener {
            removePanel()
            tryRecord()
        }
        b.btnPanelPlay.setOnClickListener {
            removePanel()
            service.startPlayback(MacroStore.loadCurrent(service))
        }
        b.btnPanelStop.setOnClickListener {
            removePanel()
            MacroService.stopAll()
        }
        b.btnPanelOpen.setOnClickListener {
            removePanel()
            openMainActivity()
        }
        panelStatus = b.panelStatus

        val width = min(dp(280), (screenW() * 0.88f).toInt())
        val params = WindowManager.LayoutParams(
            width, WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            val bp = ballParams
            x = ((bp?.x ?: 0) - width / 2 + ballSize / 2)
                .coerceIn(dp(8), (screenW() - width - dp(8)).coerceAtLeast(dp(8)))
            y = ((bp?.y ?: 0) + ballSize + dp(10))
                .coerceIn(dp(8), (screenH() - bottomInset() - dp(340)).coerceAtLeast(dp(8)))
        }

        // 标题行可拖动面板
        var downRawX = 0f
        var downRawY = 0f
        var startX = 0
        var startY = 0
        b.rowDrag.setOnTouchListener { _, e ->
            when (e.action) {
                MotionEvent.ACTION_DOWN -> {
                    downRawX = e.rawX; downRawY = e.rawY
                    startX = panelParams?.x ?: 0; startY = panelParams?.y ?: 0
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    panelParams?.x = (startX + (e.rawX - downRawX).toInt())
                        .coerceIn(0, (screenW() - width).coerceAtLeast(0))
                    panelParams?.y = (startY + (e.rawY - downRawY).toInt())
                        .coerceIn(0, (screenH() - bottomInset() - dp(200)).coerceAtLeast(0))
                    panel?.let { runCatching { wm.updateViewLayout(it, panelParams) } }
                    true
                }
                else -> false
            }
        }

        runCatching { wm.addView(b.root, params) }
        panel = b.root
        panelParams = params
        setStatus(currentStatusText())
    }

    private fun tryRecord() {
        if (!Settings.canDrawOverlays(service)) {
            Toast.makeText(service, R.string.toast_need_overlay, Toast.LENGTH_SHORT).show()
            service.startActivity(Intent(
                Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                Uri.parse("package:${service.packageName}")
            ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            return
        }
        service.startRecording(MacroStore.liveReplay(service))
    }

    private fun removePanel() {
        panel?.let { runCatching { wm.removeView(it) } }
        panel = null
        panelParams = null
        panelStatus = null
    }

    private fun openMainActivity() {
        service.packageManager.getLaunchIntentForPackage(service.packageName)?.let {
            it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            service.startActivity(it)
        }
    }

    // ---------------- 状态（任意线程可调） ----------------

    fun setStatus(text: String) = handler.post {
        panelStatus?.text = text
    }

    fun onPlayStateChanged(playing: Boolean) = handler.post {
        refreshBallState()
        setStatus(currentStatusText())
    }

    fun onRecordingChanged(recording: Boolean) = handler.post {
        refreshBallState()
        setStatus(if (recording) currentStatusText() else service.getString(R.string.panel_status_idle))
    }

    private fun currentStatusText(): String = when {
        MacroService.isRecording -> service.getString(R.string.rec_short)
        MacroService.isPlaying -> service.getString(R.string.playing_short)
        else -> service.getString(R.string.panel_status_idle)
    }
}
