package com.macroclicker.mobile.model

import org.json.JSONArray
import org.json.JSONObject
import java.util.Locale

/**
 * 事件模型：与桌面端宏 JSON 同构。
 * 长按 = 起止点相同的滑动（x==x2 && y==y2），序列化后与桌面端模拟器宏完全互通。
 */
enum class EventType { TAP, SWIPE, WAIT }

data class MacroEvent(
    var type: EventType = EventType.TAP,
    var delay: Double = 0.0,          // 执行前等待秒数（与桌面端一致）
    var x: Int = 0,
    var y: Int = 0,                   // TAP 坐标 / SWIPE 起点
    var x2: Int = 0,
    var y2: Int = 0,                  // SWIPE 终点
    var duration: Int = 300,          // SWIPE/长按 时长毫秒
) {
    val isLongPress: Boolean
        get() = type == EventType.SWIPE && x == x2 && y == y2

    fun title(): String = when {
        type == EventType.TAP -> String.format(Locale.CHINA, "点击 · (%d, %d)", x, y)
        isLongPress -> String.format(Locale.CHINA, "长按 %.1f 秒", duration / 1000.0)
        type == EventType.SWIPE -> String.format(Locale.CHINA, "滑动 · (%d,%d)→(%d,%d)", x, y, x2, y2)
        else -> String.format(Locale.CHINA, "等待 %.1f 秒", delay)
    }

    fun sub(): String = when (type) {
        EventType.SWIPE -> String.format(Locale.CHINA, "间隔 %.2fs · 时长 %dms", delay, duration)
        EventType.WAIT -> "停留倒计"
        else -> String.format(Locale.CHINA, "间隔 %.2f 秒后执行", delay)
    }

    fun toJson(): JSONObject = JSONObject().apply {
        put("type", typeName())
        put("delay", round2(delay))
        when (type) {
            EventType.TAP -> {
                put("x", x)
                put("y", y)
            }
            EventType.SWIPE -> {
                put("x", x)
                put("y", y)
                put("x2", x2)
                put("y2", y2)
                put("duration", duration)
            }
            EventType.WAIT -> Unit
        }
    }

    private fun typeName() = when (type) {
        EventType.TAP -> "tap"
        EventType.SWIPE -> "swipe"
        EventType.WAIT -> "wait"
    }

    companion object {
        private fun round2(v: Double) = Math.round(v * 100.0) / 100.0

        fun tap(x: Int, y: Int, delay: Double) =
            MacroEvent(type = EventType.TAP, delay = delay, x = x, y = y)

        fun longPress(x: Int, y: Int, delay: Double, durationMs: Int) =
            MacroEvent(type = EventType.SWIPE, delay = delay, x = x, y = y, x2 = x, y2 = y, duration = durationMs)

        fun swipe(x: Int, y: Int, x2: Int, y2: Int, delay: Double, durationMs: Int) =
            MacroEvent(type = EventType.SWIPE, delay = delay, x = x, y = y, x2 = x2, y2 = y2, duration = durationMs)

        fun wait(seconds: Double) =
            MacroEvent(type = EventType.WAIT, delay = seconds)

        /** 解析单事件；兼容桌面端宏（mouse_click → tap、wait 保留，其余类型跳过返回 null）。 */
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
                    duration = o.optInt("duration", 300).coerceIn(50, 60_000)
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
    var countdown: Int = 0,
) {
    fun toJson(): JSONObject = JSONObject().apply {
        put("loopMode", loopMode)
        put("loopCount", loopCount)
        put("loopInterval", loopInterval)
        put("countdown", countdown)
    }

    companion object {
        fun fromJson(o: JSONObject?) = PlaySettings(
            loopMode = (o?.optInt("loopMode", 0) ?: 0).coerceIn(0, 2),
            loopCount = (o?.optInt("loopCount", 10) ?: 10).coerceIn(1, 999_999),
            loopInterval = (o?.optDouble("loopInterval", 0.0) ?: 0.0).coerceAtLeast(0.0),
            countdown = (o?.optInt("countdown", 0) ?: 0).coerceIn(0, 60),
        )
    }
}

data class MacroConfig(
    var name: String = "宏 1",
    var screenW: Int = 0,             // 保存时的屏幕分辨率，用于跨设备按比例换算
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
        put("settings", settings.toJson())
        put("events", JSONArray().apply { events.forEach { put(it.toJson()) } })
    }

    /** 坐标从旧屏幕尺寸按比例换算到当前屏幕，兼容不同分辨率机型。 */
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
            val cfg = MacroConfig(name = o.optString("name", "宏 1"))
            val screen = o.optJSONObject("screen")
            cfg.screenW = screen?.optInt("w", 0) ?: 0
            cfg.screenH = screen?.optInt("h", 0) ?: 0
            cfg.settings = PlaySettings.fromJson(o.optJSONObject("settings"))
            val arr = o.optJSONArray("events") ?: JSONArray()
            for (i in 0 until arr.length()) {
                arr.optJSONObject(i)?.let { MacroEvent.fromJson(it)?.let(cfg.events::add) }
            }
            return cfg
        }
    }
}
