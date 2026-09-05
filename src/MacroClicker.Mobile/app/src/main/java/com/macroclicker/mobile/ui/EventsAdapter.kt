package com.macroclicker.mobile.ui

import android.graphics.drawable.GradientDrawable
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.color.MaterialColors
import com.macroclicker.mobile.databinding.ItemEventBinding
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroEvent

/** 事件列表：点击行编辑；行尾上移/下移/删除。 */
class EventsAdapter(
    private val events: MutableList<MacroEvent>,
    private val onEdit: (Int) -> Unit,
    private val onMove: (Int, Int) -> Unit,
    private val onDelete: (Int) -> Unit,
) : RecyclerView.Adapter<EventsAdapter.VH>() {

    inner class VH(val binding: ItemEventBinding) : RecyclerView.ViewHolder(binding.root) {
        init {
            binding.root.setOnClickListener {
                val pos = bindingAdapterPosition
                if (pos != RecyclerView.NO_POSITION) onEdit(pos)
            }
        }
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH =
        VH(ItemEventBinding.inflate(LayoutInflater.from(parent.context), parent, false))

    override fun getItemCount(): Int = events.size

    override fun onBindViewHolder(holder: VH, position: Int) {
        val ev = events[position]
        val b = holder.binding
        b.tvTitle.text = "${position + 1}. ${ev.title()}"
        b.tvSub.text = ev.sub()
        b.tvIcon.text = iconOf(ev)
        b.tvIcon.setTextColor(MaterialColors.getColor(b.root, com.google.android.material.R.attr.colorPrimary))
        b.tvIcon.background = roundedBg(b.root, MaterialColors.getColor(
            b.root, com.google.android.material.R.attr.colorSecondaryContainer))

        b.btnUp.setOnClickListener {
            val pos = holder.bindingAdapterPosition
            if (pos != RecyclerView.NO_POSITION) onMove(pos, -1)
        }
        b.btnDown.setOnClickListener {
            val pos = holder.bindingAdapterPosition
            if (pos != RecyclerView.NO_POSITION) onMove(pos, +1)
        }
        b.btnDelete.setOnClickListener {
            val pos = holder.bindingAdapterPosition
            if (pos != RecyclerView.NO_POSITION) onDelete(pos)
        }
    }

    private fun iconOf(ev: MacroEvent): String = when {
        ev.type == EventType.TAP -> "👆"
        ev.isLongPress -> "⏱"
        ev.type == EventType.SWIPE -> "↔"
        else -> "⏳"
    }

    private fun roundedBg(view: View, color: Int) = GradientDrawable().apply {
        cornerRadius = 12f * view.resources.displayMetrics.density
        setColor(color)
    }

    fun submit(newEvents: List<MacroEvent>) {
        events.clear()
        events.addAll(newEvents)
        notifyDataSetChanged()
    }
}
