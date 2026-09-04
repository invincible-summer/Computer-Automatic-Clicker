package com.macroclicker.mobile.ui

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.view.View
import android.widget.ArrayAdapter
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.button.MaterialButton
import com.macroclicker.mobile.R
import com.macroclicker.mobile.model.EventType
import com.macroclicker.mobile.model.MacroConfig
import com.macroclicker.mobile.model.MacroEvent
import com.macroclicker.mobile.overlay.PickOverlay
import com.macroclicker.mobile.service.ClickService
import com.macroclicker.mobile.store.ConfigStore
import org.json.JSONObject

/**
 * 主界面：权限引导 → 屏幕取点生成事件序列 → 配置执行模式 → 循环回放。
 * 设计思路与桌面端一致（配置 → 序列编辑 → 循环执行）。
 */
class MainActivity : AppCompatActivity() {

    private lateinit var config: MacroConfig
    private lateinit var adapter: EventAdapter

    private lateinit var tvAccStatus: TextView
    private lateinit var tvOverlayStatus: TextView
    private lateinit var tvEmpty: TextView
    private lateinit var recycler: RecyclerView
    private lateinit var spinnerMode: Spinner
    private lateinit var etLoopCount: EditText
    private lateinit var etLoopInterval: EditText
    private lateinit var etCountdown: EditText
    private lateinit var btnPlay: MaterialButton

    private val importLauncher =
        registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
            if (uri != null) importFrom(uri)
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        config = ConfigStore.load(this)

        tvAccStatus = findViewById(R.id.tvAccStatus)
        tvOverlayStatus = findViewById(R.id.tvOverlayStatus)
        tvEmpty = findViewById(R.id.tvEmpty)
        recycler = findViewById(R.id.recyclerEvents)
        spinnerMode = findViewById(R.id.spinnerMode)
        etLoopCount = findViewById(R.id.etLoopCount)
        etLoopInterval = findViewById(R.id.etLoopInterval)
        etCountdown = findViewById(R.id.etCountdown)
        btnPlay = findViewById(R.id.btnPlay)

        adapter = EventAdapter(
            config.events,
            onEdit = { pos -> EditEventDialog(this, config.events[pos]) { persist(); refreshList() }.show() },
            onMove = { pos, delta -> move(pos, delta) },
            onDelete = { pos ->
                config.events.removeAt(pos)
                persist(); refreshList()
            }
        )
        recycler.layoutManager = LinearLayoutManager(this)
        recycler.adapter = adapter

