package com.macroclicker.mobile.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.macroclicker.mobile.R
import com.macroclicker.mobile.model.MacroEvent

/** 事件序列列表适配器：行点击编辑，支持上移/下移/删除。 */
class EventAdapter(
    private var events: List<MacroEvent>,
    private val onEdit: (Int) -> Unit,
    private val onMove: (Int, Int) -> Unit,
    private val onDelete: (Int) -> Unit,
) : RecyclerView.Adapter<EventAdapter.VH>() {

    fun submit(events: List<MacroEvent>) {
        this.events = events
        notifyDataSetChanged()
    }

    class VH(v: View) : RecyclerView.ViewHolder(v) {
        val badge: TextView = v.findViewById(R.id.tvBadge)
        val title: TextView = v.findViewById(R.id.tvTitle)
        val sub: TextView = v.findViewById(R.id.tvSub)
        val up: TextView = v.findViewById(R.id.btnUp)
        val down: TextView = v.findViewById(R.id.btnDown)
        val delete: TextView = v.findViewById(R.id.btnDelete)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH {
        val v = LayoutInflater.from(parent.context).inflate(R.layout.item_event, parent, false)
        return VH(v)
    }

    override fun getItemCount(): Int = events.size

    override fun onBindViewHolder(holder: VH, position: Int) {
        val ev = events[position]
        holder.badge.text = (position + 1).toString()
        holder.title.text = ev.title()
        holder.sub.text = ev.sub()
        holder.itemView.setOnClickListener { onEdit(position) }
        holder.up.setOnClickListener { onMove(position, -1) }
        holder.up.visibility = if (position == 0) View.INVISIBLE else View.VISIBLE
        holder.down.setOnClickListener { onMove(position, 1) }
        holder.down.visibility = if (position == events.size - 1) View.INVISIBLE else View.VISIBLE
        holder.delete.setOnClickListener { onDelete(position) }
    }
}
