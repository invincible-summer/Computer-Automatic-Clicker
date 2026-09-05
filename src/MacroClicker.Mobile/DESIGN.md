# Android 手机端 · 设计文档（DESIGN.md）

> `src/MacroClicker.Mobile` — Kotlin + Material 3（AGP 8.7 / Kotlin 2.0 / minSdk 26 / targetSdk 35）。
> v5.0.0：**注入通道回归无障碍服务**（用户决策：最终 APK 不依赖 Shizuku）——以**最小权限形态**使用无障碍：`canRetrieveWindowContent=false`、不订阅任何事件，服务只能派发坐标手势，**读不了屏幕**。UI 为底栏五页（宏 / 编辑 / 录制 / 执行 / 设置）。**仅由 GitHub Actions 构建（`.github/workflows/android-apk.yml`），本地无需 Android SDK。**

## 1. 安全模型与方案依据（为什么是无障碍手势派发）

Android 安全模型下，普通应用向**其他应用**注入触摸事件只有三条路：

| 通道 | 权限要求 | 历史使用 |
| --- | --- | --- |
| AccessibilityService.dispatchGesture | 用户在系统设置开启 | **v1/v2 曾用（识别/卡死问题多），v3/v4 换 Shizuku，v5 按用户决策回归并彻底重构** |
| Shizuku（ADB shell uid） | 安装并启动第三方组件 | v3/v4 使用，v5 移除（用户要求不依赖） |
| root（sendevent/uinput） | root | 不考虑 |

v5 的无障碍用法与常见「无障碍连点器」有本质区别——**最小权限形态**：

- `canRetrieveWindowContent="false"`：服务**读不到任何屏幕内容**（无节点树、无文本、无控件信息）；
- **不订阅任何无障碍事件**（`onAccessibilityEvent` 空实现、配置不声明 eventTypes）：用户在别的应用里做什么，本服务完全不可知；
- 唯一能力 = `dispatchGesture`：把**固定路径 + 数字坐标**的手势（录制自用户本人操作）派发到屏幕，不存在任何「感知→决策」链路；
- 用户可随时在系统无障碍设置关闭即撤销；工具仅用于合法自动化场景。

相对 Shizuku 的取舍：免安装/启动第三方组件、免配对引导、重启手机后服务一般自动恢复；代价是受各 OEM 无障碍管理策略影响（见 §11），以三态状态机与引导文本兜底。

## 2. 模块结构

```
App.kt                       Application：Material 动态取色（Android 12+）+ Injector.init
model/Macro.kt               事件/配置模型 + JSON（与桌面端宏互通；长按 = 同点滑动）
store/MacroStore.kt          多宏库 filesDir/macros/<名>.json + 当前宏指针 + SAF 导入导出
inject/InjectorService.kt    无障碍服务（shell 无关）：仅 dispatchGesture 派发 tap/swipe，同步等回调+超时保底
inject/Injector.kt           三态状态机（未开启/已开启待连接/就绪）+ 派发入口
service/MacroService.kt      specialUse 前台服务：悬浮球/录制/回放宿主，通知(停止/退出)，失败即停
record/GestureRecorder.kt    完整动作录制：全屏标记层捕获 → 识别 点击/长按/滑动/等待 → 可选实时回放
overlay/FloatingBall.kt      悬浮球 + 控制面板（overlay_panel.xml 深色 M3 主题 inflate）
ui/MainActivity.kt           底栏五页主界面（宏/编辑/录制/执行/设置），无障碍开启引导，设置快照落盘
ui/MacrosAdapter.kt          宏库卡片列表（当前宏高亮 + ⋮ 菜单）
ui/EventsAdapter.kt          事件列表（点击编辑、上移/下移/删除）
ui/EditEventDialog.kt        事件编辑（ToggleGroup 类型切换动态字段）
res/xml/accessibility_service_config.xml   最小权限无障碍配置
res/                         Material 3：完整 M3 色板（浅/深）、menu/bottom_nav 五页、矢量图标
```

## 3. 注入引擎（inject/）

**InjectorService**（`BIND_ACCESSIBILITY_SERVICE`，仅系统可绑定）：

- tap = 同点短笔画（50ms，路径含一段 lineTo 保证各系统识别）；swipe = 起点→终点直线路径、时长即录制时长（系统插值触摸轨迹）；同点 swipe（长按）= 同点长时长笔画。duration 全部钳制 50–60000ms。
- **同步派发**：`dispatchGesture` + `GestureResultCallback` + CountDownLatch 阻塞等待——回放/录制的「一步完成再走下一步」语义不变；**超时保底 = 手势时长 + 15s**，回调丢失（v2 卡死根因）也不会无限等待，超时按失败处理。
- 可从任意线程调用（回放线程 / 录制回放执行器）；派发内部串行。

**Injector（单例三态状态机）**：

