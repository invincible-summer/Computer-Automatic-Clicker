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
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
import androidx.recyclerview.widget.LinearLayoutManager
import com.google.android.material.color.MaterialColors
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.macroclicker.mobile.R
import com.macroclicker.mobile.databinding.ActivityMainBinding
import com.macroclicker.mobile.model.MacroConfig
import com.macroclicker.mobile.model.MacroEvent
import com.macroclicker.mobile.service.MacroService
import com.macroclicker.mobile.shell.ShellExecutor
import com.macroclicker.mobile.store.MacroStore
import rikka.shizuku.Shizuku

/**
 * 主界面（Material 3 底栏四页）：宏 / 录制 / 执行 / 设置。
 *
 * v3.0：注入不再依赖无障碍服务——由 Shizuku（ADB shell）执行；
 * 设置页承载 Shizuku 三步引导与悬浮窗/悬浮球开关。
 * 布局全部 dp/sp + 权重 + 嵌套滚动，适配不同尺寸机型；跟随系统深浅色与动态取色。
 */
class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var adapter: EventsAdapter
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

    private val shellListener: (ShellExecutor.State) -> Unit = { refreshPermUi() }

    private val permResultListener = Shizuku.OnRequestPermissionResultListener { reqCode, _ ->
        if (reqCode == ShellExecutor.REQUEST_CODE) {
            ShellExecutor.refresh()
            refreshPermUi()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        // 边到边布局：根吃状态栏 inset，底栏吃导航栏 inset，内容区吃输入法 inset
        ViewCompat.setOnApplyWindowInsetsListener(binding.root) { _, insets ->
            val sys = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            val ime = insets.getInsets(WindowInsetsCompat.Type.ime())
            binding.root.updatePadding(top = sys.top)
            binding.bottomNav.updatePadding(bottom = sys.bottom)
            binding.pageContainer.updatePadding(bottom = ime.bottom)
            WindowInsetsCompat.CONSUMED
        }

        adapter = EventsAdapter(
            events = config.events,
            onEdit = { pos -> EditEventDialog(this, config.events[pos]) { persistAndRefresh() }.show() },
            onMove = { pos, delta -> moveEvent(pos, delta) },
            onDelete = { pos -> deleteEvent(pos) }
        )
        binding.recycler.layoutManager = LinearLayoutManager(this)
        binding.recycler.adapter = adapter

        // 底栏导航
        binding.bottomNav.setOnItemSelectedListener { item ->
            switchTab(item.itemId)
            true
        }

        // 宏管理
        binding.btnSwitchMacro.setOnClickListener { showMacroPicker() }
        binding.btnNewMacro.setOnClickListener { promptName(null) { newMacro(it) } }
        binding.btnRename.setOnClickListener { promptName(config.name) { renameMacro(it) } }
        binding.btnDuplicate.setOnClickListener { duplicateMacro() }
        binding.btnDeleteMacro.setOnClickListener { confirmDeleteMacro() }
        binding.btnAddEvent.setOnClickListener { addEvent() }
        binding.btnClearEvents.setOnClickListener { confirmClearEvents() }
        binding.btnGuide.setOnClickListener { switchTab(R.id.tab_settings) }

        // 录制
        binding.swLiveReplay.isChecked = MacroStore.liveReplay(this)
        binding.swLiveReplay.setOnCheckedChangeListener { _, checked ->
            MacroStore.setLiveReplay(this, checked)
        }
        binding.btnRecord.setOnClickListener { startRecordingFlow() }

        // 执行设置
        binding.chipMode.setOnCheckedStateChangeListener { _, checkedIds ->
            binding.tilCount.visibility =
                if (checkedIds.contains(R.id.chipCount)) View.VISIBLE else View.GONE
        }
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
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }

        Shizuku.addRequestPermissionResultListener(permResultListener)
        ShellExecutor.addStateListener(shellListener)

        switchTab(R.id.tab_macro)
    }

    override fun onResume() {
        super.onResume()
        reloadConfig()
        MacroService.addStateListener(stateListener)
        ShellExecutor.refresh()
        stateListener()
    }

    override fun onPause() {
        super.onPause()
        MacroService.removeStateListener(stateListener)
        persistSettings()
    }

    override fun onDestroy() {
        super.onDestroy()
        ShellExecutor.removeStateListener(shellListener)
        Shizuku.removeRequestPermissionResultListener(permResultListener)
    }

    // ---------------- 页面切换 ----------------

    private fun switchTab(id: Int) {
        persistSettings() // 离开页前把设置快照落盘（尤其执行页的输入框）
        binding.pageMacro.visibility = if (id == R.id.tab_macro) View.VISIBLE else View.GONE
        binding.pageRecord.visibility = if (id == R.id.tab_record) View.VISIBLE else View.GONE
        binding.pagePlay.visibility = if (id == R.id.tab_play) View.VISIBLE else View.GONE
        binding.pageSettings.visibility = if (id == R.id.tab_settings) View.VISIBLE else View.GONE
        binding.toolbar.title = when (id) {
            R.id.tab_record -> getString(R.string.tab_record)
            R.id.tab_play -> getString(R.string.tab_play)
            R.id.tab_settings -> getString(R.string.tab_settings)
            else -> getString(R.string.tab_macro)
        }
    }

    // ---------------- 刷新 ----------------

    private fun reloadConfig() {
        persistPending()
        config = MacroStore.loadCurrent(this)
        loadSettingsToUi()
        refreshEvents()
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

        val state = ShellExecutor.state
        val (stateText, btnText) = when (state) {
            ShellExecutor.State.NOT_INSTALLED -> R.string.shell_not_installed to R.string.shell_install
            ShellExecutor.State.NOT_RUNNING -> R.string.shell_not_running to R.string.shell_open
            ShellExecutor.State.UNSUPPORTED -> R.string.shell_unsupported to R.string.shell_install
            ShellExecutor.State.UNAUTHORIZED -> R.string.shell_unauthorized to R.string.shell_authorize
            ShellExecutor.State.READY -> R.string.shell_ready to R.string.shell_ready_btn
        }
        binding.tvShizukuState.setText(stateText)
        binding.btnShizuku.setText(btnText)
        binding.btnShizuku.isEnabled = state != ShellExecutor.State.READY

        // 宏页引导卡：悬浮窗或 Shizuku 未就绪时显示
        val missing = buildList {
            if (!overlayOk) add(getString(R.string.perm_overlay))
            if (state != ShellExecutor.State.READY) add(getString(stateText))
        }
        binding.cardGuide.visibility = if (missing.isEmpty()) View.GONE else View.VISIBLE
        binding.tvGuide.text = getString(R.string.guide_title) + "：" +
                missing.joinToString("；")

        // 录制页提示行
        binding.tvShellHint.setText(
            if (state == ShellExecutor.State.READY) R.string.record_live_replay
            else R.string.record_live_needs_shell
        )
    }

    private fun refreshEvents() {
        adapter.submit(config.events)
        binding.tvEmpty.visibility = if (config.events.isEmpty()) View.VISIBLE else View.GONE
        binding.tvEventCount.text =
            getString(R.string.event_count, config.events.size)
        binding.tvMacroName.text = config.name
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

    // ---------------- 宏管理 ----------------

    private fun showMacroPicker() {
        val names = MacroStore.list(this)
        if (names.isEmpty()) {
            promptName(null) { newMacro(it) }
            return
        }
        val checked = names.indexOf(config.name)
        MaterialAlertDialogBuilder(this)
            .setTitle(R.string.current_macro)
            .setSingleChoiceItems(names.toTypedArray(), checked) { dlg, which ->
                dlg.dismiss()
                if (names[which] != config.name) switchMacro(names[which])
            }
            .setNegativeButton(R.string.cancel, null)
            .show()
    }

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
            refreshEvents()
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
        config = config.copy(
            name = copyName,
            events = config.events.map { it.copy() }.toMutableList()
        )
        MacroStore.save(this, config)
        MacroStore.setCurrentName(this, copyName)
        refreshEvents()
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
        // 必须从磁盘真正加载剩余宏内容，否则空 config 会覆盖其文件（旧版数据丢失 bug）
        config = if (remaining.isNotEmpty()) {
            MacroStore.setCurrentName(this, remaining.first())
            MacroStore.loadCurrent(this)
        } else {
            MacroConfig(name = "宏 1")
        }
        loadSettingsToUi()
        refreshEvents()
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
            ShellExecutor.state != ShellExecutor.State.READY -> {
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
        ShellExecutor.refresh()
        when (ShellExecutor.state) {
            ShellExecutor.State.NOT_INSTALLED, ShellExecutor.State.UNSUPPORTED ->
                openUrl("https://shizuku.rikka.app/download/")
            ShellExecutor.State.NOT_RUNNING -> {
                val launch = packageManager.getLaunchIntentForPackage(ShellExecutor.SHIZUKU_PACKAGE)
                if (launch != null) startActivity(launch)
                else openUrl("https://shizuku.rikka.app/download/")
            }
            ShellExecutor.State.UNAUTHORIZED -> ShellExecutor.requestPermission()
            ShellExecutor.State.READY -> Unit
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
