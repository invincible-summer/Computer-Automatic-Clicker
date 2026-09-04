package com.macroclicker.mobile.model

import org.json.JSONObject
import java.util.Locale

enum class EventType { TAP, SWIPE, WAIT }

data class MacroEvent(
    var type: EventType = EventType.TAP,
    var delay: Double = 0.3,          // 执行前等待秒数（与桌面端宏格式一致）
    var x: Int = 0,
    var y: Int = 0,                   // TAP 坐标 / SWIPE 起点
    var x2: Int = 0,
    var y2: Int = 0,                  // SWIPE 终点
    var duration: Int = 350,          // SWIPE 时长毫秒
) {
    fun title(): String = when (type) {
        EventType.TAP -> String.format(Locale.CHINA, "点击 · (%d, %d)", x, y)
        EventType.SWIPE -> String.format(Locale.CHINA, "滑动 · (%d, %d) → (%d, %d)", x, y, x2, y2)
        EventType.WAIT -> String.format(Locale.CHINA, "等待 · %.1f 秒", delay)
    }

    fun sub(): String = when (type) {
        EventType.SWIPE -> String.format(Locale.CHINA, "间隔 %.2fs · 时长 %dms", delay, duration)
        else -> String.format(Locale.CHINA, "执行前间隔 %.2f 秒", delay)
    }

    fun toJson(): JSONObject = JSONObject().apply {
        put("type", typeName())
        put("delay", delay)
        if (type != EventType.WAIT) {
            put("x", x)
            put("y", y)
        }
        if (type == EventType.SWIPE) {
            put("x2", x2)
            put("y2", y2)
            put("duration", duration)
        }
    }

    private fun typeName() = when (type) {
        EventType.TAP -> "tap"
        EventType.SWIPE -> "swipe"
        EventType.WAIT -> "wait"
    }

    companion object {
        /** 解析单事件；桌面端宏的 mouse_click / wait / drag 会被映射，其余类型返回 null 跳过。 */
        fun fromJson(o: JSONObject): MacroEvent? {
            val type = o.optString("type", "")
            val delay = o.optDouble("delay", 0.0).coerceAtLeast(0.0)
            return when (type) {
                "tap", "mouse_click" -> MacroEvent(
                    type = EventType.TAP, delay = delay,
                    x = o.optInt("x", 0), y = o.optInt("y", 0)
                )
                "swipe", "drag" -> MacroEvent(
                    type = EventType.SWIPE, delay = delay,
                    x = o.optInt("x", 0), y = o.optInt("y", 0),
                    x2 = o.optInt("x2", o.optInt("x", 0)),
                    y2 = o.optInt("y2", o.optInt("y", 0)),
                    duration = o.optInt("duration", 350)
                )
                "wait" -> MacroEvent(type = EventType.WAIT, delay = delay)
                else -> null
            }
        }
    }
}

data class PlaySettings(
    var loopMode: Int = 0,            // 0 一次 / 1 指定次数 / 2 无限
    var loopCount: Int = 10,
    var loopInterval: Double = 0.0,
    var countdown: Int = 3,
)

data class MacroConfig(
    var name: String = "macro",
    var screenW: Int = 0,             // 保存时的屏幕分辨率，用于跨设备按比例换算坐标
    var screenH: Int = 0,
    var settings: PlaySettings = PlaySettings(),
    var events: MutableList<MacroEvent> = mutableListOf(),
) {
    fun toJson(): JSONObject = JSONObject().apply {
        put("name", name)
        put("version", 1)
        put("screen", JSONObject().apply {
            put("w", screenW)
            put("h", screenH)
        })
        put("settings", JSONObject().apply {
            put("loopMode", settings.loopMode)
            put("loopCount", settings.loopCount)
            put("loopInterval", settings.loopInterval)
            put("countdown", settings.countdown)
        })
        put("events", org.json.JSONArray().apply {
            events.forEach { put(it.toJson()) }
        })
    }

    /** 将坐标从旧屏幕尺寸按比例换算到当前屏幕，兼容不同分辨率机型。 */
    fun rescale(fromW: Int, fromH: Int, toW: Int, toH: Int) {
        if (fromW <= 0 || fromH <= 0 || toW <= 0 || toH <= 0) return
        if (fromW == toW && fromH == toH) return
        val sx = toW.toFloat() / fromW
        val sy = toH.toFloat() / fromH
        events.forEach { ev ->
            ev.x = (ev.x * sx).toInt()
            ev.y = (ev.y * sy).toInt()
            ev.x2 = (ev.x2 * sx).toInt()
            ev.y2 = (ev.y2 * sy).toInt()
        }
    }

    companion object {
        fun fromJson(o: JSONObject): MacroConfig {
            val cfg = MacroConfig(name = o.optString("name", "macro"))
            val screen = o.optJSONObject("screen")
            cfg.screenW = screen?.optInt("w", 0) ?: 0
            cfg.screenH = screen?.optInt("h", 0) ?: 0
            o.optJSONObject("settings")?.let { s ->
                cfg.settings = PlaySettings(
                    loopMode = s.optInt("loopMode", 0).coerceIn(0, 2),
                    loopCount = s.optInt("loopCount", 10).coerceIn(1, 999999),
                    loopInterval = s.optDouble("loopInterval", 0.0).coerceAtLeast(0.0),
                    countdown = s.optInt("countdown", 3).coerceIn(0, 60),
                )
            }
            val arr = o.optJSONArray("events")
            if (arr != null) {
                for (i in 0 until arr.length()) {
                    MacroEvent.fromJson(arr.optJSONObject(i) ?: continue)?.let { cfg.events.add(it) }
                }
            }
            return cfg
        }
    }
}