- `READY`：`InjectorService.instance != null`（onServiceConnected 置位）——以服务真实连接为准，这是 v1/v2「设置里开了但应用识别不到/反之」问题的根治点；
- `WAITING`：系统「已启用服务」列表里有本服务但尚未连接（刚开启/OEM 延迟/服务被杀）——单独成态，UI 提示等待或引导重开；
- `NOT_ENABLED`：列表里没有——引导去系统设置开启。
- 状态变化经 onServiceConnected/onUnbind/onDestroy → `refresh()` → 主线程回调；MacroService 监听：**离开 READY 且在回放 → 立即停止**（断链即停，绝不盲点续跑）；MainActivity 回前台 refresh + 2s 延迟复检一次。

## 4. 录制引擎（GestureRecorder）

流程：全屏 `TYPE_APPLICATION_OVERLAY` 标记层（极淡蒙层 alpha 0x08——远低于 Android 12「不可信触摸」0.8 阈值 + 顶部状态 pill）→ 用户连续完成整套操作 → 点悬浮球结束 → 事件序列替换当前宏。

手势识别（单指）：位移 ≤ 2×touchSlop 且时长 < longPressTimeout+100ms → **点击**；位移小而时长长 → **长按**（同点滑动，钳 ≤60s）；位移大 → **滑动**；手势间隔 → 下一事件 `delay`。多指整段忽略。

**回放同步（liveReplay，默认开）**：层上发生的手势不达应用，改为在单线程执行器上同步派发等效手势——派发前层置 `FLAG_NOT_TOUCHABLE`（派发事件按坐标命中下层应用），返回后恢复。无障碍派发毫秒级，穿透窗口极小。服务未就绪时自动降级为纯标记录制（Toast 提示）。

边界：层覆盖整屏（`FLAG_LAYOUT_IN_SCREEN|NO_LIMITS`），坐标即屏幕像素；顶部避让高度 API 30+ 用 `currentWindowMetrics` 的 statusBars+displayCutout inset（26–29 回退系统资源 id）。

## 5. 回放引擎（MacroService · specialUse 前台服务）

- **普通前台服务**（注入另由无障碍服务承担）：`foregroundServiceType="specialUse"` + `FOREGROUND_SERVICE(_SPECIAL_USE)` 权限 + `PROPERTY_SPECIAL_USE_FGS_SUBTYPE` 说明；API 34+ 用 `ServiceCompat.startForeground(..., FOREGROUND_SERVICE_TYPE_SPECIAL_USE)`。通知小图标为自有矢量资源。
- 生命周期：录制/执行开始自动 `ensureStarted`（仅从前台 UI 发起，规避 Android 12+ FGS 限制，且 Android 15/16 起「可见悬浮窗」正是后台 FGS 豁免的判定条件，悬浮球常驻与服务保活天然互补）；「悬浮球常驻」开关决定会话结束后是否 `stopSelf`。通知（LOW 通道）常显状态，带 **停止 / 退出** 两个 action。
- 线程模型：回放在独立 `macro-player` 线程；每事件先按 `delay` 分片休眠（50ms 步进，可中断），再**同步**调用 `Injector.tap/swipe`（阻塞到手势回调）；派发失败或无障碍服务断开 → 通知/悬浮球提示并停止，**绝不盲点续跑**。

## 6. 权限与引导

- **无障碍服务**：设置页三行状态卡（未开启/待连接/就绪）+「去开启/重新检测」按钮 + 教程对话框（开启路径、OEM 后台保活提示、「等待连接」的解释）；宏页顶部常显引导卡直至就绪。`startPlayback` 前置 READY 校验，录制在未就绪时降级纯标记。
- **悬浮窗权限**（SYSTEM_ALERT_WINDOW）：悬浮球与标记层必需；录制强制要求，执行不强制（通知栏可停止）。
- **通知权限**（Android 13+ POST_NOTIFICATIONS）：`ActivityResultContracts.RequestPermission` 首启请求一次；拒绝仅影响通知可见（设置页显示状态），不影响功能。

## 7. UI 设计（Material 3 · 底栏五页）