        val modes = listOf(
            getString(R.string.mode_once),
            getString(R.string.mode_count),
            getString(R.string.mode_loop)
        )
        spinnerMode.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_item, modes).apply {
            setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
        }
        spinnerMode.onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
            override fun onItemSelected(p: android.widget.AdapterView<*>?, v: View?, pos: Int, id: Long) {
                config.settings.loopMode = pos
                persist()
            }

            override fun onNothingSelected(p: android.widget.AdapterView<*>?) = Unit
        }

        val persistWatcher = { _: CharSequence? -> persist() }
        etLoopCount.addTextChangedListener(simpleWatcher(persistWatcher))
        etLoopInterval.addTextChangedListener(simpleWatcher(persistWatcher))
        etCountdown.addTextChangedListener(simpleWatcher(persistWatcher))

        findViewById<LinearLayout>(R.id.rowAccessibility).setOnClickListener {
            startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }
        findViewById<LinearLayout>(R.id.rowOverlay).setOnClickListener {
            if (!Settings.canDrawOverlays(this)) {
                startActivity(
                    Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))
                )
            }
        }

        findViewById<MaterialButton>(R.id.btnAddTap).setOnClickListener {
            startPick(PickOverlay.Mode.TAP)
        }
        findViewById<MaterialButton>(R.id.btnAddSwipe).setOnClickListener {
            startPick(PickOverlay.Mode.SWIPE)
        }
        findViewById<MaterialButton>(R.id.btnAddWait).setOnClickListener {
            config.events.add(MacroEvent(type = EventType.WAIT, delay = 1.0))
            persist(); refreshList()
            EditEventDialog(this, config.events.last()) { persist(); refreshList() }.show()
        }
        findViewById<TextView>(R.id.btnImport).setOnClickListener {
            importLauncher.launch(arrayOf("application/json", "text/plain"))
        }
        findViewById<TextView>(R.id.btnClear).setOnClickListener {
            if (config.events.isEmpty()) return@setOnClickListener
            androidx.appcompat.app.AlertDialog.Builder(this)
                .setMessage(R.string.confirm_clear)
                .setPositiveButton(R.string.dialog_ok) { _, _ ->
                    config.events.clear(); persist(); refreshList()
                }
                .setNegativeButton(R.string.cancel, null)
                .show()
        }
        btnPlay.setOnClickListener { onPlayClicked() }

        loadSettingsToUi()
        refreshAll()
    }

    override fun onResume() {
        super.onResume()
        // 取点/悬浮面板可能修改了配置，回来时重新加载
        config = ConfigStore.load(this)
        loadSettingsToUi()
        refreshAll()
    }

    // ---------------- 状态刷新 ----------------

    private fun refreshAll() {
        val accOk = ClickService.isReady
        val overlayOk = Settings.canDrawOverlays(this)

        tvAccStatus.text = getString(if (accOk) R.string.status_enabled else R.string.status_disabled)
        tvAccStatus.setTextColor(
            ContextCompat.getColor(this, if (accOk) R.color.success else R.color.danger)
        )
        tvOverlayStatus.text = getString(if (overlayOk) R.string.status_granted else R.string.status_denied)
        tvOverlayStatus.setTextColor(
            ContextCompat.getColor(this, if (overlayOk) R.color.success else R.color.danger)
        )

        refreshList()

        if (ClickService.isPlaying) {
            btnPlay.text = getString(R.string.stop_play)
            btnPlay.backgroundTintList = ContextCompat.getColorStateList(this, R.color.danger)
        } else {
            btnPlay.text = getString(R.string.start_play)
            btnPlay.backgroundTintList = ContextCompat.getColorStateList(this, R.color.success)
        }
    }

    private fun refreshList() {
        adapter.submit(config.events)
        tvEmpty.visibility = if (config.events.isEmpty()) View.VISIBLE else View.GONE
        recycler.visibility = if (config.events.isEmpty()) View.GONE else View.VISIBLE
    }

    private fun loadSettingsToUi() {
        if (spinnerMode.selectedItemPosition != config.settings.loopMode) {
            spinnerMode.setSelection(config.settings.loopMode)
        }
        if (etLoopCount.text.toString().toIntOrNull() != config.settings.loopCount) {
            etLoopCount.setText(config.settings.loopCount.toString())
        }
        val intervalText = etLoopInterval.text.toString().toDoubleOrNull()
        if (intervalText == null || intervalText != config.settings.loopInterval) {
            etLoopInterval.setText(fmtNum(config.settings.loopInterval))
        }
        if (etCountdown.text.toString().toIntOrNull() != config.settings.countdown) {
            etCountdown.setText(config.settings.countdown.toString())
        }
    }

    // ---------------- 动作 ----------------

    private fun startPick(mode: PickOverlay.Mode) {
        when {
            !ClickService.isReady -> {
                toast(R.string.toast_need_accessibility)
                startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            }
            !Settings.canDrawOverlays(this) -> {
                toast(R.string.toast_need_overlay)
                startActivity(
                    Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))
                )
            }
            else -> {
                persist()
                ClickService.instance?.startPick(mode)
                moveTaskToBack(true)
            }
        }
    }

    private fun onPlayClicked() {
        when {
            !ClickService.isReady -> {
                toast(R.string.toast_need_accessibility)
                startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            }
            !Settings.canDrawOverlays(this) -> {
                toast(R.string.toast_need_overlay)
                startActivity(
                    Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))
                )
            }
            ClickService.isPlaying -> ClickService.stopAll()
            config.events.isEmpty() -> toast(R.string.toast_no_events)
            else -> {
                persist()
                moveTaskToBack(true)
                ClickService.instance?.startPlayback(ConfigStore.load(this))
            }
        }
    }

    private fun move(pos: Int, delta: Int) {
        val to = pos + delta
        if (to < 0 || to >= config.events.size) return
        config.events[pos] = config.events[to].also { config.events[to] = config.events[pos] }
        persist()
        refreshList()
    }

    private fun persist() {
        config.settings.loopMode = spinnerMode.selectedItemPosition
            .takeIf { it >= 0 } ?: config.settings.loopMode
        config.settings.loopCount = etLoopCount.text.toString().toIntOrNull()
            ?.coerceIn(1, 999999) ?: config.settings.loopCount
        config.settings.loopInterval = etLoopInterval.text.toString().toDoubleOrNull()
            ?.coerceAtLeast(0.0) ?: config.settings.loopInterval
        config.settings.countdown = etCountdown.text.toString().toIntOrNull()
            ?.coerceIn(0, 60) ?: config.settings.countdown
        ConfigStore.save(this, config)
    }

    // ---------------- 导入桌面端宏 ----------------

    private fun importFrom(uri: Uri) {
        try {
            val text = contentResolver.openInputStream(uri)?.bufferedReader()?.readText() ?: return
            val imported = MacroConfig.fromJson(JSONObject(text))
            // 桌面端宏没有屏幕元数据，仅在带 screen 信息时按比例换算
            val (w, h) = ConfigStore.screenSize(this)
            imported.rescale(imported.screenW, imported.screenH, w, h)
            val count = imported.events.size
            if (count == 0) {
                toast(R.string.toast_import_none)
                return
            }
            config.events.addAll(imported.events)
            persist(); refreshList()
            Toast.makeText(this, getString(R.string.toast_import_ok, count), Toast.LENGTH_SHORT).show()
        } catch (e: Exception) {
            toast(R.string.toast_import_fail)
        }
    }

    // ---------------- 工具 ----------------

    private fun toast(res: Int) = Toast.makeText(this, res, Toast.LENGTH_SHORT).show()

    private fun simpleWatcher(block: (CharSequence?) -> Unit): android.text.TextWatcher =
        object : android.text.TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) = Unit
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) = block(s)
            override fun afterTextChanged(s: android.text.Editable?) = Unit
        }

    private fun fmtNum(v: Double): String =
        if (v == v.toLong().toDouble()) v.toLong().toString() else v.toString()
}
