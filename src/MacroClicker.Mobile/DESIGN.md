# Android 手机端 · 设计文档（DESIGN.md）

> `src/MacroClicker.Mobile` — Kotlin + Material 3（AGP 8.7 / Kotlin 2.0 / minSdk 26 / targetSdk 35）。
> v3.0.0 重大重构：**彻底移除无障碍服务**，注入改经 Shizuku（ADB shell 权限），全新底栏四页 UI。**仅由 GitHub Actions 构建（`.github/workflows/android-apk.yml`），本地无需 Android SDK。**

## 1. 安全模型与方案依据（为什么是 Shizuku）

Android 安全模型下，普通应用向**其他应用**注入触摸事件只有三条路：

| 通道 | 权限要求 | 风险/限制 |
| --- | --- | --- |
| AccessibilityService.dispatchGesture | 用户开启无障碍 | 全系统级权限、被银行/风控盯防、Google 持续收紧政策 |
| ADB shell `input tap/swipe` | adb / shell uid (2000) | 需要一次性引导启动授权桥 |
| root（sendevent/uinput） | root | 不考虑 |

v3.0 选择第二条：经开源组件 **Shizuku**（RikkaApps，社区广泛使用与审计）以 **shell uid** 运行本应用的 UserService，执行固定参数的 `/system/bin/input tap|swipe`。这也是开源连点器 Klick'r（Smart AutoClicker）采用的「无障碍替代」路线。

安全性质：

- **不用无障碍权限、不读取任何屏幕内容**（无 canRetrieveWindowContent 概念，服务里根本没有无障碍代码）；
- 命令为**纯 argv 数组**（二进制路径 + 子命令 + 数字坐标），不经任何 shell 解释器，无字符串注入面；
- 授权由用户在 Shizuku 中逐应用授予，**可随时撤销**；Shizuku 本身免 root（Android 11+ 无线调试配对启动，Android 8–10 需电脑一条 adb 命令）；
- shell uid 与 `adb shell` 同级，个别安全敏感界面（支付密码等）天然被系统屏蔽注入。

## 2. 模块结构

```
App.kt                    Application：Material 动态取色（Android 12+）+ ShellExecutor.init
model/Macro.kt            事件/配置模型 + JSON（与桌面端宏互通；长按 = 同点滑动）
store/MacroStore.kt       多宏库 filesDir/macros/<名>.json + 当前宏指针 + 界面开关（回放同步/悬浮球常驻）
aidl/…/shell/IShellService.aidl   Shizuku UserService 接口（exec + destroy=16777114）
shell/ShellServiceImpl.kt 运行于 Shizuku 进程（shell uid）：ProcessBuilder 执行固定 argv，15s 超时
shell/ShellExecutor.kt    状态机（未安装/未运行/版本过旧/未授权/就绪）+ bindUserService + tap/swipe
service/MacroService.kt   specialUse 前台服务：悬浮球/录制/回放宿主，通知(停止/退出)，失败即停
record/GestureRecorder.kt 完整动作录制：全屏标记层捕获 → 识别 点击/长按/滑动/等待 → 可选 shell 实时回放
overlay/FloatingBall.kt   悬浮球 + 控制面板（录制/执行/停止/主界面；录制中点球即存）
ui/MainActivity.kt        底栏四页主界面（宏/录制/执行/设置），Shizuku 引导，设置快照落盘
ui/EventsAdapter.kt       事件列表（点击编辑、上移/下移/删除）
ui/EditEventDialog.kt     事件编辑（类型切换动态字段）
res/                      Material 3：menu/bottom_nav、tab 图标、深浅色、动态取色
```

## 3. 注入后端（shell/）

