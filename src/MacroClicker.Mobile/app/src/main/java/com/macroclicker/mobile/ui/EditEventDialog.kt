package com.macroclicker.mobile.ui

import android.widget.ArrayAdapter
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.android.material.textfield.TextInputEditText
import com.macroclicker.mobile.R
import com.macroclicker.mobile.databinding.DialogEditEventBinding
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroEvent

/**
 * 事件编辑对话框：类型（点击/滑动/长按/等待）切换时动态显示字段；
 * 长按 = 起止点相同的滑动（与桌面端宏格式互通）。
 */
class EditEventDialog(
    private val activity: android.app.Activity,
    private val event: MacroEvent,
    private val onChanged: () -> Unit,
) {

    private enum class UiType { TAP, SWIPE, LONG, WAIT }

    fun show() {
        val binding = DialogEditEventBinding.inflate(activity.layoutInflater)
        val types = listOf(
            activity.getString(R.string.event_type_tap),
            activity.getString(R.string.event_type_swipe),
            activity.getString(R.string.event_type_long),
            activity.getString(R.string.event_type_wait)
        )
        binding.spType.adapter = ArrayAdapter(activity, android.R.layout.simple_spinner_dropdown_item, types)
        val initial = uiTypeOf(event)
        binding.spType.setSelection(initial.ordinal)

        fun applyVisibility(ui: UiType) {
            binding.rowEnd.visibility = if (ui == UiType.SWIPE) android.view.View.VISIBLE else android.view.View.GONE
            binding.tilDuration.visibility =
                if (ui == UiType.SWIPE || ui == UiType.LONG) android.view.View.VISIBLE else android.view.View.GONE
        }

        // 初始值
        binding.etDelay.setText(fmt(event.delay))
        binding.etX.setText(event.x.toString())
        binding.etY.setText(event.y.toString())
        binding.etX2.setText(event.x2.toString())
        binding.etY2.setText(event.y2.toString())
        binding.etDuration.setText(event.duration.toString())
        applyVisibility(initial)
        binding.spType.onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
            override fun onItemSelected(p: android.widget.AdapterView<*>?, v: android.view.View?, pos: Int, id: Long) =
                applyVisibility(UiType.entries[pos])

            override fun onNothingSelected(p: android.widget.AdapterView<*>?) = Unit
        }

        MaterialAlertDialogBuilder(activity)
            .setTitle(R.string.event_type)
            .setView(binding.root)
            .setPositiveButton(R.string.dialog_ok) { _, _ -> apply(binding) }
            .setNegativeButton(R.string.cancel, null)
            .show()
    }

    private fun apply(b: DialogEditEventBinding) {
        val ui = UiType.entries[b.spType.selectedItemPosition]
        event.delay = b.etDelay.text.toString().toDoubleOrNull()?.coerceAtLeast(0.0) ?: 0.0
        when (ui) {
            UiType.TAP -> {
                event.type = EventType.TAP
                event.x = int(b.etX); event.y = int(b.etY)
            }
            UiType.SWIPE -> {
                event.type = EventType.SWIPE
                event.x = int(b.etX); event.y = int(b.etY)
                event.x2 = int(b.etX2); event.y2 = int(b.etY2)
                event.duration = (int(b.etDuration)).coerceIn(50, 60_000)
            }
            UiType.LONG -> {
                event.type = EventType.SWIPE
                event.x = int(b.etX); event.y = int(b.etY)
                event.x2 = event.x; event.y2 = event.y
                event.duration = (int(b.etDuration)).coerceIn(50, 60_000)
            }
            UiType.WAIT -> event.type = EventType.WAIT
        }
        onChanged()
    }

    private fun int(e: TextInputEditText): Int =
        e.text.toString().toDoubleOrNull()?.toInt() ?: 0

    private fun fmt(v: Double): String =
        if (v == v.toLong().toDouble()) v.toLong().toString() else String.format(java.util.Locale.CHINA, "%.2f", v)

    private fun uiTypeOf(ev: MacroEvent): UiType = when {
        ev.type == EventType.TAP -> UiType.TAP
        ev.type == EventType.WAIT -> UiType.WAIT
        ev.isLongPress -> UiType.LONG
        else -> UiType.SWIPE
    }
}
