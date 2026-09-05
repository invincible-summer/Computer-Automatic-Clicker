# 宏连点器 · Macro Clicker

跨平台自动化连点工具，一套「录制 → 编辑 → 循环回放」的设计思路、两个平台：

- **Windows 桌面端**（`src/MacroClicker`）：双页面设计——**本机 Windows 页**录制/回放真实鼠标键盘；**模拟器 ADB 页**是独立完整的模拟器控制台（MuMu/雷电/夜神/逍遥/蓝叠…），经 ADB 注入输入、**完全不占用本机鼠标**。.NET 10 + WinForms，纯 Win32 API，零第三方依赖，深/浅色主题。
- **Android 手机端**（`src/MacroClicker.Mobile`）：**完整连续动作录制**——像平时一样操作手机，点击、滑动、长按与操作间隔自动识别成动作序列（与桌面端体验一致），一键循环执行。Kotlin + Material 3，无障碍手势注入，悬浮球随时控制。

> 深入了解实现：[桌面端 DESIGN.md](src/MacroClicker/DESIGN.md) · [手机端 DESIGN.md](src/MacroClicker.Mobile/DESIGN.md) · 仓库协作约定见 [AGENTS.md](AGENTS.md)。

## 手机版（Android）

### 功能

- **完整动作录制**：点「开始录制」后直接操作手机即可——**点击 / 滑动 / 长按 / 等待（操作间隔）** 自动识别成事件序列，无需逐个动作添加
- **回放同步**（默认开）：录制时每个手势会同步注入真实应用，界面真实响应，可以录「点按钮 → 等弹窗 → 再点」的多步流程；也可关闭做纯演示录制
- **多宏管理**：宏下拉切换、新建 / 重命名 / 复制 / 删除，事件可编辑（坐标/时长/间隔）、上移下移
- **循环执行**：一次 / 指定次数 / 无限循环，轮次间隔 + 开始倒计时
- **悬浮球**：贴边可拖动、位置记忆；展开面板即可 录制 / 执行 / 停止，录制中点球即完成保存
- **自适应与主题**：Material 3 + 动态取色（Android 12+），深浅色跟随系统；布局自适应小屏；宏内记录分辨率，跨机型导入自动按比例换算
- **宏互通**：与桌面端宏 JSON 同构（`mouse_click→tap`，`swipe`/长按/`wait` 直接收）

### 安装使用

1. 下载 APK（两种方式任选）：
   - **仓库离线包**：`publish/android/MacroClicker-v2.0.0-android.apk`，直接随仓库分发
   - **云构建最新版**：GitHub **Actions** → `Android APK` 工作流产物（CI 每次构建后自动把 APK 提交回仓库，签名固定，可直接覆盖安装）
2. 安装到手机（需允许「安装未知应用」），打开 App 完成两项授权：
   - **无障碍服务**：手势注入必需（服务**不读取任何屏幕内容**）
   - **悬浮窗权限**：悬浮球与录制层必需
3. 点「开始录制」→ App 退到后台 → 像平时一样完成整套操作 → 点悬浮球 ■ 结束
4. 悬浮球面板点「▶ 执行」即可循环回放，「■ 停止」随时停止

> 录制原理说明：Android 出于安全不允许应用被动监听触摸屏，本应用采用「全屏录制层捕获 + 无障碍手势实时回放穿透」方案实现完整录制，无需 root。

### 源码构建（仅云构建）

手机端**只通过 GitHub Actions 构建**，本地无需安装任何 Android 依赖：

```bash
git push   # 触发 .github/workflows/android-apk.yml
# Actions 完成后 APK 自动提交到 publish/android/，git pull 即得
```

## Windows 桌面端

### 双页面

| 页面 | 输入方式 | 坐标 | 适用 |
| --- | --- | --- | --- |
| 🖥 **本机 Windows** | 全局钩子录制 / SendInput 回放（占用鼠标） | 屏幕像素 | 普通桌面/网页自动化 |
| 📱 **模拟器 (ADB)** | 在模拟器窗口上直接录制 / ADB 注入回放（**不占鼠标**） | 设备像素（自适应窗口） | 模拟器挂机，可同时用电脑干别的 |

顶部工具栏、右侧「录制选项 / 执行设置」面板与 F6–F10 热键作用于当前页面。

### 本机页功能

- **宏录制**：鼠标点击（左/右/中/侧键）、拖拽轨迹、滚轮（页面滚动）、键盘与组合键（`Ctrl+C` 等）；不录制纯鼠标移动
- **间隔记录**：每事件记录与上一事件的间隔，回放还原真实节奏
- **事件编辑**：双击列表行修改（间隔/坐标/按键/时长等），支持删除、上移下移、复制、插入新事件
- **循环执行**：一次 / 指定次数 / 无限循环 + 循环间隔；倍速 0.25x–8x；播放前倒计时；鼠标甩左上角或 F10 紧急停止

### 模拟器页功能（MuMu / 各类模拟器）

