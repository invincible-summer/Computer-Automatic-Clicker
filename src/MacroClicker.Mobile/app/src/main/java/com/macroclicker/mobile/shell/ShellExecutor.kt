package com.macroclicker.mobile.shell

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
 * Shizuku 注入后端状态机：
 * 未安装 / 未运行 / 版本过旧 / 未授权 / 就绪。
 *
 * bind() 在就绪后绑定 UserService（shell uid），tap/swipe 生成固定 argv 数组
 * 调用 /system/bin/input —— 无 shell 字符串拼接，无注入风险。
 * 所有状态变化回调都在主线程；exec 允许任意线程（Binder 同步调用）。
 */
object ShellExecutor {

    enum class State { NOT_INSTALLED, NOT_RUNNING, UNSUPPORTED, UNAUTHORIZED, READY }

    const val SHIZUKU_PACKAGE = "moe.shizuku.privileged.api"
    const val REQUEST_CODE = 10001

    @Volatile
    var state: State = State.NOT_RUNNING
        private set

    @Volatile
    private var shell: IShellService? = null

    @Volatile
    private var bound = false

    private var appContext: Context? = null
    private val mainHandler = Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArraySet<(State) -> Unit>()

    private val userServiceArgs: Shizuku.UserServiceArgs by lazy {
        val ctx = appContext ?: throw IllegalStateException("ShellExecutor 未初始化")
        Shizuku.UserServiceArgs(
            ComponentName(ctx.packageName, ShellServiceImpl::class.java.name)
        )
            .processNameSuffix("shell")
            .version(1)
            .tag("macro-shell")
    }

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, service: IBinder?) {
            shell = IShellService.Stub.asInterface(service)
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            shell = null
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
            if (s != State.READY) shell = null
            mainHandler.post { listeners.forEach { runCatching { it(s) } } }
        }
    }

    private fun evaluate(): State {
        val installed = appContext?.packageManager?.getLaunchIntentForPackage(SHIZUKU_PACKAGE) != null
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
        shell = null
    }

    val isBound: Boolean get() = shell != null

    fun tap(x: Int, y: Int): Boolean =
        exec(arrayOf(INPUT_BIN, "tap", x.toString(), y.toString()))

    fun swipe(x1: Int, y1: Int, x2: Int, y2: Int, durationMs: Int): Boolean =
        exec(arrayOf(INPUT_BIN, "swipe",
            x1.toString(), y1.toString(), x2.toString(), y2.toString(), durationMs.toString()))

    /** 同步执行一条命令；失败时刷新状态（binder 可能已死）。 */
    fun exec(cmd: Array<String>): Boolean = try {
        val s = shell ?: return false
        s.exec(cmd) == 0
    } catch (_: Exception) {
        refresh()
        false
    }

    private const val INPUT_BIN = "/system/bin/input"
}
