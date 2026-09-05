package com.macroclicker.mobile.overlay

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.graphics.PixelFormat
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import com.macroclicker.mobile.R
import com.macroclicker.mobile.service.MacroService
import com.macroclicker.mobile.store.MacroStore
import kotlin.math.abs
import kotlin.math.min

/**
 * 悬浮控制球：可拖动、位置按屏幕比例记忆；点按展开控制面板。
 * 录制/执行中点球即停止，无需回到 App。
 */
class FloatingBall(private val service: MacroService) {

    private val wm = service.getSystemService(Context.WINDOW_SERVICE) as WindowManager
    private val handler = Handler(Looper.getMainLooper())
    private val prefs = service.getSharedPreferences("floating", Context.MODE_PRIVATE)

    private var ball: View? = null
    private var ballParams: WindowManager.LayoutParams? = null
    private var ballGlyph: TextView? = null
    private var panel: View? = null
    private var panelParams: WindowManager.LayoutParams? = null
    private var panelStatus: TextView? = null

    private val ballSize get() = dp(54)

    private fun dp(v: Int): Int = (v * service.resources.displayMetrics.density).toInt()
    private fun screenW(): Int = service.resources.displayMetrics.widthPixels
    private fun screenH(): Int = service.resources.displayMetrics.heightPixels

    fun show() = handler.post { addBall() }

    fun remove() = handler.post {
        removePanel()
        removeBall()
    }

    // ---------------- 悬浮球 ----------------

    @SuppressLint("ClickableViewAccessibility")
    private fun addBall() {
        if (ball != null) return
        val glyph = TextView(service).apply {
            text = "▶"
            setTextColor(0xFFFFFFFF.toInt())
            textSize = 20f
            gravity = Gravity.CENTER
        }
        ballGlyph = glyph
        val v = FrameLayout(service).apply {
            setBackgroundResource(R.drawable.bg_ball)
            addView(glyph, FrameLayout.LayoutParams(
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
                        ballParams?.y = (startY + dy).coerceIn(0, (screenH() - ballSize).coerceAtLeast(0))
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
        ballGlyph = null
    }

    private fun refreshBallState() {
        val glyph = ballGlyph ?: return
        when {
            MacroService.isRecording -> {
                glyph.text = "■"
                ball?.setBackgroundResource(R.drawable.bg_ball_record)
            }
            MacroService.isPlaying -> {
                glyph.text = "■"
                ball?.setBackgroundResource(R.drawable.bg_ball)
            }
            else -> {
                glyph.text = "▶"
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

        val root = LinearLayout(service).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundResource(R.drawable.bg_panel)
            setPadding(dp(16), dp(14), dp(16), dp(12))
        }

        val title = root.addViewText(service.getString(R.string.panel_title), 15f, true)
        panelStatus = root.addViewText(service.getString(R.string.panel_status_idle), 12f, color = 0xB3FFFFFF.toInt())

        val grid = LinearLayout(service).apply { orientation = LinearLayout.VERTICAL }
        val row1 = LinearLayout(service)
        val row2 = LinearLayout(service)
        grid.addView(row1, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT))
        grid.addView(row2, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT))
        root.addView(grid, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dp(10) })

        fun cell(parent: LinearLayout, text: String, bg: Int, fg: Int, onClick: () -> Unit) {
            val tv = TextView(service).apply {
                this.text = text
                textSize = 14f
                gravity = Gravity.CENTER
                setTextColor(fg)
                setBackgroundResource(bg)
                setPadding(dp(6), dp(10), dp(6), dp(10))
                setOnClickListener { onClick() }
            }
            val lp = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            lp.marginEnd = dp(6)
            lp.topMargin = dp(6)
            parent.addView(tv, lp)
        }

        cell(row1, service.getString(R.string.panel_record), R.drawable.bg_panel_btn_accent, 0xFFFFFFFF.toInt()) {
            removePanel()
            tryRecord()
        }
        cell(row1, service.getString(R.string.panel_play), R.drawable.bg_panel_btn, 0xFFFFFFFF.toInt()) {
            removePanel()
            service.startPlayback(MacroStore.loadCurrent(service))
        }
        cell(row2, service.getString(R.string.panel_stop), R.drawable.bg_panel_btn_danger, 0xFFFFFFFF.toInt()) {
            removePanel()
            MacroService.stopAll()
        }
        cell(row2, service.getString(R.string.panel_open), R.drawable.bg_panel_btn, 0xFFFFFFFF.toInt()) {
            removePanel()
            openMainActivity()
        }

        val width = min(dp(268), (screenW() * 0.86f).toInt())
        val params = WindowManager.LayoutParams(
            width, WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            val bp = ballParams
            x = ((bp?.x ?: 0) - width / 2 + ballSize / 2).coerceIn(dp(8), (screenW() - width - dp(8)).coerceAtLeast(dp(8)))
            y = ((bp?.y ?: 0) + ballSize + dp(10)).coerceIn(dp(8), (screenH() - dp(320)).coerceAtLeast(dp(8)))
        }

        // 拖动条（标题行）
        var downRawX = 0f
        var downRawY = 0f
        var startX = 0
        var startY = 0
        title.setOnTouchListener { _, e ->
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
                        .coerceIn(0, (screenH() - dp(320)).coerceAtLeast(0))
                    panel?.let { runCatching { wm.updateViewLayout(it, panelParams) } }
                    true
                }
                else -> false
            }
        }

        runCatching { wm.addView(root, params) }
        panel = root
        panelParams = params
        setStatus(currentStatusText())
    }

    private fun LinearLayout.addViewText(text: String, size: Float, bold: Boolean = false, color: Int = 0xFFFFFFFF.toInt()): TextView {
        val tv = TextView(service).apply {
            this.text = text
            textSize = size
            setTextColor(color)
            this.gravity = Gravity.CENTER
            paint.isFakeBoldText = bold
        }
        addView(tv, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dp(4) })
        return tv
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