- 主题 `Theme.Material3.DayNight.NoActionBar` + **完整 M3 品牌色板**（primary/secondary/tertiary + container 全套、outline/surfaceVariant，浅/深两套 colors.xml）；Android 12+ 动态取色覆盖；悬浮面板用强制深色子主题 `Theme.MacroClicker.Overlay`（任何系统主题下观感一致）。
- 结构：`MaterialToolbar` + `FrameLayout`（五个 `NestedScrollView` 页面切换）+ `BottomNavigationView`（**宏 / 编辑 / 录制 / 执行 / 设置**，labeled 模式 + 矢量图标）。
  - **宏页**：引导卡（未就绪时）→ 新建/导入按钮行 → 宏卡片列表（名称、动作数、修改时间、当前宏描边高亮 + 「使用中」徽章；点击设为当前，⋮ 菜单 = 设为当前/重命名/复制/导出/删除）→ 空态。
  - **编辑页**：当前宏摘要卡（名称 + 动作数 + 预计一轮时长）→ 事件列表（类型矢量图标 + 序号标题 + 副文本；点击编辑、上移/下移/删除）→ 添加动作 / 清空。
  - **录制页**：说明卡 + 回放同步开关（含服务状态行）+ 大录制按钮。
  - **执行页**：当前宏卡 + 注入引擎徽章 → `MaterialButtonToggleGroup` 循环模式（一次/次数/无限）→ 次数/间隔/倒计时（Dense 文本框 + suffix）→ 大开始/停止按钮 + 节奏提示。
  - **设置页**：权限与连接卡（悬浮窗 / 通知 / 无障碍三行状态 + 操作 + 教程）→ 注入引擎卡（当前状态 + 安全说明）→ 显示卡（悬浮球常驻）→ 关于卡。
- 自适应与边到边：`WindowCompat.setDecorFitsSystemWindows(window, false)`（Android 15+ 系统强制边到边无需设置）+ 分视图 insets 分发——工具栏吃 statusBars+displayCutout 顶、底栏吃 systemBars 底、内容区吃横屏 cutout 侧边与 `ime()` 底；列表 `clipToPadding=false`；权限请求全部走 ActivityResult API；预测性返回（manifest `enableOnBackInvokedCallback=true`）。
- 悬浮球：矢量图标（播放/停止）、录制红/执行描边区分；位置按屏幕比例记忆，拖动与面板落点均避让系统导航栏（API 30+ currentWindowMetrics inset，旧版回退资源 id）；面板 `overlay_panel.xml` 以深色 M3 主题 inflate（MaterialButton tonal 四宫格：录制/执行/停止/主界面，标题行拖动、右上关闭）。

## 8. 宏模型与互通（与 v3 起完全一致，向后兼容）

- 事件：`tap{x,y}` / `swipe{x,y,x2,y2,duration}`（同点=长按）/ `wait{delay}`；`delay`=执行前等待秒。
- 配置：`name` + `screen{w,h}` + `settings{loopMode,loopCount,loopInterval,countdown}` + `events[]`，`version:1`。
- 跨设备：加载/导入时按 `screen` 比例重算坐标；导入桌面端宏 `mouse_click→tap`、`swipe/wait` 直收（SAF 导入自动重名加序号、换算分辨率、设为当前；导出写 pretty JSON）。
- 多宏：`filesDir/macros/*.json`；SharedPreferences（`macro_store`）记当前宏、回放同步、悬浮球常驻——旧版本用户数据无缝升级。

## 9. 数据完整性约定

- 删除宏后必须 `loadCurrent()` 从磁盘真正加载剩余宏，否则空 config 会覆盖其文件（v2 数据丢失 bug）。
- `loadCurrent()` 在保存的当前宏不存在时回退到最近修改的宏（v2 幽灵宏 bug）。
- 适配器 `submit()` 一律先快照再全量刷新（调用方传入与内部持有同一列表引用；行内序号/当前宏高亮是位置敏感信息）。
- 设置快照：切页 / onPause / 开录 / 开执行前统一 `readSettingsFromUi() + save()`。

## 10. 构建与签名（仅 CI）

- `.github/workflows/android-apk.yml`：push 到 main（含 mobile 路径）或手动触发 → JDK17 + Gradle 8.9 → `assembleRelease`（minify 关闭）→ APK 重命名入 `publish/android/MacroClicker-vX.Y.Z-android.apk`（**先清空旧 APK**）→ 连同新生成的 `release.keystore` 自动提交回仓库 → artifact 上传。
- **运行时零第三方依赖**（v5 移除 Shizuku 后）：仅 androidx / material 官方库。
- 固定签名密钥入库（工具类应用）：每次构建签名一致，可直接覆盖安装；本地无密钥时回退 debug 签名。

## 11. 已知边界

- 无障碍手势派发毫秒级，回放节奏基本由事件 `delay` 决定；长按/滑动时长与录制一致（上限 60s）。
- 无障碍服务通常重启手机后自动恢复；但 OEM 省电策略可能杀服务 → 「已开启但未连接」（WAITING），按提示重开开关即可；小米/华为等建议允许后台运行。
- 多指手势、状态栏/手势区触摸、物理键盘不录制（平台限制/低频场景）。
- 标记层在回放同步派发瞬间（毫秒级）不可触摸，期间物理触摸直达应用而未被记录——时间窗极小，录制 pill 常显提醒。
- 安全敏感界面（支付密码等）部分仍允许手势到达（无障碍手势与 adb 不同，系统不再额外屏蔽），请勿在支付等敏感场景使用本工具。
