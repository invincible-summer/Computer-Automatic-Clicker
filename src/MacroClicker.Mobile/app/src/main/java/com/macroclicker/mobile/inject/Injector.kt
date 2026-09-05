package com.macroclicker.mobile.inject

import android.accessibilityservice.AccessibilityServiceInfo
import android.content.ComponentName
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.accessibility.AccessibilityManager
import java.util.concurrent.CopyOnWriteArraySet

/**
 * 无障碍注入引擎管理器（v5.0，替代 v3/v4 的 Shizuku 方案）：
 * 三态状态机（未开启 / 已开启待连接 / 就绪）+ 手势派发入口。
 *
 * - 状态识别吸取 v1/v2 教训：不仅查系统「已启用服务」列表，更以服务自身的
 *   onServiceConnected 静态实例为准——「已开启但未连接」（OEM 延迟/服务被杀）单独成态并引导用户；
 * - tap/swipe 可在任意线程同步调用（阻塞到手势回调，服务侧有超时保底）；
 * - 服务断开（onUnbind）→ refresh → 状态回调 → 回放立即停止，绝不盲点续跑。
 */
object Injector {

    enum class State { NOT_ENABLED, WAITING, READY }

    @Volatile
    var state: State = State.NOT_ENABLED
        private set

    private var appContext: Context? = null
    private val mainHandler = Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArraySet<(State) -> Unit>()

    /** Application.onCreate 调用一次。 */
    fun init(context: Context) {
        if (appContext != null) return
        appContext = context.applicationContext
        refresh()
    }

    fun addStateListener(l: (State) -> Unit) {
        listeners.add(l)
        mainHandler.post { l(state) }
    }

    fun removeStateListener(l: (State) -> Unit) {
        listeners.remove(l)
    }

    /** 重新评估状态；异常一律退化为「未开启」。 */
    fun refresh() {
        val s = runCatching { evaluate() }.getOrDefault(State.NOT_ENABLED)
        if (s != state) {
            state = s
            mainHandler.post { listeners.forEach { runCatching { it(s) } } }
        }
    }

    private fun evaluate(): State {
        if (InjectorService.instance != null) return State.READY
        val ctx = appContext ?: return State.NOT_ENABLED
        val am = ctx.getSystemService(Context.ACCESSIBILITY_SERVICE) as AccessibilityManager
        val expected = ComponentName(ctx, InjectorService::class.java).flattenToString()
        val enabled = am.getEnabledAccessibilityServiceList(AccessibilityServiceInfo.FEEDBACK_ALL_MASK)
            .any { it.id == expected }
        return if (enabled) State.WAITING else State.NOT_ENABLED
    }

    fun tap(x: Int, y: Int): Boolean =
        InjectorService.instance?.tap(x, y) ?: false

    fun swipe(x1: Int, y1: Int, x2: Int, y2: Int, durationMs: Int): Boolean =
        InjectorService.instance?.swipe(x1, y1, x2, y2, durationMs) ?: false
}
