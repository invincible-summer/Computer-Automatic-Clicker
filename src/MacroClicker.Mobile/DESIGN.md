# Android 手机端 · 设计文档（DESIGN.md）

> `src/MacroClicker.Mobile` — Kotlin + Material 3（AGP 8.7 / Kotlin 2.0 / minSdk 26 / targetSdk 35）。
> v4.0.0 重大重构：注入引擎**快速路径**（UserService 内直调 `InputManager.injectInputEvent`，毫秒级）+ `input` 命令兜底；UI 全面重设计为**底栏五页**（宏 / 编辑 / 录制 / 执行 / 设置）。**仅由 GitHub Actions 构建（`.github/workflows/android-apk.yml`），本地无需 Android SDK。**

## 1. 安全模型与方案依据（为什么是 Shizuku）

Android 安全模型下，普通应用向**其他应用**注入触摸事件只有三条路：

| 通道 | 权限要求 | 风险/限制 |
| --- | --- | --- |
| AccessibilityService.dispatchGesture | 用户开启无障碍 | 全系统级权限、被银行/风控盯防、Google 持续收紧政策（本项目 v1/v2 曾用，v3 起彻底移除，**永不再引入**） |
| ADB shell 注入 | adb / shell uid (2000) | 需要一次性引导启动授权桥 |
| root（sendevent/uinput） | root | 不考虑 |

v3 起选择第二条：经开源组件 **Shizuku**（RikkaApps，社区广泛使用与审计）以 **shell uid** 运行本应用的 UserService。v4 在同一通道内把执行方式从「每次 spawn 一个 `input` 进程」升级为「常驻进程内直调系统注入接口」，延迟从 0.3–0.5s 降到毫秒级（与 scrcpy 的服务端方案同源）。

安全性质：

- **不用无障碍权限、不读取任何屏幕内容**（服务里根本没有无障碍代码）；
- 快速路径是**固定签名的系统 API 调用**（`InputManager.injectInputEvent(MotionEvent, mode)`，坐标全为 int 数值），兜底路径是**纯 argv 数组**（二进制路径 + 子命令 + 数字坐标）——都不经任何 shell 解释器，无字符串注入面；快速路径甚至不创建任何子进程；
- 授权由用户在 Shizuku 中逐应用授予，**可随时撤销**；Shizuku 本身免 root（Android 11+ 无线调试配对启动，Android 8–10 需电脑一条 adb 命令）；
- shell uid 与 `adb shell` 同级，个别安全敏感界面（支付密码等）天然被系统屏蔽注入。

## 2. 模块结构

```
App.kt                       Application：Material 动态取色（Android 12+）+ Injector.init
model/Macro.kt               事件/配置模型 + JSON（与桌面端宏互通；长按 = 同点滑动）
store/MacroStore.kt          多宏库 filesDir/macros/<名>.json + 当前宏指针 + SAF 导入导出
aidl/…/inject/IInjectorService.aidl   Shizuku UserService 接口（probe/tap/swipe + destroy=16777114）
inject/InjectorServiceImpl.kt 运行于 Shizuku 进程（shell uid）：快速路径（反射 injectInputEvent）+ input 兜底
inject/Injector.kt           状态机（未安装/未运行/版本过旧/未授权/就绪）+ bindUserService + 能力探测
service/MacroService.kt      specialUse 前台服务：悬浮球/录制/回放宿主，通知(停止/退出)，失败即停
record/GestureRecorder.kt    完整动作录制：全屏标记层捕获 → 识别 点击/长按/滑动/等待 → 可选实时回放
overlay/FloatingBall.kt      悬浮球 + 控制面板（overlay_panel.xml 深色 M3 主题 inflate）
ui/MainActivity.kt           底栏五页主界面（宏/编辑/录制/执行/设置），Shizuku 引导，设置快照落盘
ui/MacrosAdapter.kt          宏库卡片列表（当前宏高亮 + ⋮ 菜单）
ui/EventsAdapter.kt          事件列表（点击编辑、上移/下移/删除）
ui/EditEventDialog.kt        事件编辑（ToggleGroup 类型切换动态字段）
res/                         Material 3：完整 M3 色板（浅/深）、menu/bottom_nav 五页、矢量图标
```

