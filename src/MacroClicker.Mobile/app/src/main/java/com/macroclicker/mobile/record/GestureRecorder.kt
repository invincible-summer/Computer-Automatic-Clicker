package com.macroclicker.mobile.record

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.PixelFormat
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.Gravity
import android.view.MotionEvent
import android.view.ViewConfiguration
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import android.widget.Toast
import com.macroclicker.mobile.R
import com.macroclicker.mobile.model.MacroEvent
import com.macroclicker.mobile.service.MacroService
import kotlin.math.abs

/**
 * 完整动作录制：全屏录制层捕获用户整套手势流，
 * 自动识别 点击 / 长按 / 滑动 / 等待（手势间隔）——与桌面端录制体验一致。
 *
 * 回放同步（liveReplay）：每个手势录制完成后立即经无障碍 dispatchGesture
 * 注入真实应用（层短暂切为 FLAG_NOT_TOUCHABLE 穿透），可边操作边录多步流程；
 * 关闭时为纯演示录制（手势只被记录、不作用于当前界面）。
 */
class GestureRecorder(
    private val service: MacroService,
    private val liveReplay: Boolean,
    private val onCountChanged: (Int) -> Unit,
    private val onFinished: (List<MacroEvent>) -> Unit,
) {
    @Volatile
    var isActive = false
        private set

    private val wm = service.getSystemService(Context.WINDOW_SERVICE) as WindowManager
    private val handler = Handler(Looper.getMainLooper())

    private var layer: FrameLayout? = null
    private var layerParams: WindowManager.LayoutParams? = null

    private val events = mutableListOf<MacroEvent>()

    // 当前手势状态
    private var downTime = 0L
    private var downX = 0f
    private var downY = 0f
    private var curX = 0f
    private var curY = 0f
    private var moved = false
    private var multiTouch = false

    /** 上一个手势结束时间（elapsedRealtime），用于计算等待间隔。 */
    private var lastGestureEnd = 0L

    private val slop = ViewConfiguration.get(service).scaledTouchSlop * 2
    private val longPressMs = ViewConfiguration.getLongPressTimeout() + 100L

    private fun dp(v: Int): Int = (v * service.resources.displayMetrics.density).toInt()

    fun start() = handler.post { addLayer() }

    @SuppressLint("ClickableViewAccessibility")
    private fun addLayer() {
        if (layer != null) return
        isActive = true

        val frame = FrameLayout(service)
        frame.setBackgroundColor(0x08000000.toInt()) // 极淡蒙层，提示“正在捕获”

        val chip = TextView(service).apply {
            text = service.getString(R.string.rec_hint)
            setTextColor(0xFFFFFFFF.toInt())
            textSize = 13f
            gravity = Gravity.CENTER
            setPadding(dp(16), dp(8), dp(16), dp(8))
            setBackgroundResource(R.drawable.bg_record_chip)
        }
        frame.addView(chip, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT,
            FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.CENTER_HORIZONTAL
        ).apply { topMargin = statusBarHeight() + dp(10) })

        frame.setOnTouchListener { _, e -> onTouchEvent(e) }

        val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            BASE_FLAGS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            x = 0
            y = 0
        }
        runCatching { wm.addView(frame, params) }
            .onFailure {
                isActive = false
                Toast.makeText(service, R.string.toast_record_layer_fail, Toast.LENGTH_SHORT).show()
                onFinished(emptyList())
                return
            }
        layer = frame
        layerParams = params
    }

    private fun onTouchEvent(e: MotionEvent): Boolean {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                multiTouch = false
                moved = false
                downTime = SystemClock.elapsedRealtime()
                downX = e.rawX
                downY = e.rawY
                curX = e.rawX
                curY = e.rawY
            }
            MotionEvent.ACTION_POINTER_DOWN -> multiTouch = true
            MotionEvent.ACTION_MOVE -> {
                curX = e.rawX
                curY = e.rawY
                if (abs(curX - downX) > slop || abs(curY - downY) > slop) moved = true
            }
            MotionEvent.ACTION_POINTER_UP -> Unit // 多指手势整体忽略
            MotionEvent.ACTION_UP -> {
                val endT = SystemClock.elapsedRealtime()
                if (multiTouch) {
                    Toast.makeText(service, R.string.toast_multi_touch, Toast.LENGTH_SHORT).show()
                } else {
                    appendGesture(downTime, downX, downY, curX, curY, endT - downTime)
                }
                lastGestureEnd = endT
            }
            MotionEvent.ACTION_CANCEL -> {
                multiTouch = false
                moved = false
            }
        }
        return true
    }

    private fun appendGesture(gestureDownT: Long, x1: Float, y1: Float, x2: Float, y2: Float, durMs: Long) {
        val delay = if (lastGestureEnd == 0L) 0.0 else
            Math.max(0.0, (gestureDownT - lastGestureEnd) / 1000.0)
        val ev = when {
            !moved && durMs < longPressMs ->
                MacroEvent.tap(x1.toInt(), y1.toInt(), round2(delay))
            !moved ->
                MacroEvent.longPress(x1.toInt(), y1.toInt(), round2(delay),
                    durMs.coerceIn(50, 60_000).toInt())
            else ->
                MacroEvent.swipe(x1.toInt(), y1.toInt(), x2.toInt(), y2.toInt(),
                    round2(delay), durMs.coerceIn(50, 60_000).toInt())
        }
        events.add(ev)
        onCountChanged(events.size)
        if (liveReplay) replay(ev)
    }

    /** 回放同步：注入刚录下的手势到真实应用，层短暂不可触摸以放行注入。 */
    private fun replay(ev: MacroEvent) {
        val gesture = service.buildGesture(ev) ?: return
        setLayerTouch(false)
        val timeout = if (ev.type == com.macroclicker.mobile.model.EventType.SWIPE)
            ev.duration.toLong() + 1500 else 2500
        service.dispatchAsync(gesture, timeout) { setLayerTouch(true) }
    }

    private fun setLayerTouch(enabled: Boolean) {
        handler.post {
            val f = layer ?: return@post
            val p = layerParams ?: return@post
            p.flags = if (enabled) BASE_FLAGS else BASE_FLAGS or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
            runCatching { wm.updateViewLayout(f, p) }
        }
    }

    /** 保存=true 时把已录事件回传；取消时回传空表。 */
    fun stop(save: Boolean) = handler.post {
        val f = layer
        layer = null
        layerParams = null
        if (f != null) runCatching { wm.removeView(f) }
        isActive = false
        onFinished(if (save) events.toList() else emptyList())
    }

    private fun statusBarHeight(): Int {
        val res = service.resources
        val id = res.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) res.getDimensionPixelSize(id) else dp(28)
    }

    private fun round2(v: Double) = Math.round(v * 100.0) / 100.0

    companion object {
        private const val BASE_FLAGS = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
    }
}