- **自动发现**：连接条「⟳」一键检测——MuMu 12 走官方 `MuMuManager.exe`（精确到实例、ADB 端口与渲染窗口句柄，16384+32×n）；雷电（5555+2n）、夜神（62001+）、逍遥（21503）、MuMu6（7555）、蓝叠/AVD/USB 经 adb 扫描发现；也可手动输入 serial（如 `127.0.0.1:5555`）
- **直接录制**（不再截图取点）：在模拟器窗口内**正常操作即录制**——点击→tap、按住拖动→滑动（含真实时长）、按住不动→长按、滚轮→页面滚动、按键→Android 键码；坐标按渲染窗口实时换算为设备像素，窗口移动/缩放/换分辨率都不影响回放
- **ADB 注入回放**：`input tap / swipe / keyevent`，本机鼠标完全空闲；设备掉线自动重连，失败即停（绝不盲点）
- **宏独立**：模拟器宏与本机宏分开存储（`macros/emulator/`），两页各自管理

### 宏管理（两端同思路）

工具栏**当前宏只读显示**（未保存修改带 `*` 标记）；「**打开宏**」弹出宏库对话框——**模糊搜索**（支持拼音首字母式的按序匹配）、双击/回车载入，还可新建、重命名、删除；「**新建**」「**保存**」在需要时输入名称（重名会确认覆盖）；「**清空**」一键清空当前事件列表。切换宏只会在有未保存修改时询问一次（保存 / 丢弃 / 取消），关窗前统一兜底，不再弹文件对话框，也不记忆上次打开的文件名。宏文件为可读 JSON，位于 exe 旁 `macros/windows|emulator/` 目录（旧版平铺宏首次运行自动迁移）。

### 全局热键

| 按键 | 功能 |
| --- | --- |
| `F6` | 开始 / 停止录制（当前页面） |
| `F7` | 停止录制 |
| `F8` | 开始执行（暂停中则为继续） |
| `F9` | 暂停 / 继续 |
| `F10` | 停止一切（录制与执行） |

热键不会被录进宏。循环执行卡住时，`F10` 或把鼠标甩到**屏幕左上角**即可急停。

### 从源码构建（桌面端）

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（仅开发时需要，成品 exe 自包含运行时）：

```bash
dotnet run --project src/MacroClicker                 # 调试运行
dotnet publish src/MacroClicker -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

## 项目结构

```
src/
├── MacroClicker/                # Windows 桌面端（.NET 10 WinForms，零依赖）
│   ├── MainForm.cs              # 主窗口：双 Tab 页面路由 / 状态机 / 热键 / 设置
│   ├── Recorder.cs              # 录制引擎（本机模式 + 模拟器窗口录制模式）
│   ├── Player.cs                # 回放引擎（SendInput / ADB 双后端）
│   ├── Emulator/                # AdbClient / MuMuLocator / EmulatorScanner / EmulatorSession
│   ├── MacroStore.cs            # 宏库（按目标分目录）+ 设置持久化 + 旧版迁移
│   ├── MacroPickerForm.cs       # 宏库选择对话框（模糊搜索/新建/重命名/删除）+ 输入框
│   └── UiTheme.cs               # 深浅色主题 + 全套自绘控件
└── MacroClicker.Mobile/         # Android 手机端（Kotlin + Material 3，仅 CI 构建）
    └── app/src/main/java/com/macroclicker/mobile/
        ├── model/Macro.kt       # 事件/配置模型（与桌面端 JSON 互通）
        ├── store/MacroStore.kt  # 多宏持久化 + 分辨率换算
        ├── service/MacroService.kt    # 无障碍服务：注入 + 回放引擎（无主线程阻塞）
        ├── record/GestureRecorder.kt  # 完整动作录制层 + 实时回放穿透
        ├── overlay/FloatingBall.kt    # 悬浮球 + 控制面板
        └── ui/                   # Material 3 主界面 / 事件列表 / 编辑对话框
```

## 宏文件格式

```json
{
  "name": "我的宏",
  "version": 2,
  "target": "windows",
  "events": [
    { "type": "mouse_click", "button": "left", "x": 800, "y": 450, "delay": 0 },
    { "type": "key", "key": "enter", "delay": 0.8 },
    { "type": "swipe", "x": 540, "y": 1500, "x2": 540, "y2": 500, "duration": 350,
      "delay": 0.5, "coordSpace": "device" },
    { "type": "wait", "delay": 1.0 }
  ]
}
```

- `target`：`windows` / `emulator`（桌面端宏库按此分目录；旧宏缺省视为 windows）
- `delay` = 执行该事件前需等待的秒数；回放时按倍速缩放
- `coordSpace: "device"` = 模拟器/手机设备像素（跨分辨率按比例换算）
- 事件类型：`mouse_click` / `mouse_down` / `mouse_up` / `move`（拖拽轨迹）/ `wheel` / `swipe`（起止点相同即长按）/ `key` / `hotkey` / `wait`；手机端使用 `tap`（等价 `mouse_click`）/ `swipe` / `wait`

## 注意事项

- 本机页点击使用屏幕绝对坐标，回放前保持目标窗口位置与录制时一致；模拟器页使用设备坐标，不受窗口位置影响
- 单次 `adb input` 约有 200-500ms 延迟，模拟器宏事件间隔建议 ≥ 0.5s；首次连接失败请确认模拟器已开启「ADB 调试」
- 管理员权限窗口中的操作可能无法被录制/回放，必要时以管理员身份运行本程序
- 部分游戏使用驱动级输入或反作弊系统，可能拦截模拟输入（手机端同理，部分 App 会屏蔽无障碍手势）
- 请勿将本工具用于违反目标平台规则的场景
