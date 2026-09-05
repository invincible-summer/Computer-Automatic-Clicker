package com.macroclicker.mobile.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.color.MaterialColors
import com.macroclicker.mobile.R
import com.macroclicker.mobile.databinding.ItemMacroBinding
import com.macroclicker.mobile.store.MacroStore
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 宏库列表：点击卡片设为当前宏；行尾 ⋮ 弹出菜单。
 * 列表小、且行内含「当前宏」高亮（依赖外部状态），用快照 + 全量刷新保证正确性。
 */
class MacrosAdapter(
    private val currentName: () -> String,
    private val onSelect: (String) -> Unit,
    private val onMenu: (String, View) -> Unit,
) : RecyclerView.Adapter<MacrosAdapter.VH>() {

    private val items = mutableListOf<MacroStore.MacroMeta>()

    inner class VH(val binding: ItemMacroBinding) : RecyclerView.ViewHolder(binding.root) {
        init {
            binding.cardMacro.setOnClickListener {
                val pos = bindingAdapterPosition
                if (pos != RecyclerView.NO_POSITION) onSelect(items[pos].name)
            }
            binding.btnMacroMenu.setOnClickListener {
                val pos = bindingAdapterPosition
                if (pos != RecyclerView.NO_POSITION) onMenu(items[pos].name, it)
            }
        }
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH =
        VH(ItemMacroBinding.inflate(LayoutInflater.from(parent.context), parent, false))

    override fun getItemCount(): Int = items.size

    override fun onBindViewHolder(holder: VH, position: Int) {
        val item = items[position]
        val b = holder.binding
        b.tvMacroName.text = item.name
        val time = SimpleDateFormat("MM-dd HH:mm", Locale.CHINA).format(Date(item.modified))
        b.tvMacroMeta.text = b.root.context.getString(R.string.macro_events_count, item.events) + " · " + time

        val isCurrent = item.name == currentName()
        b.badgeCurrent.visibility = if (isCurrent) View.VISIBLE else View.GONE
        val density = b.root.resources.displayMetrics.density
        b.cardMacro.strokeWidth = if (isCurrent) (2 * density).toInt() else 0
        b.cardMacro.strokeColor =
            MaterialColors.getColor(b.cardMacro, com.google.android.material.R.attr.colorPrimary)
    }

    fun submit(newItems: List<MacroStore.MacroMeta>) {
        val snapshot = newItems.toList()
        items.clear()
        items.addAll(snapshot)
        notifyDataSetChanged()
    }
}
