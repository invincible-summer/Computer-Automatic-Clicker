package com.macroclicker.mobile.store

import android.content.Context
import android.graphics.Point
import android.os.Build
import android.view.WindowManager
import com.macroclicker.mobile.model.MacroConfig
import org.json.JSONObject
import java.io.File

/**
 * 多宏持久化：filesDir/macros/<名称>.json + SharedPreferences 记录当前宏。
 * 加载时若保存分辨率与当前屏幕不同，自动按比例换算坐标（跨机型适配）。
 */
object MacroStore {

    private const val PREFS = "macro_store"
    private const val KEY_CURRENT = "current_macro"
    private const val KEY_LIVE_REPLAY = "live_replay"
    private const val KEY_BALL = "ball_enabled"

    fun macrosDir(ctx: Context): File =
        File(ctx.filesDir, "macros").apply { mkdirs() }

    fun screenSize(ctx: Context): Pair<Int, Int> {
        val wm = ctx.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        return if (Build.VERSION.SDK_INT >= 30) {
            val b = wm.currentWindowMetrics.bounds
            b.width() to b.height()
        } else {
            @Suppress("DEPRECATION")
            val p = Point().also { wm.defaultDisplay.getRealSize(it) }
            p.x to p.y
        }
    }

    fun list(ctx: Context): List<String> =
        macrosDir(ctx).listFiles { _, name -> name.endsWith(".json") }
            ?.sortedByDescending { it.lastModified() }
            ?.map { it.nameWithoutExtension }
            ?: emptyList()

    fun exists(ctx: Context, name: String): Boolean =
        fileOf(ctx, name).exists()

    private fun fileOf(ctx: Context, name: String) = File(macrosDir(ctx), sanitize(name) + ".json")

    fun sanitize(name: String): String =
        name.trim().replace(Regex("[\\\\/:*?\"<>|]"), "_").ifEmpty { "宏" }

    fun load(ctx: Context, name: String): MacroConfig? {
        return try {
            val f = fileOf(ctx, name)
            if (!f.exists()) return null
            val cfg = MacroConfig.fromJson(JSONObject(f.readText()))
            cfg.name = name
            val (w, h) = screenSize(ctx)
            if (cfg.screenW > 0 && cfg.screenH > 0 && (cfg.screenW != w || cfg.screenH != h)) {
                cfg.rescale(cfg.screenW, cfg.screenH, w, h)
            }
            cfg
        } catch (_: Exception) {
            null
        }
    }

    fun save(ctx: Context, cfg: MacroConfig): Boolean {
        return try {
            val (w, h) = screenSize(ctx)
            cfg.screenW = w
            cfg.screenH = h
            fileOf(ctx, cfg.name).writeText(cfg.toJson().toString(2))
            true
        } catch (_: Exception) {
            false
        }
    }

    fun delete(ctx: Context, name: String) {
        fileOf(ctx, name).delete()
    }

    /** 重命名；目标名已存在时失败。 */
    fun rename(ctx: Context, old: String, new: String): Boolean {
        val src = fileOf(ctx, old)
        val dst = fileOf(ctx, new)
        if (!src.exists() || dst.exists()) return false
        val ok = src.renameTo(dst)
        if (ok && currentName(ctx) == old) setCurrentName(ctx, new)
        return ok
    }

    fun currentName(ctx: Context): String =
        prefs(ctx).getString(KEY_CURRENT, null) ?: "宏 1"

    fun setCurrentName(ctx: Context, name: String) {
        prefs(ctx).edit().putString(KEY_CURRENT, name).apply()
    }

    /** 当前宏；不存在（首次/被删）时回退到最近修改的宏，避免产生“宏 1”幽灵文件。 */
    fun loadCurrent(ctx: Context): MacroConfig {
        val name = currentName(ctx)
        load(ctx, name)?.let { return it }
        val fallback = list(ctx).firstOrNull()
            ?: return MacroConfig(name = "宏 1")
        setCurrentName(ctx, fallback)
        return load(ctx, fallback) ?: MacroConfig(name = fallback)
    }

    fun liveReplay(ctx: Context): Boolean =
        prefs(ctx).getBoolean(KEY_LIVE_REPLAY, true)

    fun setLiveReplay(ctx: Context, value: Boolean) {
        prefs(ctx).edit().putBoolean(KEY_LIVE_REPLAY, value).apply()
    }

    /** 悬浮球常驻（前台服务保活）开关；关闭时会话结束即停止服务。 */
    fun ballEnabled(ctx: Context): Boolean =
        prefs(ctx).getBoolean(KEY_BALL, false)

    fun setBallEnabled(ctx: Context, value: Boolean) {
        prefs(ctx).edit().putBoolean(KEY_BALL, value).apply()
    }

    private fun prefs(ctx: Context) = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
