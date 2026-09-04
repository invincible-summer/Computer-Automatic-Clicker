package com.macroclicker.mobile.ui

import android.app.Activity
import android.view.View
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.app.AlertDialog
import com.macroclicker.mobile.R
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroEvent

/** 事件编辑对话框：按事件类型显示对应字段，保存时直接写回事件对象。 */
class EditEventDialog(
    private val activity: Activity,
    private val event: MacroEvent,
    private val onSaved: () -> Unit,
) {

    fun show() {
        val view = activity.layoutInflater.inflate(R.layout.dialog_edit_event, null)
        val title = view.findViewById<TextView>(R.id.tvTitle)
        val etDelay = view.findViewById<EditText>(R.id.etDelay)
        val boxTap = view.findViewById<LinearLayout>(R.id.boxTap)
        val boxSwipe = view.findViewById<LinearLayout>(R.id.boxSwipe)

        title.text = activity.getString(
            when (event.type) {
                EventType.TAP -> R.string.edit_title_tap
                EventType.SWIPE -> R.string.edit_title_swipe
                EventType.WAIT -> R.string.edit_title_wait
            }
        )
        boxTap.visibility = if (event.type == EventType.TAP) View.VISIBLE else View.GONE
        boxSwipe.visibility = if (event.type == EventType.SWIPE) View.VISIBLE else View.GONE

        etDelay.setText(fmt(event.delay))
        view.findViewById<EditText>(R.id.etX).setText(event.x.toString())
        view.findViewById<EditText>(R.id.etY).setText(event.y.toString())
        view.findViewById<EditText>(R.id.etX1).setText(event.x.toString())
        view.findViewById<EditText>(R.id.etY1).setText(event.y.toString())
        view.findViewById<EditText>(R.id.etX2).setText(event.x2.toString())
        view.findViewById<EditText>(R.id.etY2).setText(event.y2.toString())
        view.findViewById<EditText>(R.id.etDuration).setText(event.duration.toString())

        val dialog = AlertDialog.Builder(activity)
            .setView(view)
            .create()

        view.findViewById<TextView>(R.id.btnCancel).setOnClickListener { dialog.dismiss() }
        view.findViewById<TextView>(R.id.btnSave).setOnClickListener {
            event.delay = etDelay.text.toString().toDoubleOrNull()?.coerceIn(0.0, 86400.0) ?: 0.0
            when (event.type) {
                EventType.TAP -> {
                    event.x = readInt(view, R.id.etX)
                    event.y = readInt(view, R.id.etY)
                }
                EventType.SWIPE -> {
                    event.x = readInt(view, R.id.etX1)
                    event.y = readInt(view, R.id.etY1)
                    event.x2 = readInt(view, R.id.etX2)
                    event.y2 = readInt(view, R.id.etY2)
                    event.duration = etDuration(view)
                }
                EventType.WAIT -> Unit
            }
            dialog.dismiss()
            onSaved()
        }

        dialog.show()
    }

    private fun readInt(view: View, id: Int): Int =
        view.findViewById<EditText>(id).text.toString().toIntOrNull()?.coerceIn(0, 99999) ?: 0

    private fun etDuration(view: View): Int =
        view.findViewById<EditText>(R.id.etDuration).text.toString().toIntOrNull()
            ?.coerceIn(50, 60_000) ?: 350

    private fun fmt(v: Double): String =
        if (v == v.toLong().toDouble()) v.toLong().toString() else v.toString()
}