## 3. 注入引擎（inject/）

**双通道**，实现类在 Shizuku 启动的 shell uid 进程中实例化：

- **快速路径**：反射取得 `android.hardware.input.InputManager#getInstance()` 与 `injectInputEvent(MotionEvent, int)`（UserService 进程不受隐藏 API 限制——Shizuku 官方保证）。事件构造对照 AOSP `cmds/input/Input.java`：`SOURCE_TOUCHSCREEN`，DOWN 压力 1.0 / UP 压力 0.0，`INJECT_INPUT_EVENT_MODE_WAIT_FOR_FINISH(2)` 同步等待系统处理完成。tap = 同刻 DOWN+UP；swipe = DOWN + 按时长插值 MOVE（步长 ~20ms、上限 60 步）+ UP（eventTime 差 = 时长）；同点 swipe（长按）= DOWN + 等待 + UP，无 MOVE。
- **兼容路径**：`ProcessBuilder(*argv).redirectErrorStream(true)` 执行 `/system/bin/input tap|swipe`（固定 argv，15s 超时 destroyForcibly 返回 -124）。
- **回落规则**：快速路径整体不可用（反射失败，`probe()==0`）或**首个事件尚未注入**即失败 → 安全回落兼容路径；一旦已有事件注入成功则不回落（避免重复注入），返回错误由调用方停止会话。注入方法 `@Synchronized` 串行化。

**AIDL**（事务码全显式，全有或全无——v3 踩坑教训）：`int probe() = 1`、`int tap(int,int) = 2`、`int swipe(int,int,int,int,int) = 3`、`void destroy() = 16777114`（Shizuku 约定销毁事务码）。

**UserServiceArgs**：`processNameSuffix("injector").version(2).tag("macro-injector")`——v4 换了 AIDL，version 必须升（Shizuku 依 tag+version 决定复用/重建服务进程）。`bindUserService/unbindUserService` 由前台服务生命周期持有。

**Injector（单例状态机）**：`init()` 在 Application 注册 `addBinderReceivedListenerSticky/addBinderDeadListener`，状态每次 `refresh()` 重估（pingBinder → isPreV11 → checkSelfPermission），异常一律退化为「未运行」；状态变化回调主线程。连接成功即 `probe()` 探测能力，`fastMode` 供 UI 显示「快速注入 / 兼容模式」。`tap/swipe` 可在任意线程同步调用；Binder 尚未就位时会重绑一次重试（此场景不可能重复注入），已连接后的失败仅 refresh（binder 可能已死）由调用方停止——不盲目重试，杜绝双击。

## 4. 录制引擎（GestureRecorder）

流程：全屏 `TYPE_APPLICATION_OVERLAY` 标记层（极淡蒙层 alpha 0x08——远低于 Android 12「不可信触摸」0.8 阈值 + 顶部状态 pill）→ 用户连续完成整套操作 → 点悬浮球结束 → 事件序列替换当前宏。

手势识别（单指）：位移 ≤ 2×touchSlop 且时长 < longPressTimeout+100ms → **点击**；位移小而时长长 → **长按**（同点滑动，钳 ≤60s）；位移大 → **滑动**；手势间隔 → 下一事件 `delay`。多指整段忽略。

**回放同步（liveReplay，默认开）**：层上发生的手势不达应用，改为在单线程执行器上同步注入等效事件——注入前层置 `FLAG_NOT_TOUCHABLE`（注入事件按坐标命中下层应用），返回后恢复。v4 快速路径把穿透窗口从 ~0.5s 缩至毫秒级，物理触摸被漏记的时间窗大幅缩短。引擎未就绪时自动降级为纯标记录制（Toast 提示）。

边界：层覆盖整屏（`FLAG_LAYOUT_IN_SCREEN|NO_LIMITS`），坐标即屏幕像素；顶部避让高度 API 30+ 用 `currentWindowMetrics` 的 statusBars+displayCutout inset（26–29 回退系统资源 id）。

