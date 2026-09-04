package com.macroclicker.mobile.overlay

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.PixelFormat
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.TextView
import com.macroclicker.mobile.R
import com.macroclicker.mobile.service.ClickService
import com.macroclicker.mobile.store.ConfigStore
import kotlin.math.abs

/**
 * 悬浮控制：可拖动悬浮球（贴边位置按屏幕比例记忆）+ 可展开控制面板。
 * 全部尺寸使用 dp，面板宽度自适应小屏，兼容不同机型与横竖屏。
 */
class FloatingControls(private val service: ClickService) {

    private val wm = service.getSystemService(Context.WINDOW_SERVICE) as WindowManager
    private val handler = Handler(Looper.getMainLooper())
    private val prefs = service.getSharedPreferences("floating", Context.MODE_PRIVATE)

    private var ball: View? = null
    private var ballParams: WindowManager.LayoutParams? = null
    private var panel: View? = null
    private var panelParams: WindowManager.LayoutParams? = null

    private fun dp(v: Int): Int = (v * service.resources.displayMetrics.density).toInt()

    private fun screenW(): Int = service.resources.displayMetrics.widthPixels
    private fun screenH(): Int = service.resources.displayMetrics.heightPixels

    fun show() = handler.post { addBall() }

    fun hide() = handler.post {
        removePanel()
        removeBall()
    }

    fun remove() = handler.post {
        removePanel()
        removeBall()
    }

    // ---------------- 悬浮球 ----------------

    @SuppressLint("ClickableViewAccessibility", "InflateParams")
    private fun addBall() {
        if (ball != null) return
        val v = LayoutInflater.from(service).inflate(R.layout.view_floating_ball, null)
        val size = dp(52)
        val params = WindowManager.LayoutParams(
            size, size,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            val saved = loadPosition()
            x = saved.first.coerceIn(0, (screenW() - size).coerceAtLeast(0))
            y = saved.second.coerceIn(0, (screenH() - size).coerceAtLeast(0))
        }

        var downRawX = 0f
        var downRawY = 0f
        var startX = 0
        var startY = 0
        var moved = false

        v.setOnTouchListener { _, e ->
            when (e.action) {
                MotionEvent.ACTION_DOWN -> {
                    downRawX = e.rawX; downRawY = e.rawY
                    startX = ballParams?.x ?: 0; startY = ballParams?.y ?: 0
                    moved = false
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = (e.rawX - downRawX).toInt()
                    val dy = (e.rawY - downRawY).toInt()
                    if (abs(dx) > 6 || abs(dy) > 6) moved = true
                    if (moved) {
                        ballParams?.x = (startX + dx).coerceIn(0, (screenW() - size).coerceAtLeast(0))
                        ballParams?.y = (startY + dy).coerceIn(0, (screenH() - size).coerceAtLeast(0))
                        ball?.let { wm.updateViewLayout(it, ballParams) }
                    }
                    true
                }
                MotionEvent.ACTION_UP -> {
                    if (!moved) togglePanel() else savePosition()
                    true
                }
                else -> false
            }
        }

        wm.addView(v, params)
        ball = v
        ballParams = params
        refreshBallState()
    }

    private fun removeBall() {
        ball?.let { runCatching { wm.removeView(it) } }
        ball = null
        ballParams = null
    }

    private fun refreshBallState() {
        val tv = ball?.findViewById<TextView>(R.id.tvBall) ?: return
        if (ClickService.isPlaying) {
            tv.text = "■"
            tv.background = service.getDrawable(R.drawable.bg_floating_ball_danger)
        } else {
            tv.text = "▶"
            tv.background = service.getDrawable(R.drawable.bg_floating_ball)
        }
    }

    private fun savePosition() {
        val p = ballParams ?: return
        val maxX = (screenW() - dp(52)).coerceAtLeast(1)
        val maxY = (screenH() - dp(52)).coerceAtLeast(1)
        prefs.edit()
            .putFloat("xRatio", p.x.toFloat() / maxX)
            .putFloat("yRatio", p.y.toFloat() / maxY)
            .apply()
    }

    private fun loadPosition(): Pair<Int, Int> {
        val xr = prefs.getFloat("xRatio", 0.78f)
        val yr = prefs.getFloat("yRatio", 0.38f)
        val maxX = (screenW() - dp(52)).coerceAtLeast(1)
        val maxY = (screenH() - dp(52)).coerceAtLeast(1)
        return (xr * maxX).toInt() to (yr * maxY).toInt()
    }

    // ---------------- 展开面板 ----------------

    @SuppressLint("ClickableViewAccessibility", "InflateParams")
    private fun togglePanel() {
        if (panel != null) {
            removePanel()
            return
        }
        val v = LayoutInflater.from(service).inflate(R.layout.view_floating_panel, null)
        val width = minOf(dp(232), (screenW() * 0.8f).toInt())
        val params = WindowManager.LayoutParams(
            width, WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            val bp = ballParams
            x = ((bp?.x ?: 0) - width / 2).coerceIn(dp(8), (screenW() - width - dp(8)).coerceAtLeast(dp(8)))
            y = ((bp?.y ?: 0) + dp(56)).coerceIn(dp(8), (screenH() - dp(260)).coerceAtLeast(dp(8)))
        }

        v.findViewById<TextView>(R.id.btnPlay).setOnClickListener {
            removePanel()
            service.startPlayback(ConfigStore.load(service))
        }
        v.findViewById<TextView>(R.id.btnStop).setOnClickListener {
            removePanel()
            ClickService.stopAll()
        }
        v.findViewById<TextView>(R.id.btnAddTap).setOnClickListener {
            removePanel()
            service.startPick(PickOverlay.Mode.TAP)
        }
        v.findViewById<TextView>(R.id.btnAddSwipe).setOnClickListener {
            removePanel()
            service.startPick(PickOverlay.Mode.SWIPE)
        }
        v.findViewById<TextView>(R.id.btnOpenList).setOnClickListener {
            removePanel()
            openMainActivity()
        }
        v.findViewById<TextView>(R.id.btnCollapse2).setOnClickListener { removePanel() }
        v.findViewById<TextView>(R.id.btnCollapse).setOnClickListener { removePanel() }

        // 标题栏拖动
        val dragBar = v.findViewById<View>(R.id.panelDragBar)
        var downRawX = 0f
        var downRawY = 0f
        var startX = 0
        var startY = 0
        dragBar.setOnTouchListener { _, e ->
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
                        .coerceIn(0, (screenH() - dp(120)).coerceAtLeast(0))
                    panel?.let { wm.updateViewLayout(it, panelParams) }
                    true
                }
                else -> false
            }
        }

        wm.addView(v, params)
        panel = v
        panelParams = params
        setStatus(if (ClickService.isPlaying) "执行中…" else service.getString(R.string.panel_status_idle))
    }

    private fun removePanel() {
        panel?.let { runCatching { wm.removeView(it) } }
        panel = null
        panelParams = null
    }

    private fun openMainActivity() {
        val intent = service.packageManager.getLaunchIntentForPackage(service.packageName)
        intent?.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK or android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP)
        if (intent != null) service.startActivity(intent)
    }

    // ---------------- 状态（线程安全） ----------------

    fun setStatus(text: String) = handler.post {
        panel?.findViewById<TextView>(R.id.tvStatus)?.text = text
    }

    fun onPlayStateChanged(playing: Boolean) = handler.post {
        refreshBallState()
        setStatus(if (playing) "执行中…" else service.getString(R.string.panel_status_idle))
    }
}
