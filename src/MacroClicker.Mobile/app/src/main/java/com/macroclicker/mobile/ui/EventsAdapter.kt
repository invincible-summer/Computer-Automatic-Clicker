package com.macroclicker.mobile.ui

import android.view.LayoutInflater
import android.view.ViewGroup
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.color.MaterialColors
import com.macroclicker.mobile.R
import com.macroclicker.mobile.databinding.ItemEventBinding
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroEvent

/**
 * 事件列表：点击行编辑；行尾上移/下移/删除。
 * 行标题含序号（位置敏感），调用方持有的又是同一列表引用（config.events），
 * 因此沿用「先快照再全量刷新」——既防 v2 自引用清空 bug，又保证序号正确。
 */
class EventsAdapter(
    private val onEdit: (Int) -> Unit,
    private val onMove: (Int, Int) -> Unit,
    private val onDelete: (Int) -> Unit,
) : RecyclerView.Adapter<EventsAdapter.VH>() {

    private val events = mutableListOf<MacroEvent>()

    inner class VH(val binding: ItemEventBinding) : RecyclerView.ViewHolder(binding.root) {
        init {
            binding.root.setOnClickListener {
                val pos = bindingAdapterPosition
                if (pos != RecyclerView.NO_POSITION) onEdit(pos)
            }
            binding.btnUp.setOnClickListener {
                val pos = bindingAdapterPosition
                if (pos != RecyclerView.NO_POSITION) onMove(pos, -1)
            }
            binding.btnDown.setOnClickListener {
                val pos = bindingAdapterPosition
                if (pos != RecyclerView.NO_POSITION) onMove(pos, +1)
            }
            binding.btnDelete.setOnClickListener {
                val pos = bindingAdapterPosition
                if (pos != RecyclerView.NO_POSITION) onDelete(pos)
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
        b.ivIcon.setImageResource(iconOf(ev))
        b.ivIcon.setColorFilter(MaterialColors.getColor(
            b.root, com.google.android.material.R.attr.colorPrimary))
        b.btnDelete.setColorFilter(MaterialColors.getColor(
            b.root, com.google.android.material.R.attr.colorError))
    }

    private fun iconOf(ev: MacroEvent): Int = when {
        ev.type == EventType.TAP -> R.drawable.ic_ev_tap
        ev.isLongPress -> R.drawable.ic_ev_long
        ev.type == EventType.SWIPE -> R.drawable.ic_ev_swipe
        else -> R.drawable.ic_ev_wait
    }

    fun submit(newEvents: List<MacroEvent>) {
        val snapshot = newEvents.toList()
        events.clear()
        events.addAll(snapshot)
        notifyDataSetChanged()
    }
}