## 5. 回放引擎（MacroService · specialUse 前台服务）

- **普通前台服务**（不是无障碍服务）：`foregroundServiceType="specialUse"` + `FOREGROUND_SERVICE(_SPECIAL_USE)` 权限 + `PROPERTY_SPECIAL_USE_FGS_SUBTYPE` 说明；API 34+ 用 `ServiceCompat.startForeground(..., FOREGROUND_SERVICE_TYPE_SPECIAL_USE)`。通知小图标为自有矢量资源（v3 曾用平台图标）。
- 生命周期：录制/执行开始自动 `ensureStarted`（仅从前台 UI 发起，规避 Android 12+ FGS 限制，且 Android 15/16 起「可见悬浮窗」正是后台 FGS 豁免的判定条件，悬浮球常驻与服务保活天然互补）；「悬浮球常驻」开关决定会话结束后是否 `stopSelf`。通知（LOW 通道）常显状态，带 **停止 / 退出** 两个 action。
- 线程模型：回放在独立 `macro-player` 线程；每事件先按 `delay` 分片休眠（50ms 步进，可中断），再**同步**调用 `Injector.tap/swipe`；注入失败 → 通知/悬浮球提示并停止，**绝不盲点续跑**。Injector 状态监听：Shizuku binder 死亡（状态离开 READY）时若在回放，立即停止——断链即停。

## 6. 权限与引导

- **Shizuku 授权**：设置页三步引导（安装 → 启动（11+ 配对码 / 8–10 电脑 adb）→ 授权），教程含 OEM 注意（小米/ColorOS 后台弹出权限、重启后需重新启动）；宏页顶部常显引导卡直至就绪。
- **悬浮窗权限**（SYSTEM_ALERT_WINDOW）：悬浮球与标记层必需；录制强制要求，执行不强制（通知栏可停止）。
- **通知权限**（Android 13+ POST_NOTIFICATIONS）：`ActivityResultContracts.RequestPermission` 首启请求一次；拒绝仅影响通知可见（设置页显示状态），不影响功能。

## 7. UI 设计（Material 3 · 底栏五页）

- 主题 `Theme.Material3.DayNight.NoActionBar` + **完整 M3 品牌色板**（primary/secondary/tertiary + container 全套、outline/surfaceVariant，浅/深两套 colors.xml）；Android 12+ 动态取色覆盖；悬浮面板用强制深色子主题 `Theme.MacroClicker.Overlay`（任何系统主题下观感一致）。
- 结构：`MaterialToolbar` + `FrameLayout`（五个 `NestedScrollView` 页面切换）+ `BottomNavigationView`（**宏 / 编辑 / 录制 / 执行 / 设置**，labeled 模式 + 矢量图标）。
  - **宏页**：引导卡（未就绪时）→ 新建/导入按钮行 → 宏卡片列表（名称、动作数、修改时间、当前宏描边高亮 + 「使用中」徽章；点击设为当前，⋮ 菜单 = 设为当前/重命名/复制/导出/删除）→ 空态。
  - **编辑页**：当前宏摘要卡（名称 + 动作数 + 预计一轮时长）→ 事件列表（类型矢量图标 + 序号标题 + 副文本；点击编辑、上移/下移/删除）→ 添加动作 / 清空。
  - **录制页**：说明卡 + 回放同步开关（含引擎状态行）+ 大录制按钮。
  - **执行页**：当前宏卡 + 注入引擎徽章 → `MaterialButtonToggleGroup` 循环模式（一次/次数/无限，singleSelection）→ 次数/间隔/倒计时（Dense 文本框 + suffix）→ 大开始/停止按钮 + 节奏提示。
  - **设置页**：权限与连接卡（悬浮窗 / 通知 / Shizuku 三行状态 + 操作 + 教程）→ 注入引擎卡（当前模式 + 说明）→ 显示卡（悬浮球常驻）→ 关于卡。