- **AIDL**：`int exec(in String[] cmd)` + `void destroy() = 16777114`（Shizuku 约定的销毁事务码）。实现类在 Shizuku 启动的 shell uid 进程中实例化，`ProcessBuilder(*cmd).redirectErrorStream(true)`、`waitFor(15s)` 超时 `destroyForcibly` 返回 -124。
- **UserServiceArgs**：`processNameSuffix("shell").version(1).tag("macro-shell")`；`bindUserService/unbindUserService` 由前台服务生命周期持有。
- **ShellExecutor（单例状态机）**：`init()` 在 Application 注册 `addBinderReceivedListenerSticky/addBinderDeadListener`，状态每次 `refresh()` 重估（pingBinder → isPreV11 → checkSelfPermission），异常一律退化为「未运行」；状态变化回调主线程。`tap/swipe` 只拼 argv，任何 Binder 异常 → refresh + 返回 false。
- 调用方约定：`exec` 可在任意线程同步调用（回放线程/录制执行器），UI 永不调用。

## 4. 录制引擎（GestureRecorder）

流程不变：全屏 `TYPE_APPLICATION_OVERLAY` 标记层（极淡蒙层 + 顶部状态 pill）→ 用户连续完成整套操作 → 点悬浮球结束 → 事件序列替换当前宏。

手势识别（单指）：位移 ≤ 2×touchSlop 且时长 < longPressTimeout+100ms → **点击**；位移小而时长长 → **长按**（同点滑动，钳 ≤60s）；位移大 → **滑动**；手势间隔 → 下一事件 `delay`。多指整段忽略。

**回放同步（liveReplay，默认开）**：层上发生的手势不达应用，改为在单线程执行器上同步执行等效 `input tap/swipe`——注入前层置 `FLAG_NOT_TOUCHABLE`（注入事件按坐标命中下层应用），命令返回后恢复可触摸。相比 v2 的无障碍异步回调，同步语义更简单且天然串行。Shizuku 未就绪时自动降级为纯标记录制（Toast 提示），停止录制后执行器 `shutdownNow`。

边界：层覆盖整屏（`FLAG_LAYOUT_IN_SCREEN|NO_LIMITS`），坐标即屏幕像素，与 `input` 坐标系一致；系统状态栏/手势区触摸不达层（平台限制）。

## 5. 回放引擎（MacroService · specialUse 前台服务）

- v2 的无障碍服务改为**普通前台服务**：`foregroundServiceType="specialUse"` + `FOREGROUND_SERVICE(_SPECIAL_USE)` 权限 + `PROPERTY_SPECIAL_USE_FGS_SUBTYPE` 说明；API 34+ 用 `ServiceCompat.startForeground(..., FOREGROUND_SERVICE_TYPE_SPECIAL_USE)`。
- 生命周期：录制/执行开始自动 `ensureStarted`（仅从前台 UI 发起，规避 Android 12+ FGS 限制）；「悬浮球常驻」开关决定会话结束后是否 `stopSelf`。通知（LOW 通道）常显状态，带 **停止 / 退出** 两个 action。
- 线程模型：回放在独立 `macro-player` 线程；每事件先按 `delay` 分片休眠（50ms 步进，可中断），再**同步**调用 `ShellExecutor.tap/swipe`（Binder 到 shell 进程，单次约 0.3–0.5s）；注入失败 → 状态栏/通知提示并停止，绝不盲点续跑。
- 录制结束：`MacroStore.loadCurrent` → 替换 events → 保存；服务空闲且非常驻则自毁。
- 服务销毁：停播放/停录制/移除悬浮球/解绑 UserService。

## 6. 手势注入与权限

- **Shizuku 授权**：设置页承载三步引导（安装 → 启动（11+ 配对码 / 8–10 电脑 adb）→ 授权）；主界面宏页顶部常显引导卡直至就绪。
- **悬浮窗权限**（SYSTEM_ALERT_WINDOW，设置页开关跳转）：悬浮球与标记层必需；录制强制要求，执行不强制（通知栏可停止）。
- **通知权限**（Android 13+ POST_NOTIFICATIONS）：首启请求一次；拒绝仅影响通知可见，不影响功能。

## 7. UI 设计（Material 3 · 底栏四页）

