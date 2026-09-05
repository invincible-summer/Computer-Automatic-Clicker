# Windows 桌面端 · 设计文档（DESIGN.md）

> `src/MacroClicker` — .NET 10 WinForms，纯 Win32 API，零第三方依赖。

## 1. 总体架构

```
Program.cs            入口（DPI 感知 + 单实例运行）
MainForm.cs           主窗口：双 Tab 页面路由 / 工具栏 / 状态机 / 全局热键 / 设置持久化
├─ Tab「本机 Windows」 本机鼠标键盘录制回放（SendInput，会占用鼠标）
├─ Tab「模拟器 ADB」   独立完整页面：ADB 注入（不占鼠标），连接条 + 事件列表
Recorder.cs           录制引擎：全局钩子输入流 → 语义事件（本机/模拟器两种模式）
Player.cs             回放引擎：循环/倍速/暂停/急停；注入目标 = SendInput 或 ADB 会话
MacroEvent.cs         事件模型（含 target 语义与 coordSpace 坐标系）
MacroStore.cs         宏 JSON 库（macros/windows|emulator/）+ 界面设置持久化 + 旧版迁移
EventEditForm.cs      事件编辑对话框
UiTheme.cs            主题系统：深/浅色板 + 自绘按钮/卡片/列表/Tab/输入框
GlobalHook.cs         WH_KEYBOARD_LL / WH_MOUSE_LL 低级钩子（仅录制期间安装）
Simulator.cs          SendInput 输入模拟
KeyMap.cs             VK ↔ 可读名
Win32.cs              P/Invoke
Emulator/
├─ AdbClient.cs       adb.exe 子进程封装（连接/枚举/tap/swipe/keyevent/wm size）
├─ MuMuLocator.cs     MuMuManager.exe 定位 + `info -v all` 实例解析（端口/窗口句柄）
├─ EmulatorScanner.cs adb 发现（MuMu 自带→模拟器进程目录→PATH→SDK）+ 常见端口扫描 + 设备枚举
├─ EmulatorSession.cs 会话：serial/分辨率/ADB 注入；录制时「屏幕↔设备」坐标换算
└─ AndroidKeys.cs     按键名 → Android keycode
```

## 2. 双页面模型（v2 重构核心）

- **本机 Windows 页**：录制/回放真实本机输入。仅「抢鼠标」的操作在这一页发生。
- **模拟器 ADB 页**：完全独立的宏空间与录制回放路径，经 ADB 注入，本机鼠标全程空闲。
- 共享：顶部工具栏（录制/停录/执行/暂停/停止、打开宏、新建、当前宏名只读显示、保存、删除、清空、主题）、右侧设置面板（录制选项 + 执行设置）、状态栏、F6–F10 热键。
- 右侧面板与热键**作用于当前页面**；切页时先把面板值快照回离开的目标（`_curTarget` 跟踪），录制中切页自动停止录制。
- 同一时刻全局仅允许一种活动（录制或执行），由 `AppState` 状态机约束按钮可用性。

设计动机：旧版把模拟器卡片塞在右栏顶部导致内容被裁切；且两类宏（本机屏幕坐标 vs 设备坐标）混在一个列表里语义混乱。双页面从数据层（`macros/windows|emulator/`、`MacroTarget`）到 UI 彻底分离。

## 3. 事件与坐标系

- 事件类型：`mouse_click / mouse_down / mouse_up / move（仅拖拽轨迹）/ wheel / swipe / key / hotkey / wait`。
- `CoordSpace`：`null/"screen"` = 屏幕像素；`"device"` = 模拟器设备像素（录制时换算，回放直用，不受窗口移动/缩放影响）。
- `swipe` 为两端通用类型：起点/终点/时长（ms）；**起止点相同即长按**，与手机端宏完全互通。
- `delay` = 执行该事件前等待秒数（录制时取与上一事件的间隔），回放按 `delay / speed` 还原节奏。

## 4. 录制引擎（Recorder）

本机模式（继承 v1 语义）：
- 点击 = 按下+释放合并且位移小；拖拽 = 按住移动超阈值后落盘 `mouse_down` + 轨迹 `move` + `mouse_up`；
- 滚轮 ±120/格；键盘自动归并组合键（ctrl+c 等）；F6–F10 保留不录；本程序窗口内操作不录；注入输入（LLKHF_INJECTED）不录。
- **空闲鼠标移动录制已删除**（v2）：不做纯移动轨迹记录；滚轮（页面滚动）保留为可选项。

模拟器模式（v2 新增，「像 Windows 侧一样直接录制」）：
- 仅捕获落在模拟器窗口内的动作；窗口外手势整体忽略。
- 分类：按下→抬起，位移 ≤ 8px 且 <500ms → `点击(device)`；位移 ≤ 8px 且 ≥500ms → `长按(swipe 同点)`；否则 → `滑动(swipe)`，时长取真实按住时长。
- 滚轮 → 设备像素纵向 `滑动`（每格约 1/8 屏高，wheel-up = 手指下移），即「记录鼠标滚动带来的页面滚动」。
- 键盘（可选）：仅当模拟器窗口前台时录制，映射 Android keycode；组合键/修饰键/非左键提示后忽略。
- 坐标换算（`EmulatorSession.TryMapScreen`）：MuMu 实例优先用 `MuMuManager` 提供的**渲染子窗口**客户区精确映射；其他模拟器取光标处根窗口、校验其进程名 ∈ 已知模拟器进程表后用其客户区映射。每次事件实时换算 → 天然自适应窗口移动/缩放/不同分辨率。

