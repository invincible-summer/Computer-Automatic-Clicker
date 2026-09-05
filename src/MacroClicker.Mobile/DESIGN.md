# Android 手机端 · 设计文档（DESIGN.md）

> `src/MacroClicker.Mobile` — Kotlin + Material 3（AGP 8.7 / Kotlin 2.0 / minSdk 26 / targetSdk 35）。
> v2.0.0 完全重写：完整连续动作录制 + 全新 UI。**仅由 GitHub Actions 构建（`.github/workflows/android-apk.yml`），本地无需 Android SDK。**

## 1. 为什么不能「嗅探」录制——方案依据

Android 安全模型禁止无障碍服务被动监听触摸屏（AOSP 事实，改动前必读）：

- `AccessibilityServiceInfo.setMotionEventSources()` 的白名单（`@MotionEventSources`）**不含 `SOURCE_TOUCHSCREEN`**；
- `AccessibilityService.onMotionEvent` 明确「若本服务或任一服务启用触摸浏览，touchscreen 事件不会送达」；
- `TouchInteractionController`（API 34）虽可观察触摸，但要求 `FLAG_REQUEST_TOUCH_EXPLORATION_MODE` 接管触摸流——正常点击会被拦截，无法「边正常操作边录」。

因此本应用采用 **全屏录制层 + dispatchGesture 实时回放穿透**：这是无 root 下唯一可行的完整录制方案。

## 2. 模块结构

```
App.kt                    Application：Material 动态取色（Android 12+）
model/Macro.kt            事件/配置模型 + JSON（与桌面端宏互通；长按 = 同点滑动）
store/MacroStore.kt       多宏库 filesDir/macros/<名>.json + 当前宏指针 + 回放同步开关
service/MacroService.kt   无障碍服务：手势构建/注入（主线程派发+超时保底）、
                          回放引擎（独立线程）、录制引擎宿主、状态广播
record/GestureRecorder.kt 完整动作录制：全屏层捕获 → 识别 点击/长按/滑动/等待 → 可选实时回放
overlay/FloatingBall.kt   悬浮球 + 控制面板（录制/执行/停止/主界面；录制中点球即存）
ui/MainActivity.kt        主界面（权限引导 / 多宏管理 / 录制 / 执行设置）
ui/EventsAdapter.kt       事件列表（点击编辑、上移/下移/删除）
ui/EditEventDialog.kt     事件编辑（类型切换动态字段）
res/                      Material 3 布局与资源（dp/sp、深浅色、动态取色）
```

## 3. 录制引擎（GestureRecorder）

流程：`开始录制` → 悬浮层（TYPE_APPLICATION_OVERLAY 全屏可触摸 + 顶部状态 pill）出现 → 用户**连续完成整套操作** → 点悬浮球结束 → 事件序列替换当前宏。

手势识别（单指）：
- `DOWN … UP`，位移 ≤ 2×touchSlop 且时长 < longPressTimeout+100ms → **点击**；
- 位移 ≤ slop 且时长 ≥ 阈值 → **长按**（存为同点滑动，时长=真实按住时长，钳制 ≤60s）；
- 位移 > slop → **滑动**（起点→终点，时长=真实滑动时长）；
- 手势间隔 → 下一事件的 `delay`（等待语义，与桌面端一致）；
- 多指手势：整段忽略 + Toast 提示。

**回放同步（liveReplay，默认开）**：每个手势录制完成后立即 `dispatchGesture` 注入真实应用，让界面真实响应，从而支持「点按钮→等弹窗→再点」的多步流程录制。注入前把录制层临时置 `FLAG_NOT_TOUCHABLE` 放行（注入手势落在层下方的应用上），回调完成后恢复可触摸。关闭该开关则为纯演示录制（操作只被记录、不作用于界面）。

边界：层覆盖整个屏幕（`FLAG_LAYOUT_IN_SCREEN`），坐标即屏幕坐标；系统状态栏/导航手势区的触摸不达层（系统吞掉），属平台限制。

