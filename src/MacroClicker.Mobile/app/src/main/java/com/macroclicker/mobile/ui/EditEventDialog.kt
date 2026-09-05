package com.macroclicker.mobile.ui

import android.view.View
import com.google.android.material.button.MaterialButtonToggleGroup
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.android.material.textfield.TextInputEditText
import com.macroclicker.mobile.R
import com.macroclicker.mobile.databinding.DialogEditEventBinding
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroEvent
import java.util.Locale

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
        val initial = uiTypeOf(event)

        fun applyVisibility(ui: UiType) {
            binding.rowEnd.visibility = if (ui == UiType.SWIPE) View.VISIBLE else View.GONE
            binding.tilDuration.visibility =
                if (ui == UiType.SWIPE || ui == UiType.LONG) View.VISIBLE else View.GONE
            binding.etX.isEnabled = ui != UiType.WAIT
            binding.etY.isEnabled = ui != UiType.WAIT
        }

        // 初始值与类型选择
        val initialBtn = when (initial) {
            UiType.TAP -> binding.btnTypeTap
            UiType.SWIPE -> binding.btnTypeSwipe
            UiType.LONG -> binding.btnTypeLong
            UiType.WAIT -> binding.btnTypeWait
        }
        initialBtn.isChecked = true
        binding.etDelay.setText(fmt(event.delay))
        binding.etX.setText(event.x.toString())
        binding.etY.setText(event.y.toString())
        binding.etX2.setText(event.x2.toString())
        binding.etY2.setText(event.y2.toString())
        binding.etDuration.setText(event.duration.toString())
        applyVisibility(initial)
        binding.groupType.addOnButtonCheckedListener { _, _, _ ->
            applyVisibility(uiTypeOf(binding.groupType))
        }

        MaterialAlertDialogBuilder(activity)
            .setTitle(R.string.event_edit_title)
            .setView(binding.root)
            .setPositiveButton(R.string.dialog_ok) { _, _ -> apply(binding) }
            .setNegativeButton(R.string.cancel, null)
            .show()
    }

    private fun uiTypeOf(group: MaterialButtonToggleGroup): UiType = when (group.checkedButtonId) {
        R.id.btnTypeTap -> UiType.TAP
        R.id.btnTypeSwipe -> UiType.SWIPE
        R.id.btnTypeLong -> UiType.LONG
        R.id.btnTypeWait -> UiType.WAIT
        else -> UiType.TAP
    }

    private fun apply(b: DialogEditEventBinding) {
        val ui = uiTypeOf(b.groupType)
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
                event.duration = int(b.etDuration).coerceIn(50, 60_000)
            }
            UiType.LONG -> {
                event.type = EventType.SWIPE
                event.x = int(b.etX); event.y = int(b.etY)
                event.x2 = event.x; event.y2 = event.y
                event.duration = int(b.etDuration).coerceIn(50, 60_000)
            }
            UiType.WAIT -> event.type = EventType.WAIT
        }
        onChanged()
    }

    private fun int(e: TextInputEditText): Int =
        e.text.toString().toDoubleOrNull()?.toInt() ?: 0

    private fun fmt(v: Double): String =
        if (v == v.toLong().toDouble()) v.toLong().toString()
        else String.format(Locale.CHINA, "%.2f", v)

    private fun uiTypeOf(ev: MacroEvent): UiType = when {
        ev.type == EventType.TAP -> UiType.TAP
        ev.type == EventType.WAIT -> UiType.WAIT
        ev.isLongPress -> UiType.LONG
        else -> UiType.SWIPE
    }
}
