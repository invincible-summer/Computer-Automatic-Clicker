# AGENTS.md · 宏连点器仓库协作指南

本文件面向在本仓库工作的 AI 代理与贡献者，说明项目结构、构建方式与不可破坏的约定。

## 项目概览

跨平台自动化连点工具，一套「录制 → 编辑 → 循环回放」的设计思路、两个独立实现：

| 端 | 目录 | 技术栈 | 详细设计 |
| --- | --- | --- | --- |
| Windows 桌面端 | `src/MacroClicker` | .NET 10 WinForms，纯 Win32（钩子 + SendInput + ADB 子进程），零第三方依赖 | [src/MacroClicker/DESIGN.md](src/MacroClicker/DESIGN.md) |
| Android 手机端 | `src/MacroClicker.Mobile` | Kotlin + Material 3 底栏五页 UI；**无障碍手势派发注入（仅手势、不读屏幕），零第三方依赖** | [src/MacroClicker.Mobile/DESIGN.md](src/MacroClicker.Mobile/DESIGN.md) |

两端的宏 JSON 同构互通（`tap` / `swipe`（长按=同点滑动）/ `wait`；桌面端另有 `mouse_click` 等本机类型）。

## 构建与发布

**桌面端**（本机需要 .NET 10 SDK，`win32`）：

```bash
dotnet build src/MacroClicker -c Release
# 发布单文件 exe 到 publish/（产物随仓库分发）
dotnet publish src/MacroClicker -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

**手机端：只在 GitHub Actions 构建，本地不装任何 Android 依赖。**

- 工作流 `.github/workflows/android-apk.yml`：JDK 17 + Gradle 8.9 → `assembleRelease` → APK 写入 `publish/android/`（先清空旧 APK）并自动提交回仓库（`ci:` 前缀提交）。
- 签名：`src/MacroClicker.Mobile/release.keystore` 首次由 CI 生成并入库，之后每次构建复用，保证 APK 可直接覆盖安装。密钥口令见工作流（工具类应用，非机密）。
- 修改手机端后：`git push` → 等 Actions 完成 → `git pull` 即拿到新 APK。**不要手工生成/提交 APK。**

## 目录约定

- `publish/`：随仓库分发的成品（`MacroClicker.exe`、`android/*.apk`、`macros/` 示例宏）。**更新产物必须同步提交。**
- 运行期用户数据：桌面端写 exe 旁 `macros/`（按目标分 `windows/`、`emulator/` 子目录，首次运行自动迁移旧平铺宏）；手机端写 `filesDir/macros/`。均为本机数据不入库。
- `config.json`（桌面 `macros/` 下）为界面设置持久化，格式变更需在 `MainForm.LoadSettings`/`LoadLegacySettings` 做向后兼容。

## 关键平台事实（改动前必读）

1. **手机端注入通道 = 无障碍手势派发（v5.0 起，用户决策）**：仓库曾经历 无障碍（v1/v2）→ Shizuku（v3/v4）→ **无障碍回归（v5，用户明确要求最终 APK 不依赖 Shizuku）**。当前形态为**最小权限无障碍**：`canRetrieveWindowContent="false"`、不订阅任何无障碍事件（`onAccessibilityEvent` 空实现），注入只允许 `dispatchGesture` 派发固定路径 + 数字坐标手势；**不得读取屏幕内容、不得引入节点遍历/performAction 等屏幕感知、不得重新引入 Shizuku**。状态识别以服务 `onServiceConnected` 静态实例为准（三态：未开启/已开启待连接/就绪），派发同步等回调且超时保底（时长+15s），服务断开回放立即停止。
2. **Android 无法被动嗅探触摸屏**：`setMotionEventSources` 白名单不含 `SOURCE_TOUCHSCREEN`。手机端「完整录制」采用**全屏标记层捕获 + 无障碍实时回放**（录制层手势不同达应用，由注入引擎同步派发等效手势，层瞬时 `FLAG_NOT_TOUCHABLE` 放行；蒙层 alpha 须远低于 Android 12 不可信触摸 0.8 阈值），不要尝试改成钩子/嗅探方案。
3. **前台服务**：回放/录制宿主是 `specialUse` 前台服务（`FOREGROUND_SERVICE_SPECIAL_USE` + `PROPERTY_SPECIAL_USE_FGS_SUBTYPE`，API 34+ 用 `ServiceCompat.startForeground` 带类型）；只能从前台 UI 拉起（Android 12+ FGS 限制；Android 15/16 起「可见悬浮窗」是后台 FGS 豁免的判定条件）。注入另由无障碍服务承担（系统绑定，`BIND_ACCESSIBILITY_SERVICE`）。
4. **MuMu 12 官方接口**：`MuMuManager.exe`（安装目录 `shell\`，V4.0.0+），`info -v all` 返回实例 JSON（含 `adb_port`、`main_wnd`、`render_wnd`）。ADB 端口规则 16384 + 32×实例号（占用时 +1）；MuMu 6 为 7555。
5. **其他模拟器端口**：雷电 5555+2n、夜神 62001（多开 62025+）、逍遥 21503、蓝叠动态端口。桌面端 `EmulatorScanner` 按「MuMu 自带 adb → 模拟器进程目录 → PATH → SDK」顺序发现 adb。
6. **注入延迟与时限**：无障碍手势派发毫秒级/次；长按/滑动时长上限 60s，存储与执行两端都必须钳制；同步派发必须带超时保底（防回调丢失卡死，v2 教训）。

## 编码约定

- 桌面端：文件顶职责注释；UI 全部自绘主题控件（`UiTheme`），新增控件需适配深/浅色两套色板；不要引入第三方包。
- 手机端：字符串全部入 `strings.xml`（中文为默认语言）；布局只用 dp/sp + 权重 + 嵌套滚动（适配小屏）；底栏五页导航（宏/编辑/录制/执行/设置）+ Material 动态取色 + 边到边 insets 分视图分发（工具栏吃状态栏+挖孔、底栏吃系统栏、内容区吃 ime）；权限请求一律 ActivityResult API；**运行时零第三方依赖**（仅 androidx/material 官方库）。
- 两端共同：宏事件模型字段变更必须同步另一端的 JSON 解析（`MacroStore.cs` ↔ `Macro.kt`）并保持向后兼容（未知/缺失字段回退默认值）；注入命令永远使用固定参数数组，不得拼接 shell 字符串。
- 提交信息用中文，简述「改了什么」；发布产物更新的提交注明产物版本。

## 安全红线

- 工具仅用于合法自动化场景；手机端无障碍服务为**最小权限形态**：`canRetrieveWindowContent="false"`、不订阅任何无障碍事件、注入只允许 `dispatchGesture` 固定坐标手势；**不得读取屏幕内容、不得引入屏幕感知/节点操作、不得以任何理由放宽该配置**；README/应用内保留该声明。
- 注入只允许 `dispatchGesture` 派发固定路径 + 数字坐标手势（tap/swipe），永远不得拼接 shell 字符串、不得引入任意命令执行或驱动级注入；不引入检测规避、反作弊绕过类功能。
