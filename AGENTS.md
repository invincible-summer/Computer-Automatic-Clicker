# AGENTS.md · 宏连点器仓库协作指南

本文件面向在本仓库工作的 AI 代理与贡献者，说明项目结构、构建方式与不可破坏的约定。

## 项目概览

跨平台自动化连点工具，一套「录制 → 编辑 → 循环回放」的设计思路、两个独立实现：

| 端 | 目录 | 技术栈 | 详细设计 |
| --- | --- | --- | --- |
| Windows 桌面端 | `src/MacroClicker` | .NET 10 WinForms，纯 Win32（钩子 + SendInput + ADB 子进程），零第三方依赖 | [src/MacroClicker/DESIGN.md](src/MacroClicker/DESIGN.md) |
| Android 手机端 | `src/MacroClicker.Mobile` | Kotlin + Material 3，AccessibilityService 手势注入 | [src/MacroClicker.Mobile/DESIGN.md](src/MacroClicker.Mobile/DESIGN.md) |

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

- 工作流 `.github/workflows/android-apk.yml`：JDK 17 + Gradle 8.9 → `assembleRelease` → APK 写入 `publish/android/` 并自动提交回仓库（`ci:` 前缀提交）。
- 签名：`src/MacroClicker.Mobile/release.keystore` 首次由 CI 生成并入库，之后每次构建复用，保证 APK 可直接覆盖安装。密钥口令见工作流（工具类应用，非机密）。
- 修改手机端后：`git push` → 等 Actions 完成 → `git pull` 即拿到新 APK。**不要手工生成/提交 APK。**

## 目录约定

- `publish/`：随仓库分发的成品（`MacroClicker.exe`、`android/*.apk`、`macros/` 示例宏）。**更新产物必须同步提交。**
- 运行期用户数据写入 exe 旁的 `macros/`（按目标分 `windows/`、`emulator/` 子目录，首次运行自动迁移旧平铺宏），属于本机数据不入库（`.gitignore` 已忽略）。
- `config.json`（`macros/` 下）为界面设置持久化，格式变更需在 `MainForm.LoadSettings`/`LoadLegacySettings` 做向后兼容。

## 关键平台事实（改动前必读）

1. **Android 无法被动嗅探触摸屏**：`AccessibilityServiceInfo.setMotionEventSources` 白名单不含 `SOURCE_TOUCHSCREEN`；`TouchInteractionController` 要求接管触摸流。因此手机端「完整录制」采用**全屏录制层 + dispatchGesture 实时回放穿透**方案（见手机端 DESIGN.md），不要尝试改成钩子/嗅探方案。
2. **MuMu 12 官方接口**：`MuMuManager.exe`（安装目录 `shell\`，V4.0.0+），`info -v all` 返回实例 JSON（含 `adb_port`、`main_wnd`、`render_wnd`）。ADB 端口规则 16384 + 32×实例号（占用时 +1）；MuMu 6 为 7555。
3. **其他模拟器端口**：雷电 5555+2n、夜神 62001（多开 62025+）、逍遥 21503、蓝叠动态端口。桌面端 `EmulatorScanner` 按「MuMu 自带 adb → 模拟器进程目录 → PATH → SDK」顺序发现 adb。
4. **adb input 延迟**：单次 `input tap/swipe` 约 200-500ms，模拟器宏事件间隔建议 ≥0.5s；`input swipe` 时长上限 60s。
5. **无障碍手势**：`GestureDescription` 单笔画时长上限 60s（`MAX_STROKE_MS`），播放端必须钳制。

## 编码约定

- 桌面端：文件顶职责注释；UI 全部自绘主题控件（`UiTheme`），新增控件需适配深/浅色两套色板；不要引入第三方包。
- 手机端：字符串全部入 `strings.xml`（中文为默认语言）；布局只用 dp/sp + 权重 + 嵌套滚动（适配小屏）；主题跟随系统 + Material 动态取色；不新增运行时依赖除非必要。
- 两端共同：宏事件模型字段变更必须同步另一端的 JSON 解析（`MacroStore.cs` ↔ `Macro.kt`）并保持向后兼容（未知/缺失字段回退默认值）。
- 提交信息用中文，简述「改了什么」；发布产物更新的提交注明产物版本。

## 安全红线

- 工具仅用于合法自动化场景；README 与无障碍服务描述中保留「不读取屏幕内容（`canRetrieveWindowContent=false`）」的声明，不得改为 true。
- 不引入检测规避、驱动级输入、反作弊绕过类功能。