## 5. 回放引擎（Player）

- 后台线程按 `delay/speed` 逐事件回放；支持一次/次数/无限、循环间隔、倒计时、暂停（暂停期间不计时）、F10 与左上角急停。
- 本机目标：SendInput（SetCursorPos + 按键/滚轮/组合键序列）。
- 模拟器目标：`adb input tap / swipe / keyevent`。设备掉线时重连一次，仍失败则**停止并报错，绝不盲点 (0,0)**。

## 6. 模拟器适配矩阵

| 模拟器 | 发现方式 | 默认端口 |
| --- | --- | --- |
| MuMu 12（V4.0.0+） | `MuMuManager.exe info -v all`（进程→默认目录→注册表三级定位） | 16384 + 32×n |
| MuMu 6 | MuMuManager / 端口扫描 | 7555 |
| 雷电 | 端口扫描 / dnplayer 进程目录 adb | 5555 + 2×n |
| 夜神 | 端口扫描 / Nox 进程目录 | 62001（多开 62025+） |
| 逍遥 | 端口扫描 / MEmu 进程目录 | 21503 |
| 蓝叠 / AVD / USB | `adb devices` 枚举 / 自定义 serial 输入 | 动态 |

约束：`adb connect` 到未监听端口立即失败，扫描代价可忽略；单次 input 约 200-500ms 延迟，建议事件间隔 ≥0.5s。

## 7. 宏库与设置持久化

- 宏按目标分目录：`macros/windows/*.json`、`macros/emulator/*.json`；`EnsureMigrated()` 幂等迁移旧平铺宏。
- **宏选择（v2.1 重构）**：工具栏不再放可编辑下拉框（旧方案“选中即弹确认框 + 输入联想改写文本”，换宏要反复确认且易卡）。现为——
  - 当前宏**只读独立显示**在工具栏（`未命名宏` 弱色 / 已命名高亮 + `*` 脏标记），宏名标签吃掉工具栏剩余宽度（窄窗口自动收缩省略，保证右侧按钮不被裁剪）。
  - 「打开宏」弹 `MacroPickerForm`：模糊搜索（前缀 > 包含 > 按序子序列，回车打开第一个）+ 打开/新建/重命名/删除；双击打开。对话框内的重命名/删除通过 `Renamed`/`Deleted` 回传，主窗体同步当前宏名。
  - 「新建」「保存（未命名时）」用 `InputDialog` 输入名称；保存重名确认覆盖；`MacroStore.Rename` 重写 JSON 保持文件名与内层 Name 一致。
  - **未保存修改守卫**：每目标 `dirty` 标记（任何事件变更置位，加载/新建/保存清零）；仅在 dirty 且列表非空时弹**一次**「保存/丢弃/取消」，切换宏不再连环确认；关窗时对两页统一兜底询问。
- `config.json`（AppSettings）：theme / win{…} / emu{…}（各自的循环、倍速、倒计时、急停、录制选项）/ emuSerial / 窗口尺寸。旧版扁平字段由 `LoadLegacySettings` 兼容读取。

## 7.1 模拟器连接条（v2.1 重构）

- 旧方案 330px 状态标签 + 330px 下拉 + 三按钮的 FlowLayoutPanel，窄窗口换行撑高，且「连接」「断开」大多数时间是灰的死按钮。
- 现为固定 46px 单行 `Panel`（底 1px 分隔线，`LayoutEmuStrip` 手工布局）：左侧状态圆点（● 绿=已连接所选设备 / 橙=检测或连接中 / 灰=未连接）+ 状态文字（吃剩余宽度，省略号），右侧设备下拉（250px，可手输 serial）+「⟳」检测 + **连接/断开合一按钮**（所选 serial 即当前已连接 → 显示「断开」，否则「连接」；adb 未就绪时点击自动转检测）。任何时刻无死按钮。

## 8. UI 体系

- `UiTheme`：深/浅双色板，自绘 `AppButton`（语义变体+悬停动效）、`AppCard`、`AppListView`（表头/行悬停/选中）、`FieldWrap` 输入框、TabControl 主题化（选中页签强调下划线）。
- 布局：工具栏(Top) + Tab 页(Fill，含模拟器连接条) + 右侧面板(Right, AutoScroll) + 状态栏(Bottom)；MinimumSize 1120×620，Aero 贴靠后钳回；窗口尺寸/最大化记忆并按屏幕工作区裁剪。
- 「清空」= 清空当前页事件列表（工具栏按钮 + 列表右键菜单「清空列表…」，二次确认，不影响已保存文件）。
- 全局热键 F6 录制 / F7 停录 / F8 执行 / F9 暂停继续 / F10 停止。

## 9. 已知边界

- 管理员权限窗口需以管理员运行本程序才能录制/回放。
- 模拟器录制依赖「渲染窗口与设备分辨率同构」的假设（MuMu 渲染窗口精确；通用模拟器以窗口客户区近似，边框比例不同会有细微偏差）。
- 组合键在模拟器端不支持（adb 无多键接口），录制时提示并跳过。
