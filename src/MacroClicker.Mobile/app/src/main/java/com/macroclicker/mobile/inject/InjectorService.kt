package com.macroclicker.mobile.inject

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Intent
import android.graphics.Path
import android.view.accessibility.AccessibilityEvent
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * 无障碍注入服务（v5.0，用户决策回归无障碍通道、彻底移除 Shizuku）：
 * 只做一件事——把 tap/swipe 手势经 dispatchGesture 派发到屏幕坐标。
 *
 * 安全边界（由 xml/accessibility_service_config 强制）：
 *  - canRetrieveWindowContent=false：不读取任何屏幕内容；
 *  - 不订阅任何无障碍事件（onAccessibilityEvent 空实现）；
 *  - 注入只允许固定路径 + 数字坐标的手势，不存在任何屏幕感知能力。
 *
 * 可靠性设计（吸取 v1/v2 无障碍版教训）：
 *  - onServiceConnected/onUnbind 同步维护静态 instance，状态机据此识别「已开启但未连接」；
 *  - 派发同步等待结果回调，超时保底（时长 + 15s）——回放绝不无限卡死；
 *  - 服务断开 → Injector 状态回调 → 回放立即停止，绝不盲点续跑。
 */
class InjectorService : AccessibilityService() {

    companion object {
        @Volatile
        var instance: InjectorService? = null
            private set

        private const val TAP_MS = 50L
        private const val TIMEOUT_MARGIN_MS = 15_000L
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        Injector.refresh()
    }

    override fun onUnbind(intent: Intent?): Boolean {
        instance = null
        Injector.refresh()
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        Injector.refresh()
        super.onDestroy()
    }

    override fun onInterrupt() = Unit

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit

    fun tap(x: Int, y: Int): Boolean = dispatch(tapGesture(x, y), TAP_MS)

    fun swipe(x1: Int, y1: Int, x2: Int, y2: Int, durationMs: Int): Boolean {
        val d = durationMs.coerceIn(50, 60_000).toLong()
        val p = Path().apply {
            moveTo(x1.toFloat(), y1.toFloat())
            lineTo(x2.toFloat(), y2.toFloat())
        }
        return dispatch(buildGesture(p, d), d)
    }

    /** 点击：同点短笔画（系统按 DOWN+UP 派发）；路径需一段 lineTo 保证各系统识别。 */
    private fun tapGesture(x: Int, y: Int): GestureDescription {
        val p = Path().apply {
            moveTo(x.toFloat(), y.toFloat())
            lineTo(x.toFloat(), y.toFloat())
        }
        return buildGesture(p, TAP_MS)
    }

    private fun buildGesture(path: Path, durationMs: Long): GestureDescription =
        GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, durationMs))
            .build()

    /** 同步派发：阻塞等待结果回调（超时保底），true = onCompleted。可从任意线程调用。 */
    private fun dispatch(gesture: GestureDescription, durationMs: Long): Boolean {
        val latch = CountDownLatch(1)
        var completed = false
        val accepted = runCatching {
            dispatchGesture(gesture, object : GestureResultCallback() {
                override fun onCompleted(gestureDescription: GestureDescription?) {
                    completed = true
                    latch.countDown()
                }

                override fun onCancelled(gestureDescription: GestureDescription?) {
                    latch.countDown()
                }
            }, null)
        }.getOrDefault(false)
        if (!accepted) return false
        return try {
            latch.await(durationMs + TIMEOUT_MARGIN_MS, TimeUnit.MILLISECONDS) && completed
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
            false
        }
    }
}
