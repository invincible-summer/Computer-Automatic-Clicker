package com.macroclicker.mobile.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.View
import android.widget.EditText
import android.widget.PopupMenu
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.NotificationManagerCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
import androidx.recyclerview.widget.LinearLayoutManager
import com.google.android.material.color.MaterialColors
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.macroclicker.mobile.R
import com.macroclicker.mobile.databinding.ActivityMainBinding
import com.macroclicker.mobile.inject.Injector
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroConfig
import com.macroclicker.mobile.model.MacroEvent
import com.macroclicker.mobile.service.MacroService
import com.macroclicker.mobile.store.MacroStore
import rikka.shizuku.Shizuku
import java.util.Locale

/**
 * 主界面（Material 3 底栏五页）：宏 / 编辑 / 录制 / 执行 / 设置。
 *
 * v4：宏库独立成页（列表 + 导入导出），编辑页专注当前宏事件序列；
 * 注入经 Shizuku（ADB shell，快速路径 + input 兜底），不依赖无障碍服务。
 * 边到边：状态栏/挖孔→工具栏与底栏、横屏侧边→内容区、输入法→内容区；
 * 预测性返回经 manifest 的 enableOnBackInvokedCallback 开启（无自定义回退逻辑）。
 */
class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var macrosAdapter: MacrosAdapter
    private lateinit var eventsAdapter: EventsAdapter
    private var config: MacroConfig = MacroConfig()
    private var wasBusy = false

    /** 服务异步拉起后待执行的动作（开始录制/执行）。 */
    private var pendingAction: (() -> Unit)? = null

    private val stateListener: () -> Unit = {
        if (!MacroService.isPlaying && !MacroService.isRecording && wasBusy) {
            // 录制/执行刚结束：重载当前宏（录制会替换事件）
            reloadConfig()
        }
        wasBusy = MacroService.isPlaying || MacroService.isRecording
        if (MacroService.isRunning) {
            pendingAction?.let { it() }
            pendingAction = null
        }
        refreshDynamicState()
    }

    private val injectorListener: (Injector.State) -> Unit = { refreshPermUi() }

    private val permResultListener = Shizuku.OnRequestPermissionResultListener { reqCode, _ ->
        if (reqCode == Injector.REQUEST_CODE) {
            Injector.refresh()
            refreshPermUi()
        }
    }

    private val importLauncher =
        registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
            uri?.let { handleImport(it) }
        }

    private val exportLauncher =
        registerForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri ->
            uri?.let { handleExport(it) }
        }

    private val notifPermLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { refreshPermUi() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        // 边到边：工具栏吃状态栏+挖孔顶，底栏吃系统栏底，内容区吃横屏侧边与输入法
        WindowCompat.enableEdgeToEdge(window)
        ViewCompat.setOnApplyWindowInsetsListener(binding.root) { _, insets ->
            val bars = insets.getInsets(
                WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            val ime = insets.getInsets(WindowInsetsCompat.Type.ime())
            binding.toolbar.updatePadding(top = bars.top)
            binding.bottomNav.updatePadding(bottom = bars.bottom)
            binding.pageContainer.updatePadding(left = bars.left, right = bars.right, bottom = ime.bottom)
            WindowInsetsCompat.CONSUMED
        }

        // 宏库列表
        macrosAdapter = MacrosAdapter(
            currentName = { config.name },
            onSelect = { name -> if (name != config.name) switchMacro(name) },
            onMenu = { name, anchor -> showMacroMenu(name, anchor) },
        )
        binding.recyclerMacros.layoutManager = LinearLayoutManager(this)
        binding.recyclerMacros.adapter = macrosAdapter

        // 事件列表
        eventsAdapter = EventsAdapter(
            onEdit = { pos -> EditEventDialog(this, config.events[pos]) { persistAndRefresh() }.show() },
            onMove = { pos, delta -> moveEvent(pos, delta) },
            onDelete = { pos -> deleteEvent(pos) },
        )
        binding.recyclerEvents.layoutManager = LinearLayoutManager(this)
        binding.recyclerEvents.adapter = eventsAdapter

        // 底栏导航
        binding.bottomNav.setOnItemSelectedListener { item ->
            switchTab(item.itemId)
            true
        }

        // 宏库页
        binding.btnGuide.setOnClickListener { switchTab(R.id.tab_settings) }
        binding.btnNewMacro.setOnClickListener { promptName(null) { newMacro(it) } }
        binding.btnImport.setOnClickListener { importLauncher.launch(arrayOf("*/*")) }

        // 编辑页
        binding.btnAddEvent.setOnClickListener { addEvent() }
        binding.btnClearEvents.setOnClickListener { confirmClearEvents() }

        // 录制页
        binding.swLiveReplay.isChecked = MacroStore.liveReplay(this)
        binding.swLiveReplay.setOnCheckedChangeListener { _, checked ->
            MacroStore.setLiveReplay(this, checked)
        }
        binding.btnRecord.setOnClickListener { startRecordingFlow() }

        // 执行页
        binding.groupMode.addOnButtonCheckedListener { _, _, _ -> applyModeVisibility() }
        binding.btnPlay.setOnClickListener { onPlayClicked() }

        // 设置页
        binding.btnOverlay.setOnClickListener { requestOverlay() }
        binding.btnShizuku.setOnClickListener { onShizukuButton() }
        binding.btnShizukuHelp.setOnClickListener {
            MaterialAlertDialogBuilder(this)
                .setTitle(R.string.shell_help)
                .setMessage(R.string.shell_tutorial)
                .setPositiveButton(R.string.dialog_ok, null)
                .show()
        }
        binding.swBall.isChecked = MacroStore.ballEnabled(this)
        binding.swBall.setOnCheckedChangeListener { _, checked ->
            MacroStore.setBallEnabled(this, checked)
            if (checked) MacroService.ensureStarted(this)
            else MacroService.stopIfIdle(this)
        }
        binding.tvAbout.text = getString(R.string.about_body, versionName())

        // Android 13+ 通知权限（前台服务通知展示；拒绝不影响功能）
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notifPermLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        Shizuku.addRequestPermissionResultListener(permResultListener)
        Injector.addStateListener(injectorListener)

        switchTab(R.id.tab_macros)
    }

    override fun onResume() {
        super.onResume()
        reloadConfig()
        MacroService.addStateListener(stateListener)
        Injector.refresh()
        stateListener()
    }

    override fun onPause() {
        super.onPause()
        MacroService.removeStateListener(stateListener)
        persistSettings()
    }

    override fun onDestroy() {
        super.onDestroy()
        Injector.removeStateListener(injectorListener)
        Shizuku.removeRequestPermissionResultListener(permResultListener)
    }

    // ---------------- 页面切换 ----------------

    private fun switchTab(id: Int) {
        persistSettings() // 离开页前把设置快照落盘（尤其执行页的输入框）
        binding.pageMacros.visibility = if (id == R.id.tab_macros) View.VISIBLE else View.GONE
        binding.pageEdit.visibility = if (id == R.id.tab_edit) View.VISIBLE else View.GONE
        binding.pageRecord.visibility = if (id == R.id.tab_record) View.VISIBLE else View.GONE
        binding.pagePlay.visibility = if (id == R.id.tab_play) View.VISIBLE else View.GONE
        binding.pageSettings.visibility = if (id == R.id.tab_settings) View.VISIBLE else View.GONE
        binding.toolbar.title = when (id) {
            R.id.tab_edit -> getString(R.string.tab_edit)
            R.id.tab_record -> getString(R.string.tab_record)
            R.id.tab_play -> getString(R.string.tab_play)
            R.id.tab_settings -> getString(R.string.tab_settings)
            else -> getString(R.string.tab_macros)
        }
    }

    // ---------------- 刷新 ----------------

    private fun reloadConfig() {
        persistPending()
        config = MacroStore.loadCurrent(this)
        loadSettingsToUi()
        refreshEvents()
        refreshMacroList()
        refreshPermUi()
        refreshDynamicState()
    }

    /** 旧 config 引用的未保存修改（设置项）在重载前落盘。 */
    private fun persistPending() {
        if (!::binding.isInitialized) return
        readSettingsFromUi()
        MacroStore.save(this, config)
    }

    private fun refreshPermUi() {
        if (!::binding.isInitialized) return
        val overlayOk = Settings.canDrawOverlays(this)
        binding.tvOverlayState.setText(if (overlayOk) R.string.perm_enabled else R.string.perm_disabled)
        binding.btnOverlay.isEnabled = !overlayOk
        binding.btnOverlay.setText(if (overlayOk) R.string.perm_enabled else R.string.perm_go)

        val notifOk = NotificationManagerCompat.from(this).areNotificationsEnabled()
        binding.tvNotifState.setText(if (notifOk) R.string.perm_enabled else R.string.perm_notif_off)

        val state = Injector.state
        val stateText = when (state) {
            Injector.State.NOT_INSTALLED -> R.string.shell_not_installed
            Injector.State.NOT_RUNNING -> R.string.shell_not_running
            Injector.State.UNSUPPORTED -> R.string.shell_unsupported
            Injector.State.UNAUTHORIZED -> R.string.shell_unauthorized
            Injector.State.READY -> R.string.shell_ready
        }
        val btnText = when (state) {
            Injector.State.NOT_INSTALLED, Injector.State.UNSUPPORTED -> R.string.shell_install
            Injector.State.NOT_RUNNING -> R.string.shell_open
            Injector.State.UNAUTHORIZED -> R.string.shell_authorize
            Injector.State.READY -> R.string.shell_ready_btn
        }
        binding.tvShizukuState.setText(stateText)
        binding.btnShizuku.setText(btnText)
        binding.btnShizuku.isEnabled = state != Injector.State.READY

        // 注入引擎模式（设置页 + 执行页徽章 + 录制页提示）
        val engineRes = when {
            state != Injector.State.READY -> R.string.engine_unknown
            Injector.fastMode == true -> R.string.engine_fast
            else -> R.string.engine_compat
        }
        binding.tvEngineMode.setText(engineRes)
        binding.tvEngineBadge.setText(engineRes)
        binding.tvShellState.setText(
            if (state == Injector.State.READY) R.string.record_live_ready
            else R.string.record_live_needs_shell
        )

        // 宏库页引导卡：悬浮窗或 Shizuku 未就绪时显示
        val missing = buildList {
            if (!overlayOk) add(getString(R.string.perm_overlay))
            if (state != Injector.State.READY) add(getString(stateText))
        }
        binding.cardGuide.visibility = if (missing.isEmpty()) View.GONE else View.VISIBLE
        binding.tvGuide.text = getString(R.string.guide_title) + "：" +
                missing.joinToString("；")
    }

    private fun refreshMacroList() {
        val items = MacroStore.listMeta(this)
        macrosAdapter.submitList(items)
        binding.tvMacrosEmpty.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
    }

    private fun refreshEvents() {
        eventsAdapter.submit(config.events)
        binding.tvEventsEmpty.visibility = if (config.events.isEmpty()) View.VISIBLE else View.GONE
        binding.tvEditName.text = config.name
        binding.tvEditStats.text =
            getString(R.string.edit_stats, config.events.size, estimateSeconds(config.events))
        binding.tvPlayMacro.text =
            "${config.name} · ${getString(R.string.macro_events_count, config.events.size)}"
    }

    private fun refreshDynamicState() {
        if (!::binding.isInitialized) return
        if (MacroService.isPlaying) {
            binding.btnPlay.text = getString(R.string.play_stop)
            binding.btnPlay.backgroundTintList = ColorStateList.valueOf(
                MaterialColors.getColor(binding.btnPlay, com.google.android.material.R.attr.colorError))
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

    /** 一轮的预计时长（各事件等待 + 滑动时长；不含注入耗时）。 */
    private fun estimateSeconds(events: List<MacroEvent>): String {
        var s = 0.0
        events.forEach {
            s += it.delay
            if (it.type == EventType.SWIPE) s += it.duration / 1000.0
        }
        return if (s < 60) String.format(Locale.CHINA, "%.1f 秒", s)
        else String.format(Locale.CHINA, "%d 分 %.0f 秒", (s / 60).toInt(), s % 60)
    }

    // ---------------- 宏管理 ----------------

    private fun showMacroMenu(name: String, anchor: View) {
        val popup = PopupMenu(this, anchor)
        popup.menu.add(0, 1, 0, R.string.macro_use)
        popup.menu.add(0, 2, 1, R.string.macro_rename)
        popup.menu.add(0, 3, 2, R.string.macro_duplicate)
        popup.menu.add(0, 4, 3, R.string.macro_export)
        popup.menu.add(0, 5, 4, R.string.macro_delete)
        popup.setOnMenuItemClickListener { item ->
            when (item.itemId) {
                1 -> if (name != config.name) switchMacro(name)
                2 -> promptName(name) { renameMacro(it) }
                3 -> duplicateMacro(name)
                4 -> exportLauncher.launch(
                    name.replace(Regex("[\\\\/:*?\"<>|]"), "_") + ".json")
                5 -> confirmDeleteMacro(name)
            }
            true
        }
        popup.show()
    }

    private fun switchMacro(name: String) {
        persistSettings()
        config = MacroStore.load(this, name) ?: MacroConfig(name = name)
        MacroStore.setCurrentName(this, name)
        loadSettingsToUi()
        refreshEvents()
        refreshMacroList()
    }

    private fun promptName(initial: String?, onOk: (String) -> Unit) {
        val input = EditText(this).apply {
            hint = getString(R.string.macro_name_hint)
            initial?.let { setText(it); setSelection(text.length) }
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
        loadSettingsToUi()
        refreshEvents()
        refreshMacroList()
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
            refreshEvents()
            refreshMacroList()
            toast(R.string.toast_macro_saved)
        }
    }

    private fun duplicateMacro(name: String) {
        val copyName = name + " 副本"
        if (MacroStore.exists(this, copyName)) {
            toast(R.string.toast_macro_exists)
            return
        }
        val cfg = MacroStore.load(this, name) ?: return
        persistSettings()
        config = cfg.copy(name = copyName, events = cfg.events.map { it.copy() }.toMutableList())
        MacroStore.save(this, config)
        MacroStore.setCurrentName(this, copyName)
        loadSettingsToUi()
        refreshEvents()
        refreshMacroList()
        toast(R.string.toast_macro_saved)
    }

    private fun confirmDeleteMacro(name: String) {
        MaterialAlertDialogBuilder(this)
            .setMessage(R.string.confirm_delete_macro)
            .setPositiveButton(R.string.dialog_ok) { _, _ -> deleteMacro(name) }
            .setNegativeButton(R.string.cancel, null)
            .show()
    }

    private fun deleteMacro(name: String) {
        MacroStore.delete(this, name)
        val remaining = MacroStore.list(this)
        // 必须从磁盘真正加载剩余宏内容，否则空 config 会覆盖其文件（v2 数据丢失 bug 教训）
        config = if (remaining.isNotEmpty()) {
            MacroStore.setCurrentName(this, remaining.first())
            MacroStore.loadCurrent(this)
        } else {
            MacroConfig(name = "宏 1")
        }
        loadSettingsToUi()
        refreshEvents()
        refreshMacroList()
    }

    // ---------------- 导入 / 导出（SAF） ----------------

    private fun handleImport(uri: Uri) {
        val text = try {
            contentResolver.openInputStream(uri)?.use { it.readBytes().decodeToString() }
        } catch (_: Exception) {
            null
        }
        if (text.isNullOrBlank()) {
            toast(R.string.toast_import_failed)
            return
        }
        val cfg = MacroStore.importJson(this, text)
        if (cfg == null) {
            toast(R.string.toast_import_failed)
        } else {
            reloadConfig()
            toast(getString(R.string.toast_import_done, cfg.name, cfg.events.size))
        }
    }

    private fun handleExport(uri: Uri) {
        persistSettings()
        val ok = MacroStore.exportTo(this, config, uri)
        toast(if (ok) R.string.toast_export_done else R.string.toast_export_failed)
    }

    // ---------------- 事件操作 ----------------

    private fun addEvent() {
        val (w, h) = MacroStore.screenSize(this)
        config.events.add(MacroEvent.tap(w / 2, (h * 2 / 5), 0.5))
        persistAndRefresh()
        toast(R.string.toast_event_added)
    }

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

    private fun confirmClearEvents() {
        if (config.events.isEmpty()) return
        MaterialAlertDialogBuilder(this)
            .setMessage(R.string.confirm_clear_events)
            .setPositiveButton(R.string.dialog_ok) { _, _ ->
                config.events.clear()
                persistAndRefresh()
            }
            .setNegativeButton(R.string.cancel, null)
            .show()
    }

    private fun persistAndRefresh() {
        MacroStore.save(this, config)
        refreshEvents()
    }

    // ---------------- 设置读写 ----------------

    private fun applyModeVisibility() {
        binding.tilCount.visibility =
            if (binding.groupMode.checkedButtonId == R.id.btnModeCount) View.VISIBLE else View.GONE
    }

    private fun loadSettingsToUi() {
        val s = config.settings
        val btn = when (s.loopMode) {
            1 -> binding.btnModeCount
            2 -> binding.btnModeLoop
            else -> binding.btnModeOnce
        }
        btn.isChecked = true
        applyModeVisibility()
        binding.etLoopCount.setText(s.loopCount.toString())
        binding.etLoopInterval.setText(fmtNum(s.loopInterval))
        binding.etCountdown.setText(s.countdown.toString())
    }

    private fun readSettingsFromUi() {
        val s = config.settings
        s.loopMode = when (binding.groupMode.checkedButtonId) {
            R.id.btnModeCount -> 1
            R.id.btnModeLoop -> 2
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

    /** 确保前台服务已启动后执行动作（服务异步拉起时挂起待办）。 */
    private fun withService(action: (MacroService) -> Unit) {
        val svc = MacroService.instance
        if (svc != null) {
            action(svc)
        } else {
            pendingAction = { MacroService.instance?.let(action) }
            MacroService.ensureStarted(this)
        }
    }

    private fun startRecordingFlow() {
        when {
            MacroService.isRecording -> toast(R.string.rec_short)
            !Settings.canDrawOverlays(this) -> {
                toast(R.string.toast_need_overlay)
                requestOverlay()
            }
            else -> {
                persistSettings()
                val live = binding.swLiveReplay.isChecked
                MacroStore.setLiveReplay(this, live)
                withService { it.startRecording(live) }
                moveTaskToBack(true)
            }
        }
    }

    private fun onPlayClicked() {
        when {
            MacroService.isPlaying -> MacroService.stopAll()
            config.events.isEmpty() -> toast(R.string.toast_no_events)
            Injector.state != Injector.State.READY -> {
                toast(R.string.shell_not_ready_short)
                switchTab(R.id.tab_settings)
            }
            else -> {
                persistSettings()
                withService { it.startPlayback(config) }
                refreshDynamicState()
            }
        }
    }

    // ---------------- Shizuku ----------------

    private fun onShizukuButton() {
        Injector.refresh()
        when (Injector.state) {
            Injector.State.NOT_INSTALLED, Injector.State.UNSUPPORTED ->
                openUrl("https://shizuku.rikka.app/download/")
            Injector.State.NOT_RUNNING -> {
                val launch = packageManager.getLaunchIntentForPackage(Injector.SHIZUKU_PACKAGE)
                if (launch != null) startActivity(launch)
                else openUrl("https://shizuku.rikka.app/download/")
            }
            Injector.State.UNAUTHORIZED -> Injector.requestPermission()
            Injector.State.READY -> Unit
        }
        refreshPermUi()
    }

    private fun openUrl(url: String) {
        runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }
    }

    // ---------------- 通用 ----------------

    private fun toast(res: Int) = Toast.makeText(this, res, Toast.LENGTH_SHORT).show()

    private fun fmtNum(v: Double): String =
        if (v == v.toLong().toDouble()) v.toLong().toString() else v.toString()

    private fun versionName(): String = try {
        packageManager.getPackageInfo(packageName, 0).versionName ?: "?"
    } catch (_: Exception) {
        "?"
    }
}