- 主题 `Theme.Material3.DayNight.NoActionBar` + 品牌蓝；Android 12+ 动态取色，深浅色跟随系统。
- 结构：`MaterialToolbar` + `FrameLayout`（四个 `NestedScrollView` 页面切换）+ `BottomNavigationView`（**宏 / 录制 / 执行 / 设置**，labeled 模式 + 矢量图标）。
  - **宏页**：引导卡（未就绪时）→ 当前宏卡（名称 + 切换单选列表 + 新建/重命名/复制/删除）→ 事件序列卡（列表 + 计数 + 手动添加/清空）。
  - **录制页**：说明 + 回放同步开关 + Shizuku 就绪提示行 + 大按钮。
  - **执行页**：模式 Chip（一次/次数/无限）+ 次数/间隔/倒计时输入 + 开始/停止大按钮 + 节奏提示。
  - **设置页**：权限与连接（悬浮窗行 + Shizuku 行 + 教程对话框）→ 显示（悬浮球常驻开关）→ 关于。
- 自适应：全部 dp/sp + 权重 + 嵌套滚动；边到边由根 insets 监听统一处理（根吃状态栏、底栏吃导航栏、内容区吃输入法 ime），小屏完整可滚动。
- 悬浮球：可拖动、位置按屏幕比例记忆；面板四按钮；录制中变红、点球即完成保存。

## 8. 宏模型与互通（与 v2 一致）

- 事件：`tap{x,y}` / `swipe{x,y,x2,y2,duration}`（同点=长按）/ `wait{delay}`；`delay`=执行前等待秒。
- 配置：`name` + `screen{w,h}` + `settings{loopMode,loopCount,loopInterval,countdown}` + `events[]`。
- 跨设备：加载时按 `screen` 比例重算坐标；导入桌面端宏 `mouse_click→tap`、`swipe/wait` 直收。
- 多宏：`filesDir/macros/*.json`；SharedPreferences 记当前宏、回放同步、悬浮球常驻。

## 9. 设置持久化与 v2 遗留问题修复（v3.0）

- **修复数据丢失 bug**：v2 删除宏后把内存 config 置为「只剩名字的空宏」，onPause 落盘会用空事件**覆盖磁盘上剩余宏文件**；v3 删除后 `loadCurrent()` 从磁盘真正加载内容。
- **修复幽灵宏**：`loadCurrent()` 在保存的当前宏不存在时回退到最近修改的宏（v2 会凭空造出「宏 1」并在下次落盘写出文件）。
- **设置快照**：切换底栏任意页 / onPause / 开录 / 开执行前统一 `readSettingsFromUi() + save()`，杜绝输入框内容丢失。
- 事件编辑回调、录制结束重载沿用 v2 逻辑。

## 10. 构建与签名（仅 CI）

- `.github/workflows/android-apk.yml`：push 到 main（含 mobile 路径）或手动触发 → JDK17 + Gradle 8.9 → `assembleRelease`（minify 关闭）→ APK 重命名入 `publish/android/MacroClicker-vX.Y.Z-android.apk`（**先清空旧 APK**）→ 连同新生成的 `release.keystore` 自动提交回仓库 → artifact 上传。
- 依赖 `dev.rikka.shizuku:api|provider:13.1.5`（Maven Central，仅此一个运行时三方库——注入通道的必要依赖）。
- 固定签名密钥入库（工具类应用）：每次构建签名一致，可直接覆盖安装；本地无密钥时回退 debug 签名。
- GITHUB_TOKEN 产生的 `ci:` 提交不会再次触发工作流，无递归风险。

## 11. 已知边界

- 单次 `input` 注入约 0.3–0.5s（新进程开销），事件间隔建议 ≥0.5s；高频连点场景不适合本方案。
- Shizuku 重启手机后需重新启动（Android 11+ 可开其「开机自启」）；未就绪时执行被拒绝并引导，录制降级为纯标记。
- Android 8–10 无无线调试，Shizuku 需电脑启动，每次重启后都要连一次电脑。
- 多指手势、状态栏/手势区触摸、物理键盘不录制（平台限制/低频场景）。
- 标记层在回放同步注入瞬间（约 0.5s）不可触摸，期间物理触摸直达应用而未被记录——录制 pill 常显提醒，属可接受折衷。
- 安全敏感界面（支付密码等）系统层面屏蔽注入，与 adb 行为一致。
