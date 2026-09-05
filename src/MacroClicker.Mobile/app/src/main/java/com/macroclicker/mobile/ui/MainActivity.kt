package com.macroclicker.mobile.ui

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.View
import android.widget.ArrayAdapter
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.recyclerview.widget.LinearLayoutManager
import com.google.android.material.color.MaterialColors
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.macroclicker.mobile.R
import com.macroclicker.mobile.databinding.ActivityMainBinding
import com.macroclicker.mobile.model.MacroConfig
import com.macroclicker.mobile.service.MacroService
import com.macroclicker.mobile.store.MacroStore

/**
 * 主界面：权限引导 → 多宏管理 → 完整动作录制 → 循环执行。
 * 全部布局使用 dp/sp + 权重 + 嵌套滚动，适配不同尺寸机型；跟随系统深浅色与动态取色。
 */
class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var adapter: EventsAdapter
    private var config: MacroConfig = MacroConfig()
    private var macroNames: List<String> = emptyList()
    private var suppressSpinner = false
    private var wasBusy = false

    private val stateListener: () -> Unit = {
        if (!MacroService.isPlaying && !MacroService.isRecording && wasBusy) {
            // 录制/执行刚结束：重载当前宏（录制会替换事件）
            reloadConfig()
        }
        wasBusy = MacroService.isPlaying || MacroService.isRecording
        refreshDynamicState()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        // 边到边布局：系统栏 inset 作为根内边距
        ViewCompat.setOnApplyWindowInsetsListener(binding.root) { v, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            v.setPadding(0, bars.top, 0, bars.bottom)
            insets
        }

        adapter = EventsAdapter(
            events = config.events,
            onEdit = { pos -> EditEventDialog(this, config.events[pos]) { persistAndRefresh() }.show() },
            onMove = { pos, delta -> moveEvent(pos, delta) },
            onDelete = { pos -> deleteEvent(pos) }
        )
        binding.recycler.layoutManager = LinearLayoutManager(this)
        binding.recycler.adapter = adapter

        // 权限
        binding.btnAcc.setOnClickListener { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) }
        binding.btnOverlay.setOnClickListener { requestOverlay() }

        // 宏管理
        binding.spMacro.onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
            override fun onItemSelected(p: android.widget.AdapterView<*>?, v: View?, pos: Int, id: Long) {
                if (suppressSpinner) return
                val name = macroNames.getOrNull(pos) ?: return
                switchMacro(name)
            }

            override fun onNothingSelected(p: android.widget.AdapterView<*>?) = Unit
        }
        binding.btnNewMacro.setOnClickListener { promptName(null) { name -> newMacro(name) } }
        binding.btnRename.setOnClickListener { promptName(config.name) { name -> renameMacro(name) } }
        binding.btnDuplicate.setOnClickListener { duplicateMacro() }
        binding.btnDeleteMacro.setOnClickListener { confirmDeleteMacro() }

        // 录制
        binding.swLiveReplay.isChecked = MacroStore.liveReplay(this)
        binding.swLiveReplay.setOnCheckedChangeListener { _, checked ->
            MacroStore.setLiveReplay(this, checked)
        }
        binding.btnRecord.setOnClickListener { startRecording() }

        // 执行设置
        binding.chipMode.setOnCheckedStateChangeListener { _, checkedIds ->
            binding.tilCount.visibility =
                if (checkedIds.contains(R.id.chipCount)) View.VISIBLE else View.GONE
        }
        binding.btnPlay.setOnClickListener { onPlayClicked() }
    }

    override fun onResume() {
        super.onResume()
        reloadConfig()
        MacroService.addStateListener(stateListener)
        stateListener()
    }

    override fun onPause() {
        super.onPause()
        MacroService.removeStateListener(stateListener)
        persistSettings()
    }

    // ---------------- 刷新 ----------------

    private fun reloadConfig() {
        persistPending()
        config = MacroStore.loadCurrent(this)
        refreshSpinner()
        loadSettingsToUi()
        refreshEvents()
        refreshPermCard()
        refreshDynamicState()
    }

    /** 旧 config 引用的未保存修改（设置项）在重载前落盘。 */
    private fun persistPending() {
        if (!::binding.isInitialized) return
        readSettingsFromUi()
        MacroStore.save(this, config)
    }

    private fun refreshPermCard() {
        val accOk = MacroService.isReady
        val overlayOk = Settings.canDrawOverlays(this)
        binding.tvAccState.text = getString(if (accOk) R.string.perm_enabled else R.string.perm_disabled)
        binding.tvOverlayState.text = getString(if (overlayOk) R.string.perm_enabled else R.string.perm_disabled)
        binding.cardPerm.visibility = if (accOk && overlayOk) View.GONE else View.VISIBLE
    }

    private fun refreshSpinner() {
        macroNames = MacroStore.list(this)
        suppressSpinner = true
        val spinAdapter = ArrayAdapter(this, android.R.layout.simple_spinner_item, macroNames)
        spinAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
        binding.spMacro.adapter = spinAdapter
        val idx = macroNames.indexOf(config.name)
        if (idx >= 0) binding.spMacro.setSelection(idx)
        suppressSpinner = false
    }

    private fun refreshEvents() {
        adapter.submit(config.events)
        binding.tvEmpty.visibility = if (config.events.isEmpty()) View.VISIBLE else View.GONE
    }

    private fun refreshDynamicState() {
        if (!::binding.isInitialized) return
        if (MacroService.isPlaying) {
            binding.btnPlay.text = getString(R.string.play_stop)
            binding.btnPlay.backgroundTintList = android.content.res.ColorStateList.valueOf(
                MaterialColors.getColor(binding.btnPlay, com.google.android.material.R.attr.colorError)
            )
        } else {
            binding.btnPlay.text = getString(R.string.play_start)
            binding.btnPlay.backgroundTintList = null
        }
        if (MacroService.isRecording) {
            binding.btnRecord.isEnabled = false
            binding.btnRecord.text = getString(R.string.rec_short)
        } else {
            binding.btnRecord.isEnabled = true
            binding.btnRecord.text = getString(R.string.record_start)
        }
    }

    // ---------------- 宏管理 ----------------

    private fun switchMacro(name: String) {
        persistSettings()
        config = MacroStore.load(this, name) ?: MacroConfig(name = name)
        MacroStore.setCurrentName(this, name)
        loadSettingsToUi()
        refreshEvents()
    }

    private fun promptName(initial: String?, onOk: (String) -> Unit) {
        val input = android.widget.EditText(this).apply {
            hint = getString(R.string.macro_name_hint)
            initial?.let { setText(it) }
            setSelection(text.length)
        }
        MaterialAlertDialogBuilder(this)
            .setTitle(R.string.macro_name_hint)
            .setView(input)
            .setPositiveButton(R.string.dialog_ok) { _, _ ->
                val name = input.text.toString().trim()
                if (name.isEmpty()) {
                    toast(R.string.toast_macro_empty_name)
                } else onOk(name)
            }
            .setNegativeButton(R.string.cancel, null)
            .show()
    }

    private fun newMacro(name: String) {
        if (MacroStore.exists(this, name)) {
            toast(R.string.toast_macro_exists)
            return
        }
        persistSettings()
        config = MacroConfig(name = name)
        MacroStore.save(this, config)
        MacroStore.setCurrentName(this, name)
        refreshSpinner()
        loadSettingsToUi()
        refreshEvents()
        toast(R.string.toast_macro_saved)
    }

    private fun renameMacro(name: String) {
        if (name == config.name) return
        if (MacroStore.exists(this, name)) {
            toast(R.string.toast_macro_exists)
            return
        }
        if (MacroStore.rename(this, config.name, name)) {
            config.name = name
            refreshSpinner()
            toast(R.string.toast_macro_saved)
        }
    }

    private fun duplicateMacro() {
        val copyName = config.name + " 副本"
        if (MacroStore.exists(this, copyName)) {
            toast(R.string.toast_macro_exists)
            return
        }
        persistSettings()
        val copy = config.copy(name = copyName, events = config.events.map { it.copy() }.toMutableList())
        config = copy
        MacroStore.save(this, copy)
        MacroStore.setCurrentName(this, copyName)
        refreshSpinner()
        toast(R.string.toast_macro_saved)
    }

    private fun confirmDeleteMacro() {
        MaterialAlertDialogBuilder(this)
            .setMessage(R.string.confirm_delete_macro)
            .setPositiveButton(R.string.dialog_ok) { _, _ -> deleteMacro() }
            .setNegativeButton(R.string.cancel, null)
            .show()
    }

    private fun deleteMacro() {
        MacroStore.delete(this, config.name)
        val remaining = MacroStore.list(this)
        config = if (remaining.isNotEmpty()) MacroConfig(name = remaining.first()) else MacroConfig()
        MacroStore.setCurrentName(this, config.name)
        refreshSpinner()
        refreshEvents()
    }

    // ---------------- 事件操作 ----------------

    private fun moveEvent(pos: Int, delta: Int) {
        val to = pos + delta
        if (to < 0 || to >= config.events.size) return
        config.events[pos] = config.events[to].also { config.events[to] = config.events[pos] }
        persistAndRefresh()
    }

    private fun deleteEvent(pos: Int) {
        config.events.removeAt(pos)
        persistAndRefresh()
    }

    private fun persistAndRefresh() {
        MacroStore.save(this, config)
        refreshEvents()
    }

    // ---------------- 设置读写 ----------------

    private fun loadSettingsToUi() {
        val s = config.settings
        val chip = when (s.loopMode) {
            1 -> binding.chipCount
            2 -> binding.chipLoop
            else -> binding.chipOnce
        }
        chip.isChecked = true
        binding.tilCount.visibility = if (s.loopMode == 1) View.VISIBLE else View.GONE
        binding.etLoopCount.setText(s.loopCount.toString())
        binding.etLoopInterval.setText(fmtNum(s.loopInterval))
        binding.etCountdown.setText(s.countdown.toString())
    }

    private fun readSettingsFromUi() {
        val s = config.settings
        s.loopMode = when (binding.chipMode.checkedChipId) {
            R.id.chipCount -> 1
            R.id.chipLoop -> 2
            else -> 0
        }
        s.loopCount = binding.etLoopCount.text.toString().toIntOrNull()?.coerceIn(1, 999_999) ?: s.loopCount
        s.loopInterval = binding.etLoopInterval.text.toString().toDoubleOrNull()?.coerceAtLeast(0.0) ?: s.loopInterval
        s.countdown = binding.etCountdown.text.toString().toIntOrNull()?.coerceIn(0, 60) ?: s.countdown
    }

    private fun persistSettings() {
        if (!::binding.isInitialized) return
        readSettingsFromUi()
        MacroStore.save(this, config)
    }

    // ---------------- 录制 / 执行 ----------------

    private fun requestOverlay() {
        if (!Settings.canDrawOverlays(this)) {
            startActivity(
                Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))
            )
        }
    }

    private fun startRecording() {
        when {
            !MacroService.isReady -> {
                toast(R.string.toast_need_accessibility)
                startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            }
            !Settings.canDrawOverlays(this) -> {
                toast(R.string.toast_need_overlay)
                requestOverlay()
            }
            MacroService.isPlaying -> toast(R.string.playing_short)
            else -> {
                persistSettings()
                MacroStore.setLiveReplay(this, binding.swLiveReplay.isChecked)
                MacroService.instance?.startRecording(binding.swLiveReplay.isChecked)
                moveTaskToBack(true)
            }
        }
    }

    private fun onPlayClicked() {
        when {
            MacroService.isPlaying -> MacroService.stopAll()
            !MacroService.isReady -> {
                toast(R.string.toast_need_accessibility)
                startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            }
            !Settings.canDrawOverlays(this) -> {
                toast(R.string.toast_need_overlay)
                requestOverlay()
            }
            config.events.isEmpty() -> toast(R.string.toast_no_events)
            else -> {
                persistSettings()
                MacroService.instance?.startPlayback(config)
                refreshDynamicState()
            }
        }
    }

    private fun toast(res: Int) = Toast.makeText(this, res, Toast.LENGTH_SHORT).show()

    private fun fmtNum(v: Double): String =
        if (v == v.toLong().toDouble()) v.toLong().toString() else v.toString()
}
