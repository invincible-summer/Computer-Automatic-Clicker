package com.macroclicker.mobile.overlay

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Color
import android.graphics.PixelFormat
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import com.macroclicker.mobile.R
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroEvent
import com.macroclicker.mobile.service.ClickService
import com.macroclicker.mobile.store.ConfigStore

/**
 * 屏幕取点浮层：半透明全屏，点哪取哪。
 * 点击模式：可连续取多个点（点标记可删除）；滑动模式：依次取起点与终点。
 */
class PickOverlay(private val service: ClickService) {

    enum class Mode { TAP, SWIPE }

    private class Marker(val view: View, val x: Int, val y: Int)

    private val wm = service.getSystemService(Context.WINDOW_SERVICE) as WindowManager
    private val handler = Handler(Looper.getMainLooper())

    private var root: FrameLayout? = null
    private var hintText: TextView? = null
    private var mode = Mode.TAP
    private val markers = mutableListOf<Marker>()
    private var swipeStart: Marker? = null

    private fun dp(v: Int): Int = (v * service.resources.displayMetrics.density).toInt()

    fun start(mode: Mode) = handler.post { show(mode) }

    fun dismiss() = handler.post { hide() }

    @SuppressLint("ClickableViewAccessibility")
    private fun show(mode: Mode) {
        if (root != null) return
        this.mode = mode
        markers.clear()
        swipeStart = null

        val frame = FrameLayout(service)
        frame.setBackgroundColor(Color.parseColor("#33000000"))

        val hint = TextView(service).apply {
            setTextColor(Color.WHITE)
            textSize = 14f
            gravity = Gravity.CENTER
            setPadding(dp(16), dp(10), dp(16), dp(10))
            setBackgroundResource(R.drawable.bg_pick_bottom_bar)
            text = service.getString(
                if (mode == Mode.TAP) R.string.pick_tap_hint else R.string.pick_swipe_start
            )
        }
        frame.addView(hint, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.CENTER_HORIZONTAL
        ).apply { topMargin = dp(56) })
        hintText = hint

        // 底部操作条：取消 / 完成
        val bar = LinearLayout(service).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundResource(R.drawable.bg_pick_bottom_bar)
            setPadding(dp(18), dp(10), dp(18), dp(10))
        }
        val cancel = TextView(service).apply {
            text = service.getString(R.string.pick_cancel)
            setTextColor(Color.WHITE)
            textSize = 14f
            setPadding(dp(10), dp(4), dp(10), dp(4))
        }
        val done = TextView(service).apply {
            text = service.getString(R.string.pick_done)
            setTextColor(Color.WHITE)
            textSize = 14f
            setPadding(dp(14), dp(4), dp(6), dp(4))
        }
        bar.addView(cancel)
        bar.addView(done)
        frame.addView(bar, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
        ).apply { bottomMargin = dp(48) })

        cancel.setOnClickListener { finish(added = false) }
        done.setOnClickListener { finish(added = true) }

        frame.setOnTouchListener { _, e ->
            if (e.action == MotionEvent.ACTION_UP) onPicked(e.rawX.toInt(), e.rawY.toInt())
            true
        }

        val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT, WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
            PixelFormat.TRANSLUCENT
        )
        wm.addView(frame, params)
        root = frame
    }

    private fun onPicked(x: Int, y: Int) {
        when (mode) {
            Mode.TAP -> addMarker(x, y, removable = true)
            Mode.SWIPE -> {
                val start = swipeStart
                if (start == null) {
                    swipeStart = addMarker(x, y, removable = false)
                    hintText?.text = service.getString(R.string.pick_swipe_end)
                } else {
                    addMarker(x, y, removable = false)
                    appendEvent(
                        MacroEvent(
                            type = EventType.SWIPE, delay = 0.3,
                            x = start.x, y = start.y, x2 = x, y2 = y
                        )
                    )
                    Toast.makeText(service, R.string.toast_swipe_added, Toast.LENGTH_SHORT).show()
                    finish(added = false)
                }
            }
        }
    }

    private fun addMarker(x: Int, y: Int, removable: Boolean): Marker {
        val tv = TextView(service).apply {
            text = "✕"
            setTextColor(Color.WHITE)
            textSize = 13f
            gravity = Gravity.CENTER
            setBackgroundResource(R.drawable.bg_pick_marker)
        }
        val lp = FrameLayout.LayoutParams(dp(30), dp(30)).apply {
            leftMargin = x - dp(15)
            topMargin = y - dp(15)
        }
        val marker = Marker(tv, x, y)
        if (removable) {
            tv.setOnClickListener {
                (tv.parent as? FrameLayout)?.removeView(tv)
                markers.remove(marker)
            }
        }
        root?.addView(tv, lp)
        markers.add(marker)
        return marker
    }

    private fun finish(added: Boolean) {
        var count = 0
        if (added && mode == Mode.TAP && markers.isNotEmpty()) {
            markers.forEach { m ->
                appendEvent(MacroEvent(type = EventType.TAP, delay = 0.3, x = m.x, y = m.y))
            }
            count = markers.size
        }
        hide()
        service.onPickFinished()
        if (count > 0) {
            Toast.makeText(service, service.getString(R.string.toast_pick_done, count), Toast.LENGTH_SHORT).show()
        }
    }

    private fun hide() {
        root?.let { runCatching { wm.removeView(it) } }
        root = null
        hintText = null
        markers.clear()
        swipeStart = null
    }

    private fun appendEvent(ev: MacroEvent) {
        val cfg = ConfigStore.load(service)
        cfg.events.add(ev)
        ConfigStore.save(service, cfg)
    }
}
