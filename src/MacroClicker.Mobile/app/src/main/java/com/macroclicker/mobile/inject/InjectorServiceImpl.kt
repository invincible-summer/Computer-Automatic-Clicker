package com.macroclicker.mobile.inject

import android.os.SystemClock
import android.view.InputDevice
import android.view.MotionEvent
import java.lang.reflect.Method
import java.util.concurrent.TimeUnit

/**
 * IInjectorService 实现：运行在 Shizuku 启动的 shell uid 进程中。
 *
 * 快速路径：反射调用 android.hardware.input.InputManager#injectInputEvent，
 * 事件构造方式对照 AOSP cmds/input/Input.java（SOURCE_TOUCHSCREEN，
 * DOWN 压力 1.0 / UP 压力 0.0，滑动按 eventTime 差表达时长，
 * WAIT_FOR_FINISH 模式同步等待系统处理完成）。UserService 进程不受
 * 隐藏 API 限制（Shizuku 特性），因此无需任何绕过手段。
 *
 * 兼容路径：ProcessBuilder 执行固定 argv 的 /system/bin/input tap|swipe
 * （坐标全是 int 转成的数字字符串，无任何 shell 解释器参与）。
 *
 * 回落规则：快速路径整体不可用（反射失败）或首个事件尚未注入即失败时，
 * 安全回落兼容路径；一旦已有事件注入成功则不回落（避免双击）。
 */
class InjectorServiceImpl : IInjectorService.Stub() {

    /** 快速路径句柄；反射失败为 null（此时全部走兼容路径）。 */
    private val fast: FastInjector? = FastInjector.build()

    @Synchronized
    override fun probe(): Int = if (fast != null) 1 else 0

    @Synchronized
    override fun tap(x: Int, y: Int): Int {
        fast?.tap(x, y)?.let { return it }          // null = 未注入任何事件，可安全回落
        return compat(arrayOf(INPUT_BIN, "tap", x.toString(), y.toString()))
    }

    @Synchronized
    override fun swipe(x1: Int, y1: Int, x2: Int, y2: Int, durationMs: Int): Int {
        val d = durationMs.coerceIn(50, 60_000)
        fast?.swipe(x1, y1, x2, y2, d)?.let { return it }
        return compat(arrayOf(INPUT_BIN, "swipe",
            x1.toString(), y1.toString(), x2.toString(), y2.toString(), d.toString()))
    }

    override fun destroy() {
        System.exit(0)
    }

    /** 兼容路径：固定 argv + 15s 超时；返回进程退出码（0 成功）。 */
    private fun compat(cmd: Array<String>): Int {
        if (cmd.isEmpty()) return -2
        return try {
            val p = ProcessBuilder(*cmd).redirectErrorStream(true).start()
            p.outputStream.close() // 无标准输入，避免个别命令等待 stdin
            try {
                if (!p.waitFor(TIMEOUT_S, TimeUnit.SECONDS)) {
                    p.destroyForcibly()
                    return -124
                }
            } catch (_: InterruptedException) {
                p.destroyForcibly()
                Thread.currentThread().interrupt()
                return -3
            }
            runCatching { p.inputStream.readBytes() } // input 通常无输出；读掉残留
            p.exitValue()
        } catch (_: Exception) {
            -1
        }
    }

    companion object {
        private const val INPUT_BIN = "/system/bin/input"
        private const val TIMEOUT_S = 15L
    }
}

/**
 * 快速注入器：持有反射取得的 InputManager#injectInputEvent(MotionEvent, int)。
 * WAIT_FOR_FINISH(2) 与 AOSP Input.java 一致，保证时序同步。
 */