## 4. 回放引擎（MacroService）

- 事件快照 + 设置（循环模式/次数/间隔/倒计时）交给 `macro-player` 线程；
- 每个手势：构建 `GestureDescription`（单笔画，时长钳 50–60000ms）→ `mainHandler.post(dispatchGesture)` → 回放线程 `CountDownLatch.await(duration+2s)`，**主线程永不阻塞**（v1 卡死的根因即阻塞主线程等待回调）；
- `dispatchAsync` 自带超时保底（回调被系统吞掉时也能恢复），回调以 CAS 防重复；
- 停止：volatile 标志 + 线程中断，50ms 分片可中断休眠；暂停期间不计时。

## 5. 手势注入与权限

- 无障碍服务 `canPerformGestures=true`、`canRetrieveWindowContent=false`（**不读取任何屏幕内容**，声明见 strings.xml）。
- 悬浮球/录制层需「显示在其他应用上层」权限；手势执行需无障碍服务已连接。
- 主界面权限卡实时显示两项状态，全部授权后自动隐藏。

## 6. UI 设计（Material 3）

- 主题 `Theme.Material3.DayNight.NoActionBar` + 品牌蓝 `#4F6BFF`；Android 12+ 动态取色（`DynamicColors`），深浅色跟随系统。
- 页面自上而下：**权限卡** → **宏与事件卡**（宏下拉 + 新建/重命名/复制/删除 + 事件列表）→ **完整动作录制卡**（说明 + 回放同步开关 + 大按钮）→ **循环执行卡**（模式 Chip + 次数/间隔/倒计时 + 开始/停止大按钮）。
- 自适应：全部 dp/sp + 权重；根布局 `NestedScrollView`（事件列表 `nestedScrollingEnabled=false`）；边到边（targetSdk 35 强制）以 systemBars inset 设根内边距；小屏完整可滚动。
- 悬浮球：可拖动、位置按屏幕比例记忆；面板四按钮（录制/执行/停止/主界面）+ 状态行；录制中变红、点球即完成保存。

## 7. 宏模型与互通

- 事件：`tap{x,y}` / `swipe{x,y,x2,y2,duration}`（同点=长按）/ `wait{delay}`；`delay`=执行前等待秒。
- 配置：`name` + `screen{w,h}`（保存时分辨率）+ `settings{loopMode,loopCount,loopInterval,countdown}` + `events[]`。
- 跨设备：加载时按 `screen` 比例重算坐标；导入桌面端宏时 `mouse_click→tap`、`swipe/wait` 直收。
- 多宏：`filesDir/macros/*.json`，SharedPreferences 记当前宏与回放同步开关。

## 8. 构建与签名（仅 CI）

- `.github/workflows/android-apk.yml`：push 到 main（含 mobile 路径）或手动触发 → JDK17 + Gradle 8.9 → `assembleRelease`（minify 关闭）→ APK 重命名入 `publish/android/MacroClicker-vX.Y.Z-android.apk`（先清空旧 APK）→ 连同新生成的 `release.keystore` 自动提交回仓库 → artifact 上传。
- 固定签名密钥入库（工具类应用）：每次构建签名一致，可直接覆盖安装；本地无密钥时回退 debug 签名。
- GITHUB_TOKEN 产生的 `ci:` 提交不会再次触发工作流，无递归风险。

## 9. 已知边界

- 多指手势、状态栏/手势区触摸、物理键盘不录制（平台限制/低频场景）。
- 回放同步开启时，注入进行中（层短暂不可触摸）用户的物理触摸会直达应用而未被记录——录制 pill 常显提醒，属可接受折衷。
- 单笔画时长上限 60s（`GestureDescription` 限制），超长长按被钳制。
- 部分应用会屏蔽无障碍手势（安全策略），无法在此类应用内执行。