- 自适应与边到边（v4 重点修复）：`WindowCompat.enableEdgeToEdge` + 分视图 insets 分发——工具栏吃 statusBars+displayCutout 顶、底栏吃 systemBars 底、内容区吃横屏 cutout 侧边与 `ime()` 底；列表 `clipToPadding=false`；权限请求全部走 ActivityResult API；预测性返回（manifest `enableOnBackInvokedCallback=true`，无自定义回退逻辑）。
- 悬浮球：矢量图标（播放/停止）、录制红/执行描边区分；位置按屏幕比例记忆，拖动与面板落点均避让系统导航栏（API 30+ currentWindowMetrics inset，旧版回退资源 id）；面板 `overlay_panel.xml` 以深色 M3 主题 inflate（MaterialButton tonal 四宫格：录制/执行/停止/主界面，标题行拖动、右上关闭）。

## 8. 宏模型与互通（与 v3 完全一致，向后兼容）

- 事件：`tap{x,y}` / `swipe{x,y,x2,y2,duration}`（同点=长按）/ `wait{delay}`；`delay`=执行前等待秒。
- 配置：`name` + `screen{w,h}` + `settings{loopMode,loopCount,loopInterval,countdown}` + `events[]`，`version:1`。
- 跨设备：加载/导入时按 `screen` 比例重算坐标；导入桌面端宏 `mouse_click→tap`、`swipe/wait` 直收（SAF 导入自动重名加序号、换算分辨率、设为当前；导出写 pretty JSON）。
- 多宏：`filesDir/macros/*.json`；SharedPreferences（`macro_store`）记当前宏、回放同步、悬浮球常驻——v3 用户数据无缝升级。

## 9. 数据完整性约定（沿用 v3 修复）

- 删除宏后必须 `loadCurrent()` 从磁盘真正加载剩余宏，否则空 config 会覆盖其文件（v2 数据丢失 bug）。
- `loadCurrent()` 在保存的当前宏不存在时回退到最近修改的宏（v2 幽灵宏 bug）。
- 适配器 `submit()` 一律先快照再全量刷新（调用方传入与内部持有同一列表引用，直接 clear+addAll 会清空数据；行内序号/当前宏高亮是位置敏感信息，全量刷新保证正确）。
- 设置快照：切页 / onPause / 开录 / 开执行前统一 `readSettingsFromUi() + save()`。

## 10. 构建与签名（仅 CI）

- `.github/workflows/android-apk.yml`：push 到 main（含 mobile 路径）或手动触发 → JDK17 + Gradle 8.9 → `assembleRelease`（minify 关闭）→ APK 重命名入 `publish/android/MacroClicker-vX.Y.Z-android.apk`（**先清空旧 APK**）→ 连同新生成的 `release.keystore` 自动提交回仓库 → artifact 上传。
- 依赖 `dev.rikka.shizuku:api|provider:13.1.5`（Maven Central，仅此一个运行时三方库——注入通道的必要依赖）。
- 固定签名密钥入库（工具类应用）：每次构建签名一致，可直接覆盖安装；本地无密钥时回退 debug 签名。
- GITHUB_TOKEN 产生的 `ci:` 提交不会再次触发工作流，无递归风险。

## 11. 已知边界

- **快速注入**毫秒级；**兼容模式**（`input` 命令）单次约 0.3–0.5s（新进程开销），事件间隔建议 ≥0.5s——引擎回落时设置页会显示当前模式。
- Shizuku 重启手机后需重新启动（Android 11+ 可开其「开机自启」）；未就绪时执行被拒绝并引导，录制降级为纯标记。
- Android 8–10 无无线调试，Shizuku 需电脑启动，每次重启后都要连一次电脑。
- 多指手势、状态栏/手势区触摸、物理键盘不录制（平台限制/低频场景）。
- 标记层在回放同步注入瞬间（快速路径毫秒级 / 兼容模式 ~0.5s）不可触摸，期间物理触摸直达应用而未被记录——录制 pill 常显提醒。
- 安全敏感界面（支付密码等）系统层面屏蔽注入，与 adb 行为一致。