private class FastInjector private constructor(
    private val im: Any,
    private val inject: Method,
) {
    /** 本次调用是否已注入至少一个事件（决定能否安全回落兼容路径）。 */
    private var injected = false

    /** 返回 0 成功 / 负数失败 / null=尚未注入任何事件（调用方可回落）。 */
    fun tap(x: Int, y: Int): Int? {
        injected = false
        return runCatching {
            val now = SystemClock.uptimeMillis()
            send(now, MotionEvent.ACTION_DOWN, x.toFloat(), y.toFloat(), 1f)
            send(now, MotionEvent.ACTION_UP, x.toFloat(), y.toFloat(), 0f)
            0
        }.getOrElse { if (injected) -1 else null }
    }

    fun swipe(x1: Int, y1: Int, x2: Int, y2: Int, durationMs: Int): Int? {
        injected = false
        return runCatching {
            val downTime = SystemClock.uptimeMillis()
            send(downTime, MotionEvent.ACTION_DOWN, x1.toFloat(), y1.toFloat(), 1f)
            val samePoint = x1 == x2 && y1 == y2
            if (samePoint) {
                // 长按：指腹停留，无 MOVE 是自然状态，仅按 eventTime 表达时长
                sleepQuietly(durationMs.toLong())
            } else {
                // 滑动：插值 MOVE（步长 ~20ms，最多 ~60 步），墙钟与事件时间一致
                val steps = (durationMs / 20).coerceIn(1, 60)
                val stepMs = durationMs.toDouble() / steps
                for (i in 1 until steps) {
                    sleepQuietly(stepMs.toLong())
                    val t = (i.toDouble() / steps)
                    val mx = x1 + (x2 - x1) * t
                    val my = y1 + (y2 - y1) * t
                    send(downTime, MotionEvent.ACTION_MOVE, mx.toFloat(), my.toFloat(), 1f,
                        eventTimeOffset = (stepMs * i).toLong())
                }
                sleepQuietly((durationMs - (stepMs * (steps - 1)).toLong()).toLong())
            }
            val upTime = SystemClock.uptimeMillis()
            send(downTime, MotionEvent.ACTION_UP, x2.toFloat(), y2.toFloat(), 0f,
                eventTimeOffset = upTime - downTime)
            0
        }.getOrElse { if (injected) -1 else null }
    }

    /** 注入单个 MotionEvent；返回 false = 系统拒绝（未生效）。size 随压力（AOSP 惯例：DOWN/MOVE 为 1，UP 为 0）。 */
    private fun send(
        downTime: Long,
        action: Int,
        x: Float,
        y: Float,
        pressure: Float,
        eventTimeOffset: Long = 0L,
    ) {
        val eventTime = downTime + eventTimeOffset
        val e = MotionEvent.obtain(
            downTime, eventTime, action, x, y, pressure, pressure,
            0, 1f, 1f, 0, 0
        )
        try {
            e.source = InputDevice.SOURCE_TOUCHSCREEN
            // 新旧系统返回 Boolean 或 Int（0=成功），统一解释
            when (val r = inject.invoke(im, e, MODE_WAIT_FOR_FINISH)) {
                is Boolean -> if (r) injected = true else throw IllegalStateException("injectInputEvent denied")
                is Int -> if (r == 0) injected = true else throw IllegalStateException("injectInputEvent result=$r")
                else -> throw IllegalStateException("injectInputEvent unknown result")
            }
        } finally {
            e.recycle()
        }
    }

    private fun sleepQuietly(ms: Long) {
        if (ms > 0) try {
            Thread.sleep(ms)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
            throw IllegalStateException("interrupted")
        }
    }

    companion object {
        private const val MODE_WAIT_FOR_FINISH = 2 // InputManager.INJECT_INPUT_EVENT_MODE_WAIT_FOR_FINISH

        fun build(): FastInjector? = runCatching {
            val im = Class.forName("android.hardware.input.InputManager")
                .getMethod("getInstance").invoke(null)
            val inject = im.javaClass.getMethod(
                "injectInputEvent", MotionEvent::class.java, Int::class.javaPrimitiveType)
            FastInjector(im, inject)
        }.getOrNull()
    }
}
