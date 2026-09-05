package com.macroclicker.mobile.shell

import java.util.concurrent.TimeUnit

/**
 * IShellService 实现：运行在 Shizuku 启动的 shell uid 进程中。
 * 只接受调用方拼装好的固定 argv（/system/bin/input tap|swipe + 数字坐标），
 * 不经任何 shell 解释器，天然无命令注入面。
 */
class ShellServiceImpl : IShellService.Stub() {

    override fun exec(cmd: Array<String>): Int {
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
            // 命令输出极小（input 通常无输出）；退出后读取避免残留
            runCatching { p.inputStream.readBytes() }
            p.exitValue()
        } catch (_: Exception) {
            -1
        }
    }

    override fun destroy() {
        System.exit(0)
    }

    companion object {
        private const val TIMEOUT_S = 15L
    }
}
