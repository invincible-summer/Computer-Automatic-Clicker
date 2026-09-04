package com.macroclicker.mobile.store

import android.content.Context
import android.graphics.Point
import android.os.Build
import android.view.WindowManager
import com.macroclicker.mobile.model.MacroConfig
import org.json.JSONObject
import java.io.File

/** 配置持久化：filesDir/config.json，格式与桌面端宏文件兼容（附带 screen/settings 扩展字段）。 */
object ConfigStore {

    private const val FILE = "config.json"

    fun screenSize(ctx: Context): Pair<Int, Int> {
        val wm = ctx.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        return if (Build.VERSION.SDK_INT >= 30) {
            val b = wm.currentWindowMetrics.bounds
            b.width() to b.height()
        } else {
            val p = Point()
            @Suppress("DEPRECATION")
            wm.defaultDisplay.getRealSize(p)
            p.x to p.y
        }
    }

    fun load(ctx: Context): MacroConfig {
        return try {
            val f = File(ctx.filesDir, FILE)
            if (!f.exists()) return MacroConfig()
            val cfg = MacroConfig.fromJson(JSONObject(f.readText()))
            // 从其他设备迁移的配置：按保存时的屏幕比例换算到当前屏幕
            val (w, h) = screenSize(ctx)
            if (cfg.screenW > 0 && cfg.screenH > 0 && (cfg.screenW != w || cfg.screenH != h)) {
                cfg.rescale(cfg.screenW, cfg.screenH, w, h)
            }
            cfg.screenW = w
            cfg.screenH = h
            cfg
        } catch (e: Exception) {
            MacroConfig()
        }
    }

    fun save(ctx: Context, config: MacroConfig) {
        try {
            val (w, h) = screenSize(ctx)
            config.screenW = w
            config.screenH = h
            File(ctx.filesDir, FILE).writeText(config.toJson().toString(2))
        } catch (_: Exception) {
        }
    }
}
