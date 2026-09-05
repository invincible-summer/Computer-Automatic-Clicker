package com.macroclicker.mobile.inject

import android.content.ComponentName
import android.content.Context
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import rikka.shizuku.Shizuku
import java.util.concurrent.CopyOnWriteArraySet

/**
 * 注入引擎管理器（替代 v3 的 ShellExecutor）：
 * Shizuku 状态机（未安装/未运行/版本过旧/未授权/就绪）+ UserService 绑定 +
 * 快速/兼容通道能力探测 + 注入失败重估。
 *
 * - 就绪后 bind() 绑定 UserService（shell uid），连接成功即 probe() 探测能力，
 *   `fastMode` 供 UI 显示「快速注入 / 兼容模式」（实现内部总会自动回落，双保险）。
 * - tap/swipe 可在任意线程同步调用（Binder 调用）；UI 永不调用。
 * - Binder 断开时状态回退并广播，MacroService 据此「失败即停」。
 * - 仅当「尚未连接」导致失败时会重绑一次重试；已连接后的失败不重试，
 *   避免对已生效的事件造成重复注入。
 */
object Injector {

    enum class State { NOT_INSTALLED, NOT_RUNNING, UNSUPPORTED, UNAUTHORIZED, READY }

    /** 注入通道能力（连接 UserService 后探测；null = 尚未探测）。 */
    @Volatile
    var fastMode: Boolean? = null
        private set

    @Volatile
    var state: State = State.NOT_RUNNING
        private set

    @Volatile
    private var service: IInjectorService? = null

    @Volatile
    private var bound = false

    private var appContext: Context? = null
    private val mainHandler = Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArraySet<(State) -> Unit>()

    val userServiceArgs: Shizuku.UserServiceArgs by lazy {
        val ctx = appContext ?: throw IllegalStateException("Injector 未初始化")
        Shizuku.UserServiceArgs(
            ComponentName(ctx.packageName, InjectorServiceImpl::class.java.name)
        )
            .processNameSuffix("injector")
            .version(2) // v4：AIDL 由 exec(String[]) 换为 tap/swipe/probe，必须换新进程
            .tag("macro-injector")
    }

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, svc: IBinder?) {
            service = IInjectorService.Stub.asInterface(svc)
            fastMode = runCatching { service?.probe() == 1 }.getOrNull()
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            service = null
            fastMode = null
        }
    }

    /** Application.onCreate 调用一次；注册 Shizuku binder 生命周期监听。 */
    fun init(context: Context) {
        if (appContext != null) return
        appContext = context.applicationContext
        Shizuku.addBinderReceivedListenerSticky { refresh() }
        Shizuku.addBinderDeadListener { refresh() }
        refresh()
    }

    fun addStateListener(l: (State) -> Unit) {
        listeners.add(l)
        mainHandler.post { l(state) }
    }

    fun removeStateListener(l: (State) -> Unit) {
        listeners.remove(l)
    }

    /** 重新评估状态；任何异常都退化为「未运行」。 */
    fun refresh() {
        val s = runCatching { evaluate() }.getOrDefault(State.NOT_RUNNING)
        if (s != state) {
            state = s
            if (s != State.READY) {
                service = null
                fastMode = null
            }
            mainHandler.post { listeners.forEach { runCatching { it(s) } } }
        }
    }

    private fun evaluate(): State {
        val installed = appContext?.packageManager
            ?.getLaunchIntentForPackage(SHIZUKU_PACKAGE) != null
        if (!Shizuku.pingBinder()) return if (installed) State.NOT_RUNNING else State.NOT_INSTALLED
        if (Shizuku.isPreV11()) return State.UNSUPPORTED
        return if (Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED)
            State.READY else State.UNAUTHORIZED
    }

    /** 就绪时发起授权请求（binder 存活才可调用）。 */
    fun requestPermission() {
        runCatching {
            if (Shizuku.pingBinder() && !Shizuku.isPreV11() &&
                Shizuku.checkSelfPermission() != PackageManager.PERMISSION_GRANTED
            ) Shizuku.requestPermission(REQUEST_CODE)
        }
    }

    /** 绑定 UserService（幂等）；由前台服务在启动/需要注入时调用。 */
    fun bind() {
        refresh()
        if (state != State.READY || bound) return
        runCatching {
            Shizuku.bindUserService(userServiceArgs, connection)
            bound = true
        }.onFailure { refresh() }
    }

    /** 解绑并释放（前台服务销毁时调用）。 */
    fun unbind() {
        if (!bound) return
        runCatching { Shizuku.unbindUserService(userServiceArgs, connection, true) }
        bound = false
        service = null
        fastMode = null
    }

    val isBound: Boolean get() = service != null

    fun tap(x: Int, y: Int): Boolean = call { it.tap(x, y) }

    fun swipe(x1: Int, y1: Int, x2: Int, y2: Int, durationMs: Int): Boolean =
        call { it.swipe(x1, y1, x2, y2, durationMs) }

    /**
     * 同步注入调用。Binder 尚未就位时会重绑一次并重试（此场景不可能重复注入）；
     * 已连接后的任何失败都返回 false 并 refresh（binder 可能已死），由调用方决定停止。
     */
    private fun call(block: (IInjectorService) -> Int): Boolean {
        var s = service
        if (s == null) {
            bind()
            s = service ?: return false
        }
        return try {
            block(s) == 0
        } catch (_: Exception) {
            refresh()
            false
        }
    }

    const val SHIZUKU_PACKAGE = "moe.shizuku.privileged.api"
    const val REQUEST_CODE = 10001
}
